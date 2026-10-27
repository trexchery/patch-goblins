using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

// All credentials in this file are synthetic.

public class ModelTests
{
    const string ListJson = """
    {
      "schemaVersion": 1,
      "activeAccountNumber": 2,
      "accounts": [
        { "number": 1, "email": "a@example.com", "organizationName": "", "active": false,
          "usageStatus": "token_expired", "usage": null, "disabled": true,
          "lastGoodUsage": { "fiveHour": { "pct": 12.0, "resetsAt": "2026-06-22T23:29:59Z" } },
          "loginExpiresAt": "2026-07-01T00:00:00Z" },
        { "number": 2, "email": "b@example.com", "alias": "work", "active": true, "usageStatus": "ok",
          "usage": {
            "fiveHour": { "pct": 25.0, "resetsAt": "2026-06-22T23:29:59Z", "countdown": "2h", "clock": "23:29" },
            "sevenDay": { "pct": 91.5, "resetsAt": "2026-06-26T17:59:59Z", "expectedPct": 40.0, "aheadOfPace": true },
            "scoped": [ { "name": "Fable", "pct": 99.0, "resetsAt": "2026-06-26T17:59:59Z" } ],
            "spend": { "used": 1.5, "limit": 10, "pct": 15, "currency": "USD" }
          },
          "usageAgeSeconds": 42.5, "someFutureField": { "x": 1 } },
        { "number": 3, "email": "c@example.com", "active": false, "usageStatus": "unavailable",
          "usage": null, "usageError": "http-429" }
      ],
      "unclaimedCredentials": []
    }
    """;

    [Fact]
    public void ParsesListPayloadIncludingAdditiveFields()
    {
        var s = Json.Parse<Snapshot>(ListJson);
        Assert.Equal(2, s.ActiveAccountNumber);
        Assert.Equal(3, s.Accounts.Count);

        var b = s.Accounts[1];
        Assert.True(b.Active);
        Assert.Equal("work  ·  b@example.com", b.DisplayName);
        Assert.Equal(91.5, b.Usage!.SevenDay!.Pct);
        Assert.True(b.Usage.SevenDay.AheadOfPace);
        Assert.Equal("Fable", b.Usage.Scoped![0].Name);
        Assert.Equal(15, b.Usage.Spend!.Pct);
        Assert.Equal(new DateTimeOffset(2026, 6, 22, 23, 29, 59, TimeSpan.Zero), b.Usage.FiveHour!.ResetsAt);
        Assert.Null(b.StatusNote);
        Assert.Equal(99.0, Format.Peak(b));

        var a = s.Accounts[0];
        Assert.True(a.Disabled);
        Assert.Null(a.Usage);
        Assert.Equal(12.0, a.ShownUsage!.FiveHour!.Pct); // last-known shown when live is untrusted
        Assert.Contains("token expired", a.StatusNote);

        Assert.Equal("usage unavailable (http-429)", s.Accounts[2].StatusNote);
    }

    [Fact]
    public void DetectsEngineErrorPayload()
    {
        var err = Json.TryError("""{"schemaVersion":1,"error":{"type":"AccountNotFoundError","message":"no such account"}}""");
        Assert.Equal("no such account", err!.Message);
        Assert.Null(Json.TryError(ListJson));
        Assert.Null(Json.TryError("not json"));
    }

    [Fact]
    public void ParsesAutoEventStreamAndSkipsNoise()
    {
        var stdout = """
            {"schemaVersion":1,"event":"poll","ts":"2026-06-22T10:00:00Z","active":{"number":1,"email":"a@example.com"},"headroomPct":{"1":5.0},"threshold":90}
            not json at all
            {"schemaVersion":1,"event":"switch","ts":"2026-06-22T10:00:01Z","trigger":"proactive","from":{"number":1,"email":"a@example.com"},"to":{"number":2,"email":"b@example.com"},"warnings":[],"dryRun":false}
            {"schemaVersion":1,"event":"account-quarantined","number":"3","email":"c@example.com","reason":"invalid_grant"}
            {"schemaVersion":1,"event":"all-exhausted","earliestResetAt":"2026-06-22T12:00:00Z"}
            """;
        var events = Engine.ParseEvents(stdout);
        Assert.Equal(["poll", "switch", "account-quarantined", "all-exhausted"], events.Select(e => e.Event));
        Assert.Equal(2, events[1].To!.Number);
        Assert.Equal("proactive", events[1].Trigger);
        Assert.Equal("c@example.com", events[2].Email);
        Assert.NotNull(events[3].EarliestResetAt);
    }
}

public class ProviderTests
{
    // Shape of `cswap codex list --json` / `cswap grok list --json` (synthetic).
    const string ProviderJson = """
    {
      "schemaVersion": 1, "activeAccountNumber": 1,
      "accounts": [
        { "number": 1, "email": "dev@example.com", "plan": "plus", "active": true, "usageStatus": "ok",
          "usage": { "fiveHour": { "pct": 40, "resetsAt": "2026-06-22T23:00:00Z" },
                     "sevenDay": { "pct": 10, "resetsAt": "2026-06-26T00:00:00Z" } }, "usageAgeSeconds": 3 },
        { "number": 2, "email": "x@example.com", "active": false, "usageStatus": "token_expired", "usage": null,
          "usageNote": "login expired; codex renews it after a switch, else re-enroll" },
        { "number": 3, "email": "g@example.com", "active": false, "usageStatus": "ok",
          "usage": { "scoped": [ { "name": "Monthly limit", "pct": 63.5, "resetsAt": "2026-07-01T00:00:00Z" } ] } }
      ]
    }
    """;

    [Fact]
    public void ParsesProviderSnapshotAndPrefersEngineNote()
    {
        var s = Json.Parse<Snapshot>(ProviderJson);
        Assert.Equal(3, s.Accounts.Count);
        Assert.Equal("claude", s.Accounts[0].Provider); // tagged by Engine.ListAsync, never by the payload
        Assert.Equal(40, s.Accounts[0].Usage!.FiveHour!.Pct);
        Assert.Equal("login expired; codex renews it after a switch, else re-enroll", s.Accounts[1].StatusNote);
        Assert.Equal(63.5, Format.Peak(s.Accounts[2]));
        Assert.Equal("Now using", Json.Parse<SwitchResult>("""{"switched":true,"note":"Now using"}""").Note);
    }

    [Fact]
    public void RoutesProviderArguments()
    {
        Assert.Equal(["list", "--json"], Engine.Args("claude", "list", "--json"));
        Assert.Equal(["codex", "switch", "2", "--json"], Engine.Args("codex", "switch", "2", "--json"));
    }
}

public class RedactionTests
{
    [Theory]
    [InlineData("token sk-ant-oat01-AAAAAAAAAAAAAAAAAAAA leaked", "token [redacted] leaked")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz", "Authorization: [redacted]")]
    [InlineData("{\"refreshToken\": \"sk-ant-ort01-zzz\"}", "{[redacted]}")]
    [InlineData("{\"access_token\":\"xyz\"}", "{[redacted]}")]
    [InlineData("{\"id_token\": \"abc\"}", "{[redacted]}")]
    [InlineData("jwt eyJhbGciOiJub25lIn0.eyJzdWIiOiJzeW50aGV0aWMifQ.c2ln end", "jwt [redacted] end")]
    [InlineData("key sk-proj-SYNTHETIC0000000000 x", "key [redacted] x")]
    [InlineData("key xai-SYNTHETIC0000000000 x", "key [redacted] x")]
    [InlineData("refresh_token=SYNTHETIC; rest", "[redacted]; rest")]
    [InlineData("OPENAI_API_KEY=SYNTHETIC123 next", "OPENAI_[redacted] next")]
    [InlineData("password: hunter2", "[redacted]")]
    [InlineData("password = \"hunter 2\" next", "[redacted] next")]
    [InlineData("access_token = \"abc\"", "[redacted]")]
    [InlineData("OPENAI_API_KEY=\"sk-short\"", "OPENAI_[redacted]")]
    [InlineData("{\"api_key\": \"abc123\"}", "{\"[redacted]}")]
    [InlineData("{\"password\": \"hunter2\"}", "{\"[redacted]}")]
    [InlineData("{'access_token': 'abc'}", "{'[redacted]}")]
    [InlineData("gh ghp_SYNTHETIC000000000000000000000000000", "gh [redacted]")]
    [InlineData("gh github_pat_SYNTHETIC_0000000000000000", "gh [redacted]")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE", "aws [redacted]")]
    [InlineData("slack xoxb-0000000000-SYNTHETIC", "slack [redacted]")]
    [InlineData("gcp AIzaSYNTHETIC-synthetic-synthetic-000", "gcp [redacted]")]
    [InlineData("stripe rk_test_SYNTHETIC0000000000", "stripe [redacted]")]
    [InlineData("x -----BEGIN RSA PRIVATE KEY-----\nSYNTHETIC\n-----END RSA PRIVATE KEY----- y", "x [redacted] y")]
    [InlineData("x\n-----BEGIN OPENSSH PRIVATE KEY-----\nSYNTHETIC", "x\n[redacted]")]
    [InlineData("Switched to #2 b@example.com", "Switched to #2 b@example.com")]
    [InlineData(@"Saved C:\Users\me\AppData\Local\patch-goblins\settings.json for a.b@example.com",
        @"Saved C:\Users\me\AppData\Local\patch-goblins\settings.json for a.b@example.com")]
    public void RedactsSecrets(string input, string expected) => Assert.Equal(expected, Engine.Redact(input));
}

public class FormatTests
{
    static readonly DateTimeOffset Now = new(2026, 6, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FormatsResetTimes()
    {
        Assert.Equal("now", Format.When(Now.AddMinutes(-1), Now));
        Assert.Equal("in 1m", Format.When(Now.AddSeconds(20), Now));
        Assert.Equal("in 45m", Format.When(Now.AddMinutes(45), Now));
        Assert.Equal("in 2h 05m", Format.When(Now.AddMinutes(125), Now));
        Assert.Equal("7m ago", Format.Age(420));
        Assert.Equal("2h ago", Format.Age(7300));
        Assert.Equal(Now.AddDays(3).ToLocalTime().ToString("ddd HH:mm"), Format.When(Now.AddDays(3), Now));
    }

    [Fact]
    public void TooltipStaysUnderWindowsLimit()
    {
        var acct = new Account(1, new string('x', 200) + "@example.com", true, "ok",
            new Usage(new Window(10, null), new Window(20, null), null, null));
        var tip = Format.Tooltip(new Snapshot(1, 1, [acct]), auto: true);
        Assert.True(tip.Length <= 127);
        Assert.Equal("Patch Goblins — no managed account active", Format.Tooltip(null, false));
    }

    [Fact]
    public void QuotesArgumentsForConsole()
    {
        Assert.Equal("run", Engine.QuoteArg("run"));
        Assert.Equal("\"C:\\Program Files\\x\\cswap.exe\"", Engine.QuoteArg(@"C:\Program Files\x\cswap.exe"));
        Assert.Equal("\"a&b\"", Engine.QuoteArg("a&b"));
        Assert.Equal("'run'", Engine.PsQuote("run"));
        Assert.Equal("'it''s $x `n %PATH% & more'", Engine.PsQuote("it's $x `n %PATH% & more"));
        Assert.Equal("('say '+[char]34+'hi'+[char]34+'')", Engine.PsQuote("say \"hi\"")); // never a `"` in the script
    }

    [Fact]
    public void FindProgramFallsBackToKnownDirectories()
    {
        using var t = new TempDir();
        var exe = Path.Combine(t.Path, "pg-nowhere-on-path.exe");
        File.WriteAllText(exe, "");
        Assert.Equal(exe, Engine.FindProgram("pg-nowhere-on-path.exe", Path.Combine(t.Path, "missing"), t.Path));
        Assert.Null(Engine.FindProgram("pg-nowhere-on-path.exe", Path.Combine(t.Path, "missing")));
    }

    [Fact]
    public void TheConsoleShellIsPowerShellWithCmdAsTheFallback()
    {
        Assert.Equal(["-NoLogo", "-NoExit", "-Command", @"& 'C:\Program Files\x\cswap.exe' 'run' '3' '--' '--resume' 'sid' 'it''s'"],
            Engine.ShellArgs(@"C:\Program Files\PowerShell\7\pwsh.exe", null, @"C:\Program Files\x\cswap.exe", ["run", "3", "--", "--resume", "sid", "it's"]));
        Assert.Equal(["-NoLogo", "-NoExit", "-Command", @"$Host.UI.RawUI.WindowTitle = 'Claude · a'; & 'C:\x\cswap.exe' 'run'"],
            Engine.ShellArgs(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "Claude · a", @"C:\x\cswap.exe", ["run"]));
        Assert.Equal(["/k", @"C:\x\cswap.exe", "run", "a&b"], Engine.ShellArgs(Engine.Cmd, null, @"C:\x\cswap.exe", ["run", "a&b"]));
        Assert.Equal(["/k", @"title Claude  a && C:\x\cswap.exe run ""a&b"""], Engine.ShellArgs(Engine.Cmd, "Claude & a", @"C:\x\cswap.exe", ["run", "a&b"]));
        Assert.True(Engine.Shell.EndsWith("pwsh.exe", StringComparison.OrdinalIgnoreCase) || Engine.Shell.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase));
    }

    /// Every argument reaches the program as typed, through the shell's `-Command` script (the conhost form, with its title).
    [Fact]
    public void ThePowerShellConsoleHandsArgumentsOverVerbatim()
    {
        var python = Engine.FindOnPath("python.exe");
        if (python == null) return; // the probe
        string[] args = ["-I", "-c", "import sys,json;print(json.dumps(sys.argv[1:]))", "run", "--", "it's \"quoted\"", "50% & more; `x` $y", "a b", "Ünïcödé · dot"];
        var shellArgs = Engine.ShellArgs(Engine.Shell, "Claude · test", python, args);
        var psi = new System.Diagnostics.ProcessStartInfo(Engine.Shell) { UseShellExecute = false, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var a in shellArgs.Where(a => a != "-NoExit")) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = System.Diagnostics.Process.Start(psi)!;
        var line = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        Assert.Equal(args[3..], JsonSerializer.Deserialize<string[]>(line)!);
    }

    /// Opt in with PATCH_GOBLINS_LAUNCH_SMOKE=<folder>: a real console opens (Windows Terminal where installed) exactly as
    /// the tray opens one, and the program inside writes what it received into that folder.
    [Fact]
    public void ARealConsoleLaunchCarriesEveryArgument()
    {
        var python = Engine.FindOnPath("python.exe");
        if (Environment.GetEnvironmentVariable("PATCH_GOBLINS_LAUNCH_SMOKE") is not { Length: > 0 } dir || python == null) return;
        var file = Path.Combine(dir, "launch-smoke.json");
        File.Delete(file);
        string[] args = ["run", "--", "it's \"quoted\"", "50% & more; `x` $y", "a b", "Ünïcödé · dot"];
        Engine.LaunchConsoleWith(python, "Claude · smoke; test", dir,
            ["-I", "-c", "import sys,json;open(sys.argv[1],'w',encoding='utf-8').write(json.dumps(sys.argv[2:]))", file, .. args]);
        for (var i = 0; i < 100 && !File.Exists(file); i++) Thread.Sleep(100);
        Assert.Equal(args, JsonSerializer.Deserialize<string[]>(File.ReadAllText(file))!);
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dock-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class StorageTests
{
    [Fact]
    public void SettingsRoundTripAtomicallyAndClampRefresh()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "sub", "settings.json");
        new DockSettings { AutoSwitch = true, RefreshSeconds = 5 }.Save(file);
        var loaded = DockSettings.Load(file);
        Assert.True(loaded.AutoSwitch);
        Assert.Equal(DockSettings.MinRefreshSeconds, loaded.RefreshSeconds);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!)); // no temp files left behind
    }

    [Fact]
    public void CorruptSettingsFallBackToDefaults()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "settings.json");
        File.WriteAllText(file, "{ not json");
        Assert.False(DockSettings.Load(file).AutoSwitch);
    }

    [Fact]
    public void PurgesEnrollmentLeftovers()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(Path.Combine(t.Path, "abc", "sub"));
        File.WriteAllText(Path.Combine(t.Path, "abc", ".credentials.json"), "{\"synthetic\":true}");
        ClaudeCli.PurgeEnrollLeftovers(t.Path);
        Assert.Empty(Directory.GetDirectories(t.Path));
        ClaudeCli.PurgeEnrollLeftovers(Path.Combine(t.Path, "missing")); // absent root is fine
    }

    [Fact]
    public void HardenRestrictsDirectoryAndChildrenToCurrentUser()
    {
        using var t = new TempDir();
        var store = Path.Combine(t.Path, "store");
        Directory.CreateDirectory(Path.Combine(store, "credentials"));
        File.WriteAllText(Path.Combine(store, "credentials", "x.enc"), "dpapi1:synthetic");

        StoreSecurity.Harden(store);

        Assert.True(StoreSecurity.IsHardened(store));
        var me = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var fileRules = new FileInfo(Path.Combine(store, "credentials", "x.enc")).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.NotEmpty(fileRules);
        Assert.All(fileRules, r => Assert.True(r.IdentityReference.Equals(me) || r.IdentityReference.Equals(system)));
        // A file created afterwards inherits the same restriction.
        var later = Path.Combine(store, "later.json");
        File.WriteAllText(later, "{}");
        Assert.All(new FileInfo(later).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            r => Assert.True(r.IdentityReference.Equals(me) || r.IdentityReference.Equals(system)));
    }
}

public class SettingsDefaultsTests
{
    [Fact]
    public void KeysAbsentFromAnOlderFileKeepTheirDefaults()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "settings.json");
        File.WriteAllText(file, """{ "AutoSwitch": true, "RefreshSeconds": 300 }""");
        var s = DockSettings.Load(file);
        Assert.True(s.AutoSwitch);
        Assert.True(s.PreserveRemoteControl); // on unless the user turns it off
        Assert.False(s.AutoProjectAccount);
        Assert.False(s.QuotaNotifications);

        var fresh = new DockSettings();
        Assert.True(fresh.PreserveRemoteControl);
        Assert.False(fresh.AutoProjectAccount);
        Assert.False(fresh.QuotaNotifications);
    }

    [Fact]
    public void ExplicitValuesRoundTrip()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "settings.json");
        new DockSettings { PreserveRemoteControl = false, AutoProjectAccount = true, QuotaNotifications = true }.Save(file);
        var s = DockSettings.Load(file);
        Assert.False(s.PreserveRemoteControl);
        Assert.True(s.AutoProjectAccount);
        Assert.True(s.QuotaNotifications);
    }
}

public class QuotaAlertTests
{
    static Account A(string email, double? fiveHour = null, double? weekly = null, string status = "ok", string? org = null, List<Window>? scoped = null) =>
        new(1, email, false, status,
            status == "ok" ? new Usage(fiveHour is { } f ? new Window(f, null) : null, weekly is { } w ? new Window(w, null) : null, scoped, null) : null,
            // Last-known readings exist but must never be read as live ones.
            LastGoodUsage: status == "ok" ? null : new Usage(new Window(5, null), new Window(5, null), null, null),
            OrganizationUuid: org);

    static List<QuotaAlert> Seen(QuotaAlerts q, params Account[] accounts) => q.Observe("claude", accounts);

    [Fact]
    public void FirstReadingIsASilentBaseline()
    {
        var q = new QuotaAlerts();
        Assert.Empty(Seen(q, A("a@example.com", 95, 100)));
        Assert.Empty(Seen(q, A("a@example.com", 95, 100)));
    }

    [Fact]
    public void AlertsOnNearExhaustedAndResetOnlyOnce()
    {
        var q = new QuotaAlerts();
        Seen(q, A("a@example.com", 10));
        Assert.Empty(Seen(q, A("a@example.com", Palette.WarnPct - 0.1)));

        var near = Assert.Single(Seen(q, A("a@example.com", Palette.WarnPct)));
        Assert.Equal((QuotaState.Near, "5-hour"), (near.State, near.Window));
        Assert.Equal("a@example.com · 5-hour at 80%", near.Line);
        Assert.Empty(Seen(q, A("a@example.com", 90))); // still near: no repeat

        var spent = Assert.Single(Seen(q, A("a@example.com", 100)));
        Assert.Equal(QuotaState.Exhausted, spent.State);
        Assert.Equal("a@example.com · 5-hour limit reached", spent.Line);
        Assert.Empty(Seen(q, A("a@example.com", 100)));

        var reset = Assert.Single(Seen(q, A("a@example.com", 3)));
        Assert.Equal(QuotaState.Normal, reset.State);
        Assert.Equal("a@example.com · 5-hour limit reset", reset.Line);
        Assert.Empty(Seen(q, A("a@example.com", 4)));
    }

    [Fact]
    public void SkippingNearStillAlertsExhausted()
    {
        var q = new QuotaAlerts();
        Seen(q, A("a@example.com", 10));
        Assert.Equal(QuotaState.Exhausted, Assert.Single(Seen(q, A("a@example.com", 100))).State);
    }

    [Fact]
    public void EasingDownIsSilentButAnExhaustedWindowCanAlertAgain()
    {
        var q = new QuotaAlerts();
        Seen(q, A("a@example.com", 85)); // baseline: already near
        Assert.Empty(Seen(q, A("a@example.com", 10))); // Near -> Normal
        Seen(q, A("a@example.com", 100));
        Assert.Empty(Seen(q, A("a@example.com", 85))); // Exhausted -> Near
        Assert.Equal(QuotaState.Exhausted, Assert.Single(Seen(q, A("a@example.com", 100))).State);
    }

    [Fact]
    public void UnavailableDataNeverChangesStateOrCountsAsZero()
    {
        var q = new QuotaAlerts();
        Seen(q, A("a@example.com", 100)); // baseline: exhausted
        foreach (var bad in new[] { "unavailable", "token_expired", "relogin_required", "api_key", "something_new" })
            Assert.Empty(Seen(q, A("a@example.com", status: bad)));
        Assert.Empty(Seen(q, new Account(1, "a@example.com", false, "ok", null))); // ok but no reading
        Assert.Empty(Seen(q, A("a@example.com", 100))); // were an outage read as 0 %, this would re-alert
        Assert.Equal(QuotaState.Normal, Assert.Single(Seen(q, A("a@example.com", 10))).State);

        Seen(q, A("b@example.com", 10));
        Assert.Empty(Seen(q, A("b@example.com", status: "unavailable")));
        Assert.Equal(QuotaState.Near, Assert.Single(Seen(q, A("b@example.com", 85))).State);

        // A missing window is unknown, not 0 %.
        Seen(q, A("c@example.com", 100, 100));
        Assert.Empty(Seen(q, A("c@example.com", fiveHour: null, weekly: 100)));
        Assert.Empty(Seen(q, A("c@example.com", 100, 100)));
    }

    [Fact]
    public void WindowsAccountsAndProvidersAreTrackedSeparately()
    {
        var q = new QuotaAlerts();
        var opus = new List<Window> { new(10, null, "Opus") };
        Seen(q, A("a@example.com", 10, 10, scoped: opus), A("b@example.com", 10, 10, org: "org-1"));
        q.Observe("codex", [A("a@example.com", 10, 10)]);

        var alerts = Seen(q,
            A("a@example.com", 10, 90, scoped: [new(100, null, "Opus")]),
            A("b@example.com", 10, 10, org: "org-1"));
        Assert.Equal(["a@example.com · weekly at 90%", "a@example.com · Opus limit reached"], alerts.Select(a => a.Line));

        // Same email, other provider: its own baseline, named with the provider.
        var codex = Assert.Single(q.Observe("codex", [A("a@example.com", 85, 10)]));
        Assert.Equal("Codex a@example.com · 5-hour at 85%", codex.Line);
        // Same email, other organization: a different account.
        Assert.Empty(Seen(q, A("b@example.com", 10, 10, org: "org-2")));
    }

    [Fact]
    public void AliasNamesTheAccount()
    {
        var q = new QuotaAlerts();
        Seen(q, A("a@example.com", 10) with { Alias = "work" });
        Assert.Equal("work · 5-hour at 85%", Assert.Single(Seen(q, A("a@example.com", 85) with { Alias = "work" })).Line);
    }

    [Fact]
    public void OneRefreshBecomesOneBalloon()
    {
        Assert.Null(QuotaAlerts.Balloon([]));

        var q = new QuotaAlerts();
        var accounts = Enumerable.Range(1, 5).Select(i => A($"u{i}@example.com", 10)).ToArray();
        Seen(q, accounts);
        var alerts = Seen(q, accounts.Select(a => A(a.Email, 90)).ToArray());
        Assert.Equal(5, alerts.Count);

        var (title, text) = QuotaAlerts.Balloon(alerts)!.Value;
        Assert.Equal("Quota update", title);
        Assert.Equal("u1@example.com · 5-hour at 90%\nu2@example.com · 5-hour at 90%\nu3@example.com · 5-hour at 90%\n+2 more", text);
        Assert.Equal("u1@example.com · 5-hour at 90%", QuotaAlerts.Balloon(alerts.Take(1).ToList())!.Value.Text);
    }
}

public class ProjectMapTests
{
    const string MappingsJson = """
    { "schemaVersion": 1, "mappings": {
        "c:\\work\\client": { "email": "b@example.com", "organizationUuid": "org-1", "added": "2026-01-01T00:00:00Z" },
        "c:\\work": { "email": "a@example.com", "organizationUuid": "" },
        "c:\\no-email": { "organizationUuid": "x" },
        "c:\\junk": "nope" } }
    """;

    static string Write(TempDir t, string text)
    {
        var file = Path.Combine(t.Path, "mappings.json");
        File.WriteAllText(file, text);
        return file;
    }

    static readonly Account A = new(1, "a@example.com", true, "ok", null);
    static readonly Account B = new(2, "b@example.com", false, "ok", null, OrganizationUuid: "org-1");

    [Fact]
    public void MissingCorruptAndMisshapenFilesReadAsEmpty()
    {
        using var t = new TempDir();
        Assert.Empty(ProjectMap.Load(Path.Combine(t.Path, "mappings.json")));
        Assert.Empty(ProjectMap.Load(Write(t, "{ not json")));
        Assert.Empty(ProjectMap.Load(Write(t, "[]")));
        Assert.Empty(ProjectMap.Load(Write(t, """{ "mappings": [] }""")));
        Assert.Empty(ProjectMap.Load(Write(t, """{ "schemaVersion": 1 }""")));
    }

    [Fact]
    public void ParsesEntriesAndSkipsMalformedOnes()
    {
        using var t = new TempDir();
        var maps = ProjectMap.Load(Write(t, MappingsJson));
        Assert.Equal([@"c:\work", @"c:\work\client"], maps.Select(m => m.Folder));
        Assert.Equal(["work", "client"], maps.Select(m => m.Leaf));
        Assert.Equal(("a@example.com", ""), (maps[0].Email, maps[0].Org));
        Assert.Equal(("b@example.com", "org-1"), (maps[1].Email, maps[1].Org));
        Assert.Equal(@"c:\", new ProjectMapping(@"c:\", "a@example.com", "").Leaf);
    }

    [Fact]
    public void MatchesByEmailAndOrganization()
    {
        var personal = new ProjectMapping(@"c:\work", "a@example.com", "");
        var team = new ProjectMapping(@"c:\work\client", "b@example.com", "org-1");
        Assert.True(personal.Matches(A));
        Assert.False(personal.Matches(B));
        Assert.True(team.Matches(B));
        Assert.False(team.Matches(B with { OrganizationUuid = "org-2" })); // same email, other organization
        Assert.False(team.Matches(B with { OrganizationUuid = null }));
        Assert.False(personal.Matches(A with { Provider = "codex" }));
    }

    [Fact]
    public void NearestMappedAncestorDecidesTheAccount()
    {
        using var t = new TempDir();
        var maps = ProjectMap.Load(Write(t, MappingsJson));
        string Pick(params string[] folders) => string.Join(" ", folders.Select(f => ProjectMap.AccountFor(f, [A, B], maps)?.Email ?? "-"));

        Assert.Equal("a@example.com b@example.com b@example.com a@example.com",
            Pick(@"C:\Work", @"C:\Work\Client", @"C:\WORK\client\src\deep", @"C:\work\other"));
        Assert.Equal("- -", Pick(@"C:\Work2", @"D:\work")); // a shared prefix is not an ancestor
        Assert.Null(ProjectMap.AccountFor(@"C:\work\client", [A], maps)); // its account is gone: no guess
        Assert.Null(ProjectMap.AccountFor(@"C:\work", [A], []));
    }
}

public class LaunchTests
{
    [Fact]
    public void RestoreRcComesBeforeTheDoubleDash()
    {
        Assert.Equal(["run", "2", "--restore-rc"], Launch.Run(2, true));
        Assert.Equal(["run", "2", "--restore-rc", "--", "Read notes.md and continue"], Launch.Run(2, true, "Read notes.md and continue"));
        var args = Launch.Run(null, true, "--resume");
        Assert.Equal(["run", "--restore-rc", "--", "--resume"], args);
        Assert.True(Array.IndexOf(args, "--restore-rc") < Array.IndexOf(args, "--"));
    }

    [Fact]
    public void RestoreRcIsOmittedWhenTurnedOff()
    {
        Assert.Equal(["run", "2"], Launch.Run(2, false));
        Assert.Equal(["run", "2", "--", "prompt"], Launch.Run(2, false, "prompt"));
        Assert.Equal(["run", "--", "prompt"], Launch.Run(null, false, "prompt"));
    }

    [Fact]
    public void ContinueInClaudePicksTheAccountToRunAs()
    {
        Account mapped = new(1, "m@example.com", false, "ok", null), active = new(2, "a@example.com", true, "ok", null);
        // Mapped folder: the engine resolves the mapping itself, so no NUM (passing the active one would pin the wrong account).
        Assert.Equal(["run", "--restore-rc", "--", "p"], Launch.Continue(mapped, active, true, "p")!);
        Assert.Equal(["run", "--restore-rc", "--", "p"], Launch.Continue(mapped, null, true, "p")!);
        Assert.Equal(["run", "2", "--restore-rc", "--", "p"], Launch.Continue(null, active, true, "p")!);
        Assert.Null(Launch.Continue(null, null, true, "p")); // no known account: launch claude directly
        Assert.Equal(["run", "2", "--", "p"], Launch.Continue(null, active, false, "p")!);
        Assert.Equal(["run", "--", "p"], Launch.Continue(mapped, active, false, "p")!);
    }

    [Fact]
    public void SwitchBalloonNamesBothAccounts()
    {
        Assert.Equal("a@example.com switched out · b@example.com active",
            Format.SwitchNotice(new AccountRef(1, "a@example.com"), new AccountRef(2, "b@example.com")));
        Assert.Equal("switched · b@example.com active", Format.SwitchNotice(null, new AccountRef(2, "b@example.com")));
    }
}

/// Drives the real engine end to end against a synthetic home directory.
/// Set PATCH_GOBLINS_TEST_ENGINE to a cswap.exe (build.ps1 does); skipped otherwise.
[Collection("engine")]
public class EngineIntegrationTests
{
    [Fact]
    public async Task EnrollListSealRemove()
    {
        var exe = Environment.GetEnvironmentVariable("PATCH_GOBLINS_TEST_ENGINE");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return; // no engine build available

        using var home = new TempDir();
        var saved = new Dictionary<string, string?>();
        void Set(string k, string? v) { saved.TryAdd(k, Environment.GetEnvironmentVariable(k)); Environment.SetEnvironmentVariable(k, v); }
        Set("USERPROFILE", home.Path);
        Set("HOME", home.Path);
        Set("CLAUDE_CONFIG_DIR", null);
        // Usage fetches must not leave the machine: route them into a dead proxy.
        Set("HTTPS_PROXY", "http://127.0.0.1:9");
        Set("HTTP_PROXY", "http://127.0.0.1:9");
        try
        {
            var login = Path.Combine(home.Path, "login");
            Directory.CreateDirectory(login);
            File.WriteAllText(Path.Combine(login, ".credentials.json"), JsonSerializer.Serialize(new
            {
                claudeAiOauth = new { accessToken = "sk-ant-oat01-SYNTHETIC", refreshToken = "sk-ant-ort01-SYNTHETIC", expiresAt = 4102444800000, scopes = new[] { "user:inference" } },
            }));
            File.WriteAllText(Path.Combine(login, ".claude.json"), JsonSerializer.Serialize(new
            {
                oauthAccount = new { emailAddress = "synthetic@example.com", accountUuid = "acc-1", organizationUuid = "org-1", organizationName = "" },
            }));

            var engine = new Engine(exe);
            var enrolled = await engine.CommandAsync("enroll", login);
            Assert.Contains("synthetic@example.com", enrolled);
            Assert.DoesNotContain("SYNTHETIC", enrolled);

            var snap = await engine.ListAsync();
            var acct = Assert.Single(snap.Accounts);
            Assert.Equal("synthetic@example.com", acct.Email);
            Assert.False(acct.Active); // enrollment never touches the default login

            var raw = await engine.RunAsync(["list", "--json"]);
            Assert.DoesNotContain("SYNTHETIC", raw.Stdout + raw.Stderr);

            var enc = Directory.GetFiles(Path.Combine(home.Path, ".claude-swap-backup", "credentials"), "*.enc");
            Assert.NotEmpty(enc);
            Assert.All(enc, f =>
            {
                var text = File.ReadAllText(f);
                Assert.StartsWith("dpapi1:", text);
                Assert.DoesNotContain("SYNTHETIC", System.Text.Encoding.Latin1.GetString(Convert.FromBase64String(text["dpapi1:".Length..])));
            });

            await engine.CommandWithInputAsync("y\n", "remove", "1");
            Assert.Empty((await engine.ListAsync()).Accounts);
        }
        finally
        {
            foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }
    }
}

public class RenderTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

    static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => All(x).Prepend(x));

    static List<string> SectionLabels(Control c) =>
        c.Controls.Cast<Control>().SelectMany(x => x is Label { Tag: "section" } l ? [l.Text] : SectionLabels(x)).ToList();

    static IEnumerable<AccountRow> FindRows(Control c) =>
        c.Controls.Cast<Control>().SelectMany(x => x is AccountRow r ? [r] : FindRows(x));

    /// Smoke test: the dashboard lays out a realistic snapshot without throwing.
    /// Set DOCK_RENDER_OUT=<file.png> to also save what it drew.
    [Fact]
    public void DashboardRendersAccounts()
    {
        var now = DateTimeOffset.UtcNow;
        Window W(double p, double hours, string? name = null) => new(p, now.AddHours(hours), name);
        var snap = new Snapshot(1, 2,
        [
            new Account(1, "personal@example.com", false, "ok", new Usage(W(34, 2.2), W(58, 50), null, null)),
            new Account(2, "work@example.com", true, "ok", new Usage(W(92, 1.1), W(71, 30), [W(88, 70, "Opus")], null), Alias: "work"),
            new Account(3, "side@example.com", false, "token_expired", null,
                LastGoodUsage: new Usage(W(5, 4), W(12, 90), null, null), Disabled: true),
        ]);
        var jobs = new Dictionary<string, Job>
        {
            ["personal@example.com|"] = new(JobKind.Working, "#42 fix login · reviewing"),
            ["work@example.com|"] = new(JobKind.Yours, "yours · signed in"),
            ["side@example.com|"] = new(JobKind.Idle, "sitting out"),
        };
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                using var form = new DashboardForm();
                form.Render(snap, null, null, null, jobs);
                form.SetStatus("12:00");
                Assert.Equal("", form.StatusText); // a refresh time is not news: the header says nothing until the team does
                form.SetTeam("1 working · 1 resting");
                Assert.Equal("1 working · 1 resting", form.StatusText);
                form.SetStatus(DashboardForm.Updating);
                Assert.Equal("1 working · 1 resting", form.StatusText);
                form.SetStatus(DashboardForm.Offline);
                Assert.Equal("offline", form.StatusText);
                form.SetStatus("12:00");
                form.Location = new Point(-10000, -10000); // off-screen: never steals the user's desktop
                form.Show();
                Application.DoEvents();
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                Assert.InRange(form.Height, Ui.S(200), Ui.S(360)); // three slim rows, not cards
                Assert.All(FindRows(form), r => Assert.Equal(Ui.S(68), r.Height)); // each goblin's job line makes its row a line taller
                if (Environment.GetEnvironmentVariable("DOCK_RENDER_OUT") is { Length: > 0 } outPath) bmp.Save(outPath);
                Assert.Equal(["TEAM"], SectionLabels(form)); // no visitors: just the team

                form.Render(snap, null);
                Application.DoEvents();
                Assert.All(FindRows(form), r => Assert.Equal(Ui.S(52), r.Height)); // no job to show: the slim row

                var codex = new Snapshot(1, 1, [new Account(1, "dev@example.com", true, "ok", new Usage(W(40, 3), W(10, 90), null, null)) { Provider = "codex" }]);
                form.Render(snap, null, [("codex", codex, null), ("grok", null, "offline")], null, jobs);
                Application.DoEvents();
                Assert.Equal(["TEAM", "+ VISITORS · CODEX · GROK"], SectionLabels(form)); // visitors stay closed until clicked
                Assert.Empty(FindRows(form).Where(r => r.Account.Provider != "claude"));
                var closedHeight = form.Height;
                if (Environment.GetEnvironmentVariable("DOCK_RENDER_PROVIDERS_OUT") is { Length: > 0 } pOut)
                {
                    form.Shimmer(-1); // the Agents button's glint sweeps for the first moments: a still frame
                    using var pb = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(pb, new Rectangle(0, 0, form.Width, form.Height));
                    pb.Save(pOut);
                }
                form.SetVisitorsOpen(true);
                Application.DoEvents();
                Assert.Equal(["TEAM", "– VISITORS · CODEX · GROK", "CODEX", "GROK"], SectionLabels(form));
                Assert.Equal(["dev@example.com"], FindRows(form).Where(r => r.Account.Provider != "claude").Select(r => r.Account.Email));
                Assert.True(form.Height > closedHeight);
                Assert.InRange(form.Height, Ui.S(300), Ui.S(560));
                form.SetVisitorsOpen(false);
                form.Render(snap, null);
                Application.DoEvents();

                // A refresh while the flyout is hidden must leave it hidden: WM_SETREDRAW TRUE sets WS_VISIBLE, which put a blank,
                // never-activated (so never auto-hidden) flyout on the desktop.
                form.Hide();
                form.Render(snap, null, null, null, jobs);
                Application.DoEvents();
                Assert.False(IsWindowVisible(form.Handle));
                form.Show();
                form.Render(snap, null);
                Application.DoEvents();

                // Expanded row (accordion) must also lay out.
                var row = FindRows(form).First();
                row.SetExpanded(true, animate: false);
                Application.DoEvents();
                Assert.True(row.Height > Ui.S(100));
                if (Environment.GetEnvironmentVariable("DOCK_RENDER_EXPANDED_OUT") is { Length: > 0 } exOut)
                {
                    using var eb = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(eb, new Rectangle(0, 0, form.Width, form.Height));
                    eb.Save(exOut);
                }

                // Settings open inside the flyout, widened to fit, and close back to the accounts.
                row.SetExpanded(false, animate: false);
                var narrow = form.Width;
                var settings = new SettingsPanel();
                form.ShowSettings(settings);
                Application.DoEvents();
                Assert.True(form.SettingsOpen && form.Width >= settings.Width && form.Height > 200);
                // The same three names as the tray menu and the flyout, in the same order.
                Assert.Equal(["TEAM", "WORK", "UPKEEP"], All(settings).OfType<Label>().Select(l => l.Text).Where(t => t is "TEAM" or "WORK" or "UPKEEP"));
                if (Environment.GetEnvironmentVariable("DOCK_RENDER_SETTINGS_OUT") is { Length: > 0 } sOut)
                {
                    using var sb = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(sb, new Rectangle(0, 0, form.Width, form.Height));
                    sb.Save(sOut);
                }
                // Search keeps the rows whose name, hint or section match every word.
                settings.Search.Text = "draft";
                Assert.Equal(["Draft pull requests"], settings.VisibleRows);
                settings.Search.Text = "upkeep switch";
                Assert.Equal(["Auto-switch near limit", "Automatic switch line", "Switch at", "Random switch delay"], settings.VisibleRows);
                settings.Search.Text = "approval";
                Assert.Equal(["Ask for approval"], settings.VisibleRows);
                settings.Search.Text = "";
                Assert.Equal(14, settings.VisibleRows.Count()); // both "Random switch delay" and "Automatic switch line"
                form.CloseSettings();
                Assert.False(form.SettingsOpen);
                Assert.True(form.Visible); // closing settings keeps the flyout open
                Assert.Equal(narrow, form.Width);

                // An approval waits on a card above the accounts; its answer removes it.
                var asked = form.Ask("Start agents on app?", "They take its open issues.", TimeSpan.FromMinutes(1));
                Application.DoEvents();
                Assert.Equal(1, form.Pending);
                var card = All(form).First(c => c.Tag as string == "ask");
                if (Environment.GetEnvironmentVariable("DOCK_RENDER_ASK_OUT") is { Length: > 0 } aOut)
                {
                    using var ab = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(ab, new Rectangle(0, 0, form.Width, form.Height));
                    ab.Save(aOut);
                }
                card.Controls.OfType<Pill>().First(p => p.Text == "Approve").PerformClick();
                for (var i = 0; i < 20 && !asked.IsCompleted; i++) { Application.DoEvents(); Thread.Sleep(10); }
                Assert.True(asked.IsCompletedSuccessfully && asked.Result);
                Assert.Equal(0, form.Pending);

                // The notice that replaces Windows' balloon: drawn off-screen, never shown here.
                using var toast = new Toast("Agents need you", "app · #12 Fix login: which database should the cache use, Redis or the existing Postgres?", Palette.Current.Live, null);
                Assert.True(toast.Width > Ui.S(300) && toast.Height is > 50 and < 160);
                if (Environment.GetEnvironmentVariable("DOCK_RENDER_TOAST_OUT") is { Length: > 0 } tOut)
                {
                    using var tb = new Bitmap(toast.Width, toast.Height);
                    toast.DrawToBitmap(tb, new Rectangle(0, 0, toast.Width, toast.Height));
                    tb.Save(tOut);
                }
            }
            catch (Exception e) { failure = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw failure;
    }

    /// A row lists its account's project folders in the expanded view when they fit on a line,
    /// and always in its context menu (open a pinned terminal there, or remove the assignment).
    [Fact]
    public void RowShowsProjectFoldersAndOffersThemInItsMenu()
    {
        var snap = new Snapshot(1, 1,
        [
            new Account(1, "a@example.com", true, "ok", null),
            new Account(2, "b@example.com", false, "ok", null),
        ]);
        var long1 = @"c:\work\" + new string('x', 40);
        var maps = new List<ProjectMapping> { new(@"c:\work\client", "a@example.com", ""), new(@"c:\work\app", "a@example.com", "") };
        var wide = new List<ProjectMapping> { new(long1, "b@example.com", ""), new(long1 + "2", "b@example.com", "") };
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                using var form = new DashboardForm { Location = new Point(-10000, -10000) };
                var actions = new List<string>();
                form.AccountAction += (_, act) => actions.Add(act);
                form.Render(snap, null, null, [.. maps, .. wide]);
                form.Show();
                Application.DoEvents();
                var rows = FindRows(form).ToList();
                foreach (var r in rows) r.SetExpanded(true, animate: false);
                var plain = new AccountRow(snap.Accounts[1], DateTimeOffset.Now);
                plain.SetExpanded(true, animate: false);
                Assert.Equal(plain.Height + Ui.S(22), rows[0].Height); // "Projects · client, app" fits: one more line
                Assert.Equal(plain.Height, rows[1].Height); // two 40-character folders don't: menu only

                var menu = rows[0].ShowMenu(Point.Empty);
                var projects = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Projects");
                Assert.Contains(menu.Items.Cast<ToolStripItem>(), i => i.Text == "Assign project folder…");
                Assert.Equal(["client", "app"], projects.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text));
                var client = projects.DropDownItems.OfType<ToolStripMenuItem>().First(i => i.Text == "client");
                Assert.Equal(@"c:\work\client", client.ToolTipText);
                Assert.Equal(["Open terminal", "Remove"], client.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text));
                client.DropDownItems.Cast<ToolStripItem>().First(i => i.Text == "Remove").PerformClick();
                Application.DoEvents();
                Assert.Equal([@"project-remove:c:\work\client"], actions);

                var wideMenu = rows[1].ShowMenu(Point.Empty);
                Assert.Equal(2, wideMenu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Projects").DropDownItems.Count);
                wideMenu.Close();
                if (!menu.IsDisposed) menu.Close();
                Application.DoEvents();
                plain.Dispose();
            }
            catch (Exception e) { failure = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw failure;
    }

    /// Regression: a refresh re-renders (disposing every row) while a row's menu is open.
    /// Clicking an item then closed the menu through the disposed row, which threw before the click ran.
    [Fact]
    public void MenuOfDisposedRowStillClicksAndIsDisposed()
    {
        var snap = new Snapshot(1, 1,
        [
            new Account(1, "a@example.com", true, "ok", null),
            new Account(2, "b@example.com", false, "ok", null),
        ]);
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                using var form = new DashboardForm { Location = new Point(-10000, -10000) };
                Account? opened = null;
                form.TerminalRequested += a => opened = a;
                form.Render(snap, null);
                form.Show();
                Application.DoEvents();
                var rows = FindRows(form).ToList();
                var menu = rows[0].ShowMenu(Point.Empty);
                form.Render(snap, null);
                Assert.All(rows, r => Assert.False(r.IsDisposed)); // nothing shown changed: the same rows, no rebuild to flicker
                form.Render(new Snapshot(1, 1, [snap.Accounts[0], snap.Accounts[1] with { Disabled = true }]), null);
                Assert.All(rows, r => Assert.True(r.IsDisposed));

                menu.Items.Cast<ToolStripItem>().First(i => i.Text == "Open terminal").PerformClick();
                Application.DoEvents();
                Assert.Equal(1, opened?.Number); // still reaches the dashboard's handler
                Assert.True(menu.IsDisposed);
            }
            catch (Exception e) { failure = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw failure;
    }
}
