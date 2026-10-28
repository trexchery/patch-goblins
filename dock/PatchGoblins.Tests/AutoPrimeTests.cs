using System.Text.Json.Nodes;
using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

// All accounts and credentials here are synthetic.
public class AutoPrimeTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 18, 15, 0, 0, TimeSpan.Zero);

    static Account A(int n, double five = 0, DateTimeOffset? resets = null, double weekly = 10, string status = "ok", bool disabled = false,
        bool active = false, Spend? spend = null, DateTimeOffset? fetched = null, List<Window>? scoped = null, string provider = "claude") =>
        new Account(n, $"a{n}@example.com", active, status,
            status == "ok" ? new Usage(new Window(five, resets), new Window(weekly, null), scoped, spend) : null,
            LastGoodUsage: status == "ok" ? null : new Usage(new Window(0, null), new Window(1, null), null, null),
            Disabled: disabled, OrganizationUuid: "org")
        { UsageFetchedAt = fetched ?? Now - TimeSpan.FromMinutes(1), Provider = provider };

    [Fact]
    public void ReadyMeansAFreshUnstartedWindowWellWithinThePlansLimits()
    {
        using var tmp = new TempDir();
        var p = new Primer(Path.Combine(tmp.Path, "prime.json"));
        var accounts = new[]
        {
            A(1),
            A(2, five: 3), // in use
            A(3, resets: Now.AddHours(2)), // started
            A(4, disabled: true), // excluded from auto-switch
            A(5, status: "api_key"), // no subscription: would bill
            A(6, status: "token_expired"), // only a last-known reading
            A(7, weekly: 96), // near the weekly limit: past it, extra usage could bill
            A(8, scoped: [new Window(100, null, "Opus")]),
            A(9, spend: new Spend(4, 50, 8, "USD", null)), // paid extra usage on
            A(10, fetched: Now.AddMinutes(-40)), // someone may have started it since
            A(11, provider: "codex"),
        };
        Assert.Equal([1], p.Ready(accounts, Now).Select(a => a.Number));
        // At the click, only a reading taken just now counts.
        Assert.Single(p.Ready([A(1, fetched: Now.AddMinutes(-10))], Now));
        Assert.Empty(p.Ready([A(1, fetched: Now.AddMinutes(-10))], Now, Primer.SendReadingAge));
    }

    [Fact]
    public void EachWindowIsPrimedOnceAcrossRestartsAndTheNextOneAgain()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "prime.json");
        var p = new Primer(path);
        Assert.True(p.Observe([A(1, five: 40, resets: Now.AddMinutes(-2), fetched: Now.AddMinutes(-10))], Now)); // learns the window that just ended
        var fresh = A(1, fetched: Now);
        Assert.Single(p.Ready([fresh], Now));
        Assert.True(p.Begin(fresh, Now)); // written before the request: a crash right after never sends twice
        Assert.Empty(new Primer(path).Ready([fresh], Now.AddMinutes(1)));
        p.Finish(fresh, PrimeResult.Sent, "sent");
        Assert.Empty(p.Ready([fresh], Now.AddMinutes(2)));

        // Nobody saw the primed window (the laptop slept through it): once it's surely over, fresh again.
        Assert.Single(new Primer(path).Ready([A(1, fetched: Now.AddHours(5).AddMinutes(1))], Now.AddHours(5).AddMinutes(2)));

        // The new window shows up, ends, and the one after it is fresh again.
        p.Observe([A(1, five: 1, resets: Now.AddHours(5), fetched: Now.AddMinutes(4))], Now.AddMinutes(4));
        Assert.Empty(p.Ready([A(1, fetched: Now.AddHours(4))], Now.AddHours(4).AddMinutes(1))); // read before that window ended
        Assert.Single(p.Ready([A(1, fetched: Now.AddHours(5).AddMinutes(2))], Now.AddHours(5).AddMinutes(3)));
    }

    [Fact]
    public void SuccessNeedsEvidenceThatTheWindowStarted()
    {
        using var tmp = new TempDir();
        var p = new Primer(Path.Combine(tmp.Path, "prime.json"));
        var (one, two, three) = (A(1), A(2), A(3));
        foreach (var a in new[] { one, two, three })
        {
            p.Begin(a, Now);
            p.Finish(a, PrimeResult.Sent, "sent");
        }
        Assert.Equal("sent", p.Records["a1@example.com|org"].Outcome);

        // A reading from before the request proves nothing.
        p.Observe([A(1, five: 2, resets: Now.AddHours(5), fetched: Now.AddMinutes(-1))], Now.AddMinutes(1));
        Assert.Equal("sent", p.Records["a1@example.com|org"].Outcome);

        p.Observe([
            A(1, five: 2, resets: Now.AddHours(5), fetched: Now.AddMinutes(4)), // rounded to the hour or not: started by the prime
            A(2, five: 30, resets: Now.AddHours(1), fetched: Now.AddMinutes(4)), // someone was already using it
        ], Now.AddMinutes(4));
        Assert.Equal("verified", p.Records["a1@example.com|org"].Outcome);
        Assert.Equal("in use", p.Records["a2@example.com|org"].Outcome);
        p.Observe([], Now.AddMinutes(31)); // never showed the new window
        Assert.Equal("unverified", p.Records["a3@example.com|org"].Outcome);
        Assert.Contains("#3 unverified: no reading showed the new window", p.Status([one, two, three], Now.AddMinutes(31)));
        Assert.Contains(p.Status([one, two, three], Now.AddMinutes(31)), l => l.StartsWith("Last primed"));
    }

    [Fact]
    public void AFailedPrimeIsReportedAndCanBeTriedAgain()
    {
        using var tmp = new TempDir();
        var p = new Primer(Path.Combine(tmp.Path, "prime.json"));
        var a = A(4);
        p.Begin(a, Now);
        p.Finish(a, PrimeResult.Refused, "login expired: run cswap login 4");
        Assert.Contains("#4 failed: login expired: run cswap login 4", p.Status([a], Now));
        Assert.Empty(p.Ready([a], Now.AddMinutes(1))); // the reading from before the attempt may be stale now
        Assert.Single(p.Ready([A(4, fetched: Now.AddMinutes(2))], Now.AddMinutes(3)));
        Assert.Null(p.LastPrime); // nothing went out
    }

    [Fact]
    public void AnUnclearPrimeIsNeverRetriedUntilEvidenceOrTheWindowIsOver()
    {
        using var tmp = new TempDir();
        var p = new Primer(Path.Combine(tmp.Path, "prime.json"));
        var (one, two) = (A(1), A(2));
        foreach (var a in new[] { one, two })
        {
            p.Begin(a, Now);
            p.Finish(a, PrimeResult.Unknown, "engine timed out"); // the provider may have taken it
        }
        Assert.Contains("#1 unknown: engine timed out", p.Status([one], Now));
        Assert.Empty(p.Ready([A(1, fetched: Now.AddMinutes(2)), A(2, fetched: Now.AddMinutes(2))], Now.AddMinutes(3))); // not "failed": no retry
        Assert.True(p.WantsReread(Now.AddMinutes(4)));

        p.Observe([A(1, five: 1, resets: Now.AddHours(5), fetched: Now.AddMinutes(4))], Now.AddMinutes(4)); // it did go out
        Assert.Equal("verified", p.Records["a1@example.com|org"].Outcome);
        p.Observe([], Now.AddMinutes(31));
        Assert.Equal("unverified", p.Records["a2@example.com|org"].Outcome);
        Assert.Empty(p.Ready([A(2, fetched: Now.AddHours(2))], Now.AddHours(2)));
        Assert.Single(p.Ready([A(2, fetched: Now.AddHours(5).AddMinutes(1))], Now.AddHours(5).AddMinutes(2))); // that window is surely over
    }

    [Fact]
    public void TheVerificationRereadIsKeptUntilAReadingComesBack()
    {
        using var tmp = new TempDir();
        var p = new Primer(Path.Combine(tmp.Path, "prime.json"));
        p.Begin(A(1), Now);
        p.Finish(A(1), PrimeResult.Sent, "sent");
        Assert.False(p.WantsReread(Now.AddMinutes(1))); // the engine's cached reading can't show it yet
        Assert.True(p.WantsReread(Now.AddMinutes(4)));
        Assert.True(p.WantsReread(Now.AddMinutes(5))); // that re-read failed: still wanted

        p.Reread(Now.AddMinutes(5)); // this one came back, but with a reading from before the prime
        p.Observe([A(1, fetched: Now.AddMinutes(-1))], Now.AddMinutes(5));
        Assert.Equal("sent", p.Records["a1@example.com|org"].Outcome);
        Assert.False(p.WantsReread(Now.AddMinutes(6))); // no faster than the engine's cache
        Assert.True(p.WantsReread(Now.AddMinutes(9)));

        p.Reread(Now.AddMinutes(9));
        p.Observe([A(1, five: 1, resets: Now.AddHours(5), fetched: Now.AddMinutes(9))], Now.AddMinutes(9));
        Assert.Equal("verified", p.Records["a1@example.com|org"].Outcome);
        Assert.False(p.WantsReread(Now.AddMinutes(20)));
    }

    [Fact]
    public void ALedgerThatCantBeReadOrSavedBlocksPriming()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "prime.json");

        // Unwritable: Begin says so and leaves no trace, nothing is ready until a save works again.
        Directory.CreateDirectory(path);
        var p = new Primer(path);
        Assert.False(p.Begin(A(1), Now));
        Assert.Null(p.Records["a1@example.com|org"].Window);
        Assert.StartsWith("can't save the prime ledger", p.Problem);
        Assert.Empty(p.Ready([A(1)], Now));
        Assert.Contains(p.Status([A(1)], Now), l => l.StartsWith("Paused: can't save"));
        Directory.Delete(path);
        Assert.True(p.Observe([], Now)); // the failed save is tried again
        Assert.True(p.Save());
        Assert.Single(p.Ready([A(1)], Now));

        // Corrupt: blocked, across a restart too, and left as it is for the owner to fix or delete.
        File.WriteAllText(path, "{ not json");
        foreach (var restarted in new[] { new Primer(path), new Primer(path) })
        {
            Assert.Contains(path, restarted.Problem);
            Assert.Empty(restarted.Ready([A(1)], Now));
            Assert.False(restarted.Begin(A(1), Now));
            Assert.False(restarted.Observe([A(1, resets: Now.AddHours(1))], Now));
            Assert.False(restarted.Save());
        }
        Assert.Equal("{ not json", File.ReadAllText(path));
        p = new Primer(path);
        File.Delete(path);
        p.Observe([], Now); // deleted: priming carries on
        Assert.Null(p.Problem);
        Assert.Single(p.Ready([A(1)], Now));
    }

    [Fact]
    public void APrimeIsOneQuietTurnOnTheSubscriptionLoginOnly()
    {
        string[] tail = ["-p", "Hi", "--model", "haiku", "--effort", "low", "--output-format", "json",
            "--no-session-persistence", "--safe-mode", "--strict-mcp-config", "--disable-slash-commands", "--setting-sources", "project", "--tools", ""];
        // The signed-in account: its own login, no copy and no switch. Any other: its sealed profile only.
        Assert.Equal(["run", "1", "--", .. tail], Engine.PrimeArgs(A(1, active: true)));
        Assert.Equal(["run", "2", "--require-session", "--", .. tail], Engine.PrimeArgs(A(2)));
        Assert.DoesNotContain("--bare", Engine.PrimeArgs(A(2))); // API-key auth only: would bill
        Assert.DoesNotContain("--no-share", Engine.PrimeArgs(A(2)));

        var unset = Engine.PrimeUnset(["ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK",
            "CLAUDE_CONFIG_DIR", "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_GIT_BASH_PATH", "PATH"]);
        Assert.Equal(["ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_OAUTH_TOKEN",
            "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CONFIG_DIR"], unset.Order(StringComparer.Ordinal)); // Git Bash's location and PATH stay
    }

    [Fact]
    public void TheUsageReadingsTimeIsRead()
    {
        var snap = Json.Parse<Snapshot>("""{"schemaVersion":1,"accounts":[{"number":1,"email":"a@example.com","active":true,"usageStatus":"ok","usageFetchedAt":"2026-10-18T15:00:00+00:00"}]}""");
        Assert.Equal(Now, snap.Accounts[0].UsageFetchedAt);
    }
}

[Collection("agents end to end")] // changes process environment variables
public sealed class AutoPrimeEngineTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly Dictionary<string, string?> _was = [];

    void Set(string name, string? value)
    {
        if (!_was.ContainsKey(name)) _was[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    [Fact]
    public async Task PrimeSendsOnceSilentlyWithoutTheTraysCredentialsOrSession()
    {
        var log = Path.Combine(_tmp.Path, "cswap.log");
        Set("FAKE_LOG", log);
        Set("PATCH_GOBLINS_DATA", _tmp.Path);
        Set("ANTHROPIC_API_KEY", "sk-ant-api03-SYNTHETIC"); // must never reach the request
        Set("CLAUDECODE", "1"); // (CLAUDE_CONFIG_DIR: covered by PrimeUnset's test; the engine tests own that variable)
        var engine = new Engine(Path.Combine(AppContext.BaseDirectory, "fakes", "cswap.cmd"));
        var account = new Account(2, "b@example.com", false, "ok", null);

        Assert.Equal((PrimeResult.Sent, "sent"), await engine.PrimeAsync(account));
        Assert.Contains("run 2 --require-session -- -p Hi --model haiku", File.ReadAllText(log));
        var seen = File.ReadAllText(log + ".prime").Trim().Split('|');
        Assert.Equal(["False", "False", "False"], seen[..3]); // no API key, config folder or parent session reached it
        Assert.Equal(Path.Combine(_tmp.Path, "prime"), seen[3].TrimEnd('\\')); // an empty folder, not the user's home

        // The engine refused before Claude started: nothing went out.
        Set("FAKE_PRIME_FAIL", "Account-2 is the active default login");
        var (result, detail) = await engine.PrimeAsync(account);
        Assert.Equal(PrimeResult.Refused, result);
        Assert.Contains("active default login", detail);

        // Claude started, then failed without an answer: it may have gone out.
        Set("FAKE_PRIME_FAIL", null);
        Set("FAKE_PRIME_CRASH", "connection reset");
        Assert.Equal(PrimeResult.Unknown, (await engine.PrimeAsync(account)).Result);
    }

    public void Dispose()
    {
        foreach (var (k, v) in _was) Environment.SetEnvironmentVariable(k, v);
        _tmp.Dispose();
    }
}

public class TerminalEnvironmentTests
{
    [Fact]
    public void ANewTerminalNeverInheritsTheSessionHandoffWasStartedFrom()
    {
        static Dictionary<string, string?> Env(params (string, string)[] vars) => vars.ToDictionary(v => v.Item1, v => (string?)v.Item2, StringComparer.OrdinalIgnoreCase);
        // What the owner's Windows gives a new program: their own settings, Claude Code's among them.
        IReadOnlyDictionary<string, string> windows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @"C:\Windows", ["USERPROFILE"] = @"C:\Users\me", ["CLAUDE_EFFORT"] = "high", ["CLAUDE_CODE_GIT_BASH_PATH"] = @"C:\Git\bin\bash.exe",
        };
        // Patch Goblins started from inside a session: everything that session set up for its tools goes.
        var fromSession = Env(("CLAUDECODE", "1"), ("CLAUDE_CODE_CHILD_SESSION", "1"), ("CLAUDE_CODE_MESSAGING_TOKEN", "x"), ("CLAUDE_PID", "1"), ("PATCH_GOBLINS_DATA", @"C:\dev"),
            ("AI_AGENT", "claude-code_agent"), ("NO_COLOR", "1"), ("GIT_EDITOR", "true"), ("GIT_TERMINAL_PROMPT", "0"), ("PROMPT", "$P$G"),
            ("PATH", @"C:\Windows;C:\session-only"), ("USERPROFILE", @"C:\Users\me"));
        Engine.TopLevel(fromSession, () => windows);
        Assert.Equal(windows.Append(KeyValuePair.Create("PATCH_GOBLINS_DATA", @"C:\dev")).OrderBy(v => v.Key), // Patch Goblins' own instance stays
            fromSession.OrderBy(v => v.Key).Select(v => KeyValuePair.Create(v.Key, v.Value!)));

        // Started normally: the environment is the owner's own; only stray session markers go.
        var own = Env(("NO_COLOR", "1"), ("AI_AGENT", "x"), ("CLAUDE_CODE_CHILD_SESSION", "1"), ("CLAUDE_EFFORT", "high"),
            ("CLAUDE_CODE_SESSION_LOG", "1"), ("PATH", @"C:\x"));
        Engine.TopLevel(own, () => throw new InvalidOperationException("not needed"));
        Assert.Equal(["AI_AGENT", "CLAUDE_CODE_SESSION_LOG", "CLAUDE_EFFORT", "NO_COLOR", "PATH"], own.Keys.Order(StringComparer.Ordinal)); // the owner's diagnostics stay

        // Started by a process that already dropped the markers (a restarted tray): the session's NO_COLOR goes all the same.
        var relayed = Env(("AI_AGENT", "claude-code_2-1-309_agent"), ("NO_COLOR", "1"), ("PATH", @"C:\x"));
        Engine.TopLevel(relayed, () => windows);
        Assert.False(relayed.ContainsKey("NO_COLOR"));
        Assert.False(relayed.ContainsKey("AI_AGENT"));
    }

    [Fact]
    public void WindowsGivesAFreshEnvironmentWithoutAnySession()
    {
        var fresh = Engine.FreshEnvironment();
        Assert.NotNull(fresh);
        Assert.True(fresh!.ContainsKey("PATH") && fresh.ContainsKey("USERPROFILE"));
        Assert.False(fresh.ContainsKey("CLAUDECODE")); // even though these tests may run inside one
    }

    [Fact]
    public void TheAgentsTerminalThemeIsPatchGoblinsGreenOnTheOwnersOwnTheme()
    {
        using var tmp = new TempDir();
        static JsonNode Theme(string plugin) => JsonNode.Parse(File.ReadAllText(Path.Combine(plugin, "themes", "patch-goblins.json")))!;
        var plugin = AgentsTerminal.WriteTheme(tmp.Path, "dark-daltonized")!;
        Assert.Equal("goblin", JsonNode.Parse(File.ReadAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json")))!["name"]!.GetValue<string>());
        // Its own commands, /goblin:needs and /goblin:goal, each with a description Claude Code lists under "/".
        Assert.Equal(["goal.md", "needs.md"], Directory.GetFiles(Path.Combine(plugin, "commands")).Select(Path.GetFileName).Order());
        Assert.All(Directory.GetFiles(Path.Combine(plugin, "commands")), f => Assert.StartsWith("---\ndescription: ", File.ReadAllText(f).ReplaceLineEndings("\n")));
        var theme = Theme(plugin);
        Assert.Equal("dark-daltonized", theme["base"]!.GetValue<string>()); // the owner's own theme underneath
        Assert.Equal("""{"clawd_body":"#5BD25B","claude":"#6FD36F","claudeShimmer":"#A6EBA6"}""", theme["overrides"]!.ToJsonString()); // the mascot color (the goblin's green) and the brand accent, nothing else
        // A light theme gets darker greens that read on it; auto, a custom theme or none get a dark base. Never the owner's setting.
        var light = Theme(AgentsTerminal.WriteTheme(Path.Combine(tmp.Path, "light"), "light"));
        Assert.Equal("light", light["base"]!.GetValue<string>());
        Assert.Equal("#2E7D32", light["overrides"]!["clawd_body"]!.GetValue<string>());

        // The goblin itself: the preload beside the session's files, handed to Claude Code's Bun runtime through BUN_OPTIONS.
        var env = AgentsTerminal.Mascot(tmp.Path);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "mascot.js")));
        var preload = env["BUN_OPTIONS"].Split("--preload ")[^1];
        Assert.DoesNotContain(" ", preload); // one option: a spaced path goes in its 8.3 form
        Assert.DoesNotContain("\\", preload); // Bun reads a backslash there as an escape
        Assert.True(File.Exists(preload), preload);
        foreach (var other in new[] { "auto", "custom:mine", null })
            Assert.Equal("dark", Theme(AgentsTerminal.WriteTheme(Path.Combine(tmp.Path, (other ?? "none").Replace(":", "_")), other))["base"]!.GetValue<string>());
    }

    /// The preload swaps Claude Code's mascot rows (as its renderer writes them: runs with color and background
    /// escapes between) for the goblin's, same widths, escapes kept; anything else, a progress bar included, passes.
    [Fact]
    public void TheMascotPreloadSwapsClaudesRowsForTheGoblins()
    {
        if (Engine.FindOnPath("node.exe") is not { } node) return;
        using var tmp = new TempDir();
        AgentsTerminal.Mascot(tmp.Path);
        const string G = "\u001b[38;2;91;210;91m", B = "\u001b[48;2;0;0;0m", R = "\u001b[49m\u001b[39m";
        var frame = $"{G} ▐\u001b[39m{G}{B}▛███▛█{R} Claude Code v2.1.307\n{G}▝▜{B}█████\u001b[49m█▀{R} Fable\n{G} ▝▝   ▝▝ {R} ~\n{G}{B}█████{R} 50%\n";
        var js = "const {swap}=require(process.argv[1]);process.stdout.write(swap(require('fs').readFileSync(0,'utf8')))";
        var psi = new System.Diagnostics.ProcessStartInfo(node) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8 };
        psi.ArgumentList.Add("-e"); psi.ArgumentList.Add(js); psi.ArgumentList.Add(Path.Combine(tmp.Path, "mascot.js"));
        using var p = System.Diagnostics.Process.Start(psi)!;
        using (var w = new StreamWriter(p.StandardInput.BaseStream, new System.Text.UTF8Encoding(false))) w.Write(frame);
        var swapped = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal($"{G}▐▖\u001b[39m{G}{B}▄▄▄▄▗▌{R} Claude Code v2.1.307\n{G}▝█{B}▄██▄█\u001b[49m▘ {R} Fable\n{G} ▝▟▟▙▙▘  {R} ~\n{G}{B}█████{R} 50%\n", swapped);
    }
}
