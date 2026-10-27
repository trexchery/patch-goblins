using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

/// The supervisor end to end, against a real git repository with a local bare "origin", the fake
/// engine in fakes/cswap.ps1 as Claude Code (synthetic accounts, no credentials) and fakes/gh.ps1 as
/// GitHub. Runs on one thread, like the tray's UI thread.
[Collection("agents end to end")]
public sealed class AgentsEndToEndTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly Loop _loop = new();
    readonly string _repo, _gh, _log;
    readonly Cli _cli;
    readonly Engine _engine;
    AgentStore _store;
    readonly ConcurrentQueue<string> _notes = new();
    readonly string? _dataWas = Environment.GetEnvironmentVariable("PATCH_GOBLINS_DATA");
    Supervisor? _sup;

    static readonly DateTimeOffset Now = DateTimeOffset.Now;

    static Account A(int n, double pct, bool active = false) =>
        new(n, $"a{n}@example.com", active, "ok", new Usage(new Window(pct, Now.AddHours(2)), new Window(pct, Now.AddDays(3)), null, null));

    // #1 is the login at the keyboard and never used; #2 has the most headroom, then #3.
    static readonly IReadOnlyList<Account> Accounts = [A(1, 0, active: true), A(2, 10), A(3, 30)];

    string P(string name) => Path.Combine(_tmp.Path, name);

    public AgentsEndToEndTests()
    {
        _gh = P("gh");
        _log = P("cswap.log");
        Directory.CreateDirectory(_gh);
        Environment.SetEnvironmentVariable("FAKE_LOG", _log);
        Environment.SetEnvironmentVariable("FAKE_GH", _gh);
        Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", P("data")); // logs stay out of the real profile
        AgentProcess.BelowNormal = false;
        _repo = NewRepo("repo");
        var fakes = Path.Combine(AppContext.BaseDirectory, "fakes");
        // Well inside Until's 90 s: a hung fake gh is killed and logged, and Until's failure prints patch-goblins.log.
        if (!OperatingSystem.IsWindows())
            foreach (var fake in new[] { "gh.sh", "cswap.sh" })
                File.SetUnixFileMode(Path.Combine(fakes, fake), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _cli = new Cli(Engine.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git")!, Path.Combine(fakes, OperatingSystem.IsWindows() ? "gh.cmd" : "gh.sh")) { GhTimeout = TimeSpan.FromSeconds(30) };
        _engine = new Engine(Path.Combine(fakes, OperatingSystem.IsWindows() ? "cswap.cmd" : "cswap.sh"));
        _store = new AgentStore(P("agents"));
    }

    /// A repository with one commit on main and its own local bare origin (`name`.git).
    string NewRepo(string name)
    {
        var repo = P(name);
        Git(_tmp.Path, "init", "--quiet", "--bare", "-b", "main", repo + ".git");
        Git(_tmp.Path, "clone", "--quiet", repo + ".git", repo);
        Git(repo, "config", "user.email", "agents@example.com");
        Git(repo, "config", "user.name", "Patch Goblins test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "--quiet", "-m", "init");
        Git(repo, "push", "--quiet", "-u", "origin", "main");
        Git(repo, "remote", "set-head", "origin", "main");
        return repo;
    }

    static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {err}");
        return output.Trim();
    }

    static AgentsConfig Fast() => new()
    {
        Stall = TimeSpan.FromHours(1), // a run slowed by a busy machine is not stalled; the stall test sets its own
        ScanEvery = TimeSpan.FromHours(1),
        PrEvery = TimeSpan.FromSeconds(1),
        StopGrace = TimeSpan.FromSeconds(5),
        LaunchBackoff = TimeSpan.FromSeconds(1),
        Relink = TimeSpan.FromSeconds(1),
        Triage = false, // its own tests turn it on: it would take the fake director's plans meant for goals
    };

    /// The tray starting, with the repository's agents terminal open (agents work only then) unless `terminal` is false.
    Task<Supervisor> StartAsync(AgentsConfig? config = null, bool terminal = true) => _loop.Run(() =>
    {
        var s = new Supervisor(_engine, _cli, _store, config ?? Fast());
        s.Authorize = (_, _, _) => Task.FromResult(true);
        s.ConfirmChecks = (_, _) => Task.FromResult(true);
        s.Notify = (title, text) => _notes.Enqueue($"{title}: {text}");
        s.Start();
        if (terminal) s.Attach(_repo);
        s.UpdateAccounts(Accounts, []);
        return _sup = s;
    });

    Task Terminal(bool open, string? repo = null) => _loop.Run(() =>
    {
        if (open) _sup!.Attach(repo ?? _repo);
        else _sup!.Detach(repo ?? _repo);
        return 0;
    });

    void Issues(string json) => File.WriteAllText(Path.Combine(_gh, "issues.json"), json);

    Task<string> Tool(string tool, string args, string? repo = null) =>
        _loop.Run(() => _sup!.HandleAsync(tool, JsonDocument.Parse(args).RootElement.Clone(), repo ?? _repo));

    Task<T> On<T>(Func<RepoAgents, T> read, string? repo = null) => _loop.Run(() => read(_sup!.RepoFor(repo ?? _repo)!));

    WorkItem Item(RepoAgents r, int issue) => r.Items.Single(i => i.Issue == issue);

    async Task Until(Func<RepoAgents, bool> condition, string what, int seconds = 90, string? repo = null)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!await On(r => { try { return condition(r); } catch (InvalidOperationException) { return false; } }, repo))
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"{what}; state: {await On(r => string.Join(", ", r.Items.Select(i => $"{i.Label}={i.State} ({i.Note})").Concat(r.Goals.Select(g => $"{g.Id}={g.State} plans={g.Plans} failures={g.Failures} ({g.Note})"))), repo)}\nlog:\n{Read(_log)}\ngh:\n{Read(Path.Combine(_gh, "gh.log"))}\npatch-goblins.log:\n{Read(Path.Combine(P("data"), "patch-goblins.log"))}");
            await Task.Delay(200);
        }
    }

    /// Waits for a stopped run's process to be gone: the state can flip while the job's process is still exiting.
    static async Task Gone(int pid, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            try { using var p = Process.GetProcessById(pid); if (p.HasExited) return; }
            catch (ArgumentException) { return; }
            Assert.True(DateTime.UtcNow < until, $"process {pid} still runs");
            await Task.Delay(100);
        }
    }

    /// Shared with the fakes, which may be appending to a log right now.
    static string Read(string path)
    {
        if (!File.Exists(path)) return "";
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(f).ReadToEnd();
    }

    string[] Runs => Read(_log).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.StartsWith("run ")).ToArray();

    int _plans;

    /// The fake director's answer to the next plan prompt, in order; without one it plans nothing new.
    void Plan(string json) => File.WriteAllText($"{_log}.plan-{++_plans:000}.json", json);

    static Goal G(RepoAgents r, string id = "g1") => r.Goals.Single(g => g.Id == id);

    static WorkItem W(RepoAgents r, string id) => r.Items.Single(i => i.Id == id);

    string[] DirectorPrompts() => Read(_log + ".prompts").Split("---- account").Where(p => p.Contains("You are the Patch Goblins Director")).ToArray();

    /// The planner's own checkout of the base.
    string PlanCheckout(RepoAgents r) => Path.Combine(P("agents"), "wt", r.Key + "-plan-g1");

    static AgentsConfig GoalConfig(TimeSpan? replan = null)
    {
        var c = Fast();
        c.ReplanEvery = replan ?? TimeSpan.FromHours(1);
        return c;
    }

    Task<string> Goal(object args) => Tool("agents_goal", JsonSerializer.Serialize(args));

    [Fact]
    public async Task WorksAnIssueThroughReviewToAPullRequestAndFinishesWhenMerged()
    {
        Issues("""
        [
          {"number":1,"title":"Add greeting","body":"Say hello.","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":2,"title":"Should we?","body":"","labels":[{"name":"question"}],"assignees":[],"author":{"login":"owner"}},
          {"number":3,"title":"Theirs","body":"","labels":[],"assignees":[{"login":"someone"}],"author":{"login":"owner"}},
          {"number":4,"title":"After greeting","body":"Blocked by #1.","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":5,"title":"Covered","body":"","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":6,"title":"Master Roadmap (6 Phases)","body":"- [ ] #1\n- [ ] #4\n- [ ] #8","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":7,"title":"[Phase 2/6] Platform Foundation","body":"planning and tracking only","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":8,"title":"Decide the hosting model","body":"","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":9,"title":"Storage","body":"## Dependencies\n#8 hosting; coordinate #1.\n","labels":[],"assignees":[],"author":{"login":"owner"}}
        ]
        """);
        File.WriteAllText(Path.Combine(_gh, "others.json"), """[{"number":3,"closingIssuesReferences":[{"number":5}]}]""");
        await StartAsync();
        var started = await Tool("agents_control", """{"action":"start","verify":"git --version"}""");
        Assert.StartsWith("Started.", started);

        await Until(r => r.Items.Any(i => i.Issue == 1 && i.State == ItemState.Open), "issue 1 reaches an open pull request");
        Assert.Equal(new[] { 1 }, await On(r => r.Items.Select(i => i.Issue!.Value).ToArray())); // 2-9 filtered out: held, theirs, blocked, covered, trackers, a decision, unmet dependency
        var item = await On(r => Item(r, 1));
        Assert.Equal(7, item.Pr);
        Assert.Equal("patch-goblins/1-add-greeting", item.Branch);
        Assert.Contains("patch-goblins/1-add-greeting", Git(_repo, "ls-remote", "origin"));
        var body = Read(Path.Combine(_gh, "pr-body-7.md"));
        Assert.Contains("For #1.", body);
        Assert.DoesNotContain("Closes", body);
        // Without memory the review prompt is as it was: no blank line before the verdict instructions.
        var review = Read(_log + ".prompts").Split("---- account").First(r => r.Contains("You are reviewing")).ReplaceLineEndings("\n");
        Assert.DoesNotContain("\n\n\n", review);
        Assert.Contains("Ignore style preferences.\nEnd with one line:", review);
        Assert.Contains("`git --version` passed", body);

        // A work run, then a fresh review run; never the signed-in account, never without isolation.
        Assert.Equal(2, Runs.Length);
        Assert.All(Runs, r => Assert.StartsWith("run 2 --require-session -- -p", r));
        Assert.DoesNotContain("Edit", Runs[0]);
        Assert.Contains("Edit Write NotebookEdit", Runs[1]);
        Assert.All(Read(_log + ".env").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), l => Assert.Equal("patch-goblins-agent-has-no-github-access|file|", l));
        Assert.Contains(_notes, n => n.StartsWith("Pull request ready"));

        var status = await Tool("agents_status", "{}");
        Assert.Contains("Pull request #7 for #1 Add greeting: waiting for review", status);
        Assert.DoesNotContain("session", status); // machinery stays out of view unless asked
        Assert.Contains("session", await Tool("agents_status", """{"detail":true}"""));

        File.WriteAllText(Path.Combine(_gh, "pr-7.json"), """{"state":"MERGED"}""");
        // Done first, then the worktree and local branch go.
        await Until(r => Item(r, 1).State == ItemState.Done && !Directory.Exists(item.Worktree) && Git(_repo, "branch", "--list", "patch-goblins/*").Length == 0,
            "the merged pull request finishes the item and cleans up");
        Assert.DoesNotContain("issue close", Read(Path.Combine(_gh, "gh.log")));
        _sup!.Dispose();
    }

    [Fact]
    public async Task AutomaticModeRunsOnePerAccountSpacedOutAndOpensDraftPullRequests()
    {
        Issues("""
        [
          {"number":30,"title":"One","body":"[fake:slow] a","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":31,"title":"Two","body":"[fake:slow] b","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":32,"title":"Three","body":"[fake:slow] c","labels":[],"assignees":[],"author":{"login":"owner"}}
        ]
        """);
        var config = Fast();
        config.MaxConcurrent = 0; // automatic: #1 is the login at the keyboard, so two accounts take two at once
        config.Ramp = TimeSpan.FromSeconds(2);
        config.DraftPrs = true;
        await StartAsync(config);
        await Tool("agents_control", """{"action":"start"}""");

        var peak = 0;
        await Until(r =>
        {
            peak = Math.Max(peak, r.Items.Count(i => i.State is ItemState.Working or ItemState.Checking));
            return r.Items.Count(i => i.State == ItemState.Open) == 3;
        }, "all three reach a pull request", 180); // the longest pipeline here: two chains of runs, reviews and pull requests
        Assert.Equal(2, peak); // never more runs than usable accounts, and both at once
        var work = Runs.Where(r => !r.Contains("Edit Write")).ToArray(); // work runs, not reviews
        Assert.Contains(work, r => r.StartsWith("run 2 "));
        Assert.Contains(work, r => r.StartsWith("run 3 "));
        Assert.All(Read(Path.Combine(_gh, "gh.log")).Split('\n').Where(l => l.StartsWith("pr create")), l => Assert.Contains("--draft", l));
        _sup!.Dispose();
    }

    [Fact]
    public async Task EveryAccountOutOfUsageHoldsTheWorkAndAReturningAccountResumesIt()
    {
        Issues("""[{"number":40,"title":"Later","body":"Do it.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await _loop.Run(() => { _sup!.UpdateAccounts([A(1, 0, active: true), A(2, 95), A(3, 96)], []); return 0; });
        await Tool("agents_control", """{"action":"start"}""");
        await AgentProcessTests.WaitUntil(() => _notes.Any(n => n.StartsWith("Agents waiting for usage")));
        Assert.Empty(Runs); // nothing started while every account is out

        await _loop.Run(() => { _sup!.UpdateAccounts(Accounts, []); return 0; });
        await Until(r => Item(r, 40).State == ItemState.Open, "work resumes once an account has usage again");
        // The note goes out at the end of the pass that launched the work, so it can trail the item opening.
        await AgentProcessTests.WaitUntil(() => _notes.Any(n => n.StartsWith("Agents resumed")));
        _sup!.Dispose();
    }

    [Fact]
    public async Task AQuestionParksItsIssueWorkMovesOnAndAnAnswerOnTheIssueBringsItBack()
    {
        Issues("""
        [
          {"number":10,"title":"Greeting tone","body":"[fake:blocked] Pick a tone.","labels":[{"name":"bug"}],"assignees":[],"author":{"login":"owner"}},
          {"number":11,"title":"Add farewell","body":"Say bye.","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":12,"title":"Add emoji","body":"Smile.","labels":[],"assignees":[],"author":{"login":"owner"}}
        ]
        """);
        var config = Fast();
        await StartAsync(config);
        await Tool("agents_control", """{"action":"start"}""");

        await Until(r => Item(r, 10).State == ItemState.NeedsOwner, "issue 10 asks the owner");
        Assert.Equal("Should the greeting be formal?", await On(r => Item(r, 10).Note));
        // Asked on the issue; the fake may still be writing it.
        await AgentProcessTests.WaitUntil(() => Read(Path.Combine(_gh, "question-10.md")).Contains("Should the greeting be formal?"));
        await Until(r => Item(r, 11).State == ItemState.Open, "work moves on to issue 11");
        await Until(r => Item(r, 12).State == ItemState.Open, "and to issue 12");
        Assert.Contains("Needs you: #10 Greeting tone: Should the greeting be formal?", await Tool("agents_status", "{}"));

        // The owner answers on GitHub. Two open pull requests leave capacity for more work.
        Issues("""[{"number":10,"title":"Greeting tone","body":"[fake:blocked] Pick a tone.","labels":[],"assignees":[],"author":{"login":"owner"}},{"number":13,"title":"New","body":"","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        // A question Patch Goblins asked before the rename (old marker) is not the owner's answer.
        File.WriteAllText(Path.Combine(_gh, "comments-10.json"), $$"""{"comments":[{"author":{"login":"owner"},"body":"Which tone?\n<!-- autodock-agents -->","createdAt":"{{DateTimeOffset.UtcNow.AddMinutes(1):O}}"},{"author":{"login":"owner"},"body":"Formal, please.","createdAt":"{{DateTimeOffset.UtcNow.AddMinutes(1):O}}"}]}""");
        await _loop.Run(() => { config.ScanEvery = TimeSpan.FromSeconds(1); _sup!.UpdateAccounts(Accounts, []); return 0; });
        await Until(r => Item(r, 10).State == ItemState.Open, "the answered issue reaches a pull request without a merge");
        Assert.Contains("owner: Formal, please.", Read(_log + ".prompts"));
        Assert.DoesNotContain("Which tone?", Read(_log + ".prompts"));
        Assert.Contains(Runs, r => r.Contains("--resume")); // in its own conversation
        await Until(r => Item(r, 13).State == ItemState.Open, "the next issue also reaches a pull request");
        Assert.DoesNotContain("Holding new work", await Tool("agents_status", "{}"));
        _sup!.Dispose();
    }

    [Fact]
    public async Task AWorktreeInTheMovedDataFolderCarriesOnThere()
    {
        Issues("""[{"number":10,"title":"Greeting tone","body":"[fake:blocked] Pick a tone.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        var config = Fast();
        await StartAsync(config);
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 10).State == ItemState.NeedsOwner, "issue 10 asks the owner");
        var was = await On(r => Item(r, 10).Worktree!);
        await _loop.Run(() => { _sup!.Dispose(); return 0; });

        Directory.Move(P("agents"), P("moved")); // autodock's data folder became Patch Goblins'
        _store = new AgentStore(P("moved"));
        await StartAsync(config);
        var moved = await On(r => _store.Worktree(r, Item(r, 10)));
        Assert.True(Directory.Exists(moved));
        Assert.Equal(moved, await On(r => Item(r, 10).Worktree));
        // git learns the worktree's new place, so no prune drops it with its work.
        await AgentProcessTests.WaitUntil(() => Git(_repo, "worktree", "list", "--porcelain").Replace('\\', '/').Contains(moved.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

        File.WriteAllText(Path.Combine(_gh, "comments-10.json"), $$"""{"comments":[{"author":{"login":"owner"},"body":"Formal, please.","createdAt":"{{DateTimeOffset.UtcNow.AddMinutes(1):O}}"}]}""");
        await _loop.Run(() => { config.ScanEvery = TimeSpan.FromSeconds(1); _sup!.UpdateAccounts(Accounts, []); return 0; });
        await Until(r => Item(r, 10).State == ItemState.Open, "the answered issue carries on in its moved worktree");
        Assert.False(Directory.Exists(was)); // no fresh worktree at the old place
        _sup!.Dispose();
    }

    [Fact]
    public async Task MemoryCarriesAnOwnerNoteIntoTheNextPromptAndLearnedLinesBecomeAgentNotes()
    {
        Issues("""[{"number":40,"title":"Learn","body":"[fake:learn] Do it.","labels":[],"assignees":[],"author":{"login":"owner"}},{"number":41,"title":"After","body":"[fake:learn] Plain.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync(terminal: false); // nothing runs until the notes are in
        Assert.Contains("never started", await Tool("agents_memory", """{"action":"add","text":"x"}"""));
        await Tool("agents_control", """{"action":"start"}""");
        Assert.Equal("Noted for this repository.", await Tool("agents_memory", """{"action":"add","text":"Always use tabs in this repository."}"""));
        await Tool("agents_memory", """{"action":"add","text":"Shared rule for every repository.","global":true}""");
        await Terminal(true);
        await Until(r => Item(r, 40).State == ItemState.Open && Item(r, 41).State == ItemState.Open, "both items reach a pull request");

        var prompts = Read(_log + ".prompts");
        // An item's own agent notes stay out of its review; the next item's work prompt carries them.
        var runs = prompts.Split("---- account", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(runs.Where(r => r.Contains("You are reviewing")), r => Assert.DoesNotContain(r.Contains("issue #40") ? "#40 Learn] " : "#41 After] ", r));
        Assert.Contains("#40 Learn] the build needs the fake toolchain", runs.First(r => r.Contains("Resolve GitHub issue #41"))); // (the log is not UTF-8, so the dot is left out)
        Assert.Contains("- [owner] Always use tabs in this repository.", prompts);
        Assert.Contains("- [owner] Shared rule for every repository.", prompts);
        Assert.True(prompts.IndexOf("Shared memory of Patch Goblins agents", StringComparison.Ordinal) < prompts.IndexOf("How to work:", StringComparison.Ordinal));
        // The LEARNED lines became agent notes and did not leak into the pull request's summary.
        var listed = await Tool("agents_memory", """{"action":"list"}""");
        Assert.Contains("[agent · #41 After] the build needs the fake toolchain", listed);
        Assert.Contains("[agent · #41 After] tests live next to the code", listed);
        Assert.Equal("added issue-40.txt", await On(r => Item(r, 40).Summary));
        Assert.Contains("Memory: 3 notes here, 1 shared", await Tool("agents_status", """{"detail":true}"""));

        Assert.Equal("Forgot 1 note.", await Tool("agents_memory", """{"action":"forget","text":"tabs"}"""));
        Assert.Equal("No such note; the numbers are those agents_memory list prints.", await Tool("agents_memory", """{"action":"forget","text":"12"}"""));
        Assert.Equal("Forgot 1 note.", await Tool("agents_memory", """{"action":"forget","text":"G1."}"""));
        Assert.DoesNotContain("tabs", await Tool("agents_memory", """{"action":"list"}"""));
        _sup!.Dispose();
    }

    [Fact]
    public async Task ALimitMovesTheWorkToAnotherAccountWithAHandoff()
    {
        Issues("""[{"number":20,"title":"Quota","body":"[fake:quota] Do it.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 20).State == ItemState.Open, "the item finishes on another account");
        Assert.StartsWith("run 2 ", Runs[0]);
        Assert.StartsWith("run 3 ", Runs[1]);
        Assert.Contains("--session-id", Runs[1]); // a new conversation...
        Assert.Contains("handoff --from claude --dir", Read(_log)); // ...that starts from the engine's hand-off notes
        Assert.Contains("hand-off notes", Read(_log + ".prompts"));
        Assert.Equal(0, await On(r => Item(r, 20).Failures)); // a limit is not the work's fault
        _sup!.Dispose();
    }

    [Fact]
    public async Task VerificationFailuresAndReviewFindingsGoBackToTheSameConversation()
    {
        Issues("""[{"number":30,"title":"Fragile","body":"[fake:break] [fake:review-changes] Careful.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", JsonSerializer.Serialize(new { action = "start", verify = OperatingSystem.IsWindows() ? "if exist broken.txt (exit 1) else (exit 0)" : "test ! -f broken.txt" }));
        await Until(r => Item(r, 30).State == ItemState.Open, "the item passes verification and review");
        var prompts = Read(_log + ".prompts");
        Assert.Contains("and it failed", prompts);
        Assert.Contains("A fresh review of your change found these problems", prompts);
        Assert.Contains("the greeting has no test", prompts);
        // work, (verify fails) resume, review → changes, resume, review → approved
        Assert.Equal(5, Runs.Length);
        Assert.Equal(3, Runs.Count(r => !r.Contains("NotebookEdit")));
        Assert.Equal(2, Runs.Count(r => r.Contains("--resume")));
        Assert.False(File.Exists(Path.Combine(await On(r => Item(r, 30).Worktree!), "broken.txt")));
        // Unrouted, it runs on the default route. One failed verification may be a flake: the same route resumes.
        // The review's findings are a genuine failure: the fix pass gets the stronger model, same effort, same conversation.
        var work = Runs.Where(r => !r.Contains("NotebookEdit")).ToArray();
        Assert.All(work[..2], r => Assert.Contains("--model sonnet --effort medium", r));
        Assert.Contains("--model opus --effort medium", work[2]);
        Assert.Contains("--resume", work[2]);
        Assert.All(Runs, r => Assert.Contains("\"ultracode\":false", r)); // the owner never allowed it
        var route = await On(r => Item(r, 30).Route!);
        Assert.Equal(("opus", 1, true), (route.Model, route.Escalations, route.VerifyFailed));
        _sup!.Dispose();
    }

    [Fact]
    public async Task TheTriageRoutesQueuedIssuesAndTheirRunsUseTheRoute()
    {
        Issues("""
        [
          {"number":40,"title":"Fix typo","body":"Teh -> The in README.","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":41,"title":"Split the parser","body":"Three independent parsers.","labels":[],"assignees":[],"author":{"login":"owner"}}
        ]
        """);
        // The Director runs #41 first, on Haiku at high effort with ultracode (the three choices are its own), and #40 after it.
        Plan("""
        {"status":"working","summary":"two items","items":[{"title":"invented"}],"route":[
          {"item":"#41","difficulty":"trivial","model":"haiku","effort":"high","ultracode":true,"why":"parts are independent"},
          {"item":"#40","difficulty":"trivial","model":"haiku","effort":"low","after":["#41"]}]}
        """);
        var config = Fast();
        (config.Triage, config.Ultracode, config.Ramp) = (true, true, TimeSpan.FromSeconds(1));
        await StartAsync(config);
        await Tool("agents_control", """{"action":"start","verify":"git --version"}""");
        await Until(r => r.Items.Any(i => i.Issue == 41 && i.State == ItemState.Open), "#41 reaches a pull request");
        var director = Assert.Single(DirectorPrompts());
        Assert.Contains("There is no goal to plan here", director);
        Assert.Contains("Three independent parsers.", director); // bounded descriptions, not titles alone
        Assert.Contains("Capacity:", director);
        // The triage plans nothing and is read-only, without workflows; the work run uses its route, the reviewer is never Haiku.
        Assert.Equal(2, await On(r => r.Items.Count));
        Assert.Contains("NotebookEdit", Runs[0]);
        Assert.Contains("--model sonnet --effort high", Runs[0]); // routing the backlog spends no Opus
        Assert.Contains("\"ultracode\":false", Runs[0]);
        Assert.Contains("issue #41", Read(_log + ".prompts").Split("---- account")[2]);
        Assert.Contains("--model haiku --effort high", Runs[1]);
        Assert.Contains("\"ultracode\":true", Runs[1]); // allowed, recommended, and account #2 has room
        Assert.Contains("--model sonnet --effort low", Runs[2]);
        // #40 waited for #41 to land, and its prompt says what #41 did.
        Assert.Equal(ItemState.Queued, await On(r => Item(r, 40).State));
        File.WriteAllText(Path.Combine(_gh, "pr-7.json"), """{"state":"MERGED"}""");
        await Until(r => Item(r, 40).State == ItemState.Open, "#40 starts once #41 merged");
        Assert.Contains("This builds on work that already landed:\n- #41 Split the parser (merged as #7): added issue-41.txt", Read(_log + ".prompts").ReplaceLineEndings("\n"));
        // The triage is no goal of the owner's; every run was measured, quota as usage, never as cost.
        Assert.Equal("No goals yet.", await Tool("agents_goal", """{"action":"list"}"""));
        Assert.Contains("route haiku · low (trivial, director)", await Tool("agents_details", """{"item":"#40"}"""));
        // A line waits for a usage reading taken after its run ended: the one at hand is older.
        Assert.False(File.Exists(Path.Combine(P("agents"), "metrics.jsonl")));
        var fresh = Accounts.Select(a => a with { UsageFetchedAt = DateTimeOffset.Now.AddSeconds(1) }).ToList();
        await _loop.Run(() => { _sup!.UpdateAccounts(fresh, []); return 0; });
        var metrics = Read(Path.Combine(P("agents"), "metrics.jsonl")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.True(metrics.Count >= 5);
        Assert.Equal(("plan", "plan", "sonnet", "fake-model"), (metrics[0].GetProperty("kind").GetString(), metrics[0].GetProperty("outcome").GetString(),
            metrics[0].GetProperty("model").GetString(), metrics[0].GetProperty("ran_on").GetString()));
        Assert.True(metrics[0].GetProperty("quota_start").GetProperty("fiveHour").GetDouble() >= 0);
        Assert.All(metrics, m => Assert.DoesNotContain("cost", m.GetRawText(), StringComparison.OrdinalIgnoreCase));
        // Runs one after another on one account: only the last one's reading is its own; the others say why they have none.
        var ends = metrics.Where(m => m.GetProperty("quota_end").ValueKind == JsonValueKind.Object).ToList();
        Assert.NotEmpty(ends);
        Assert.All(ends, m => Assert.True(m.GetProperty("quota_end").GetProperty("at").GetDateTimeOffset() > m.GetProperty("at").GetDateTimeOffset()));
        Assert.All(metrics.Except(ends), m => Assert.Equal("another run shared the account", m.GetProperty("quota_note").GetString()));
        _sup!.Dispose();
    }

    [Fact]
    public async Task QuietButBusyRunsAreNotStalledAndALingeringAnsweredRunIsClosedAtOnce()
    {
        Issues("""
        [
          {"number":50,"title":"Think","body":"[fake:think] Reason quietly.","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":51,"title":"Wait","body":"[fake:bgwait] Wait on a background task.","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":52,"title":"Linger","body":"[fake:linger] Answer, then stay.","labels":[],"assignees":[],"author":{"login":"owner"}}
        ]
        """);
        var config = Fast();
        // Short windows: 16 s of streamed thinking and of a quiet background wait both outlast Stall.
        (config.Stall, config.PendingStall, config.FinishGrace) = (TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1));
        await StartAsync(config);
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 50).State == ItemState.Open, "streamed reasoning is progress", 120);
        await Until(r => Item(r, 51).State == ItemState.Open, "a pending background task is waiting, not a stall", 120);
        var started = DateTime.UtcNow;
        await Until(r => Item(r, 52).State == ItemState.Open, "an answered run that stays is closed and judged", 120);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(100), "not after the run's own two minutes");
        Assert.Equal((0, 0, 0), await On(r => (Item(r, 50).Failures, Item(r, 51).Failures, Item(r, 52).Failures)));
        // Its answer is the one with the marker, not the empty result after it nor the "still running" one before it.
        Assert.Equal("added issue-52.txt", await On(r => Item(r, 52).Summary));
        _sup!.Dispose();
    }

    [Fact]
    public async Task AnIssueOrderedAfterOneClosedUnmergedAsksTheOwnerInsteadOfWaitingForever()
    {
        Issues("""
        [
          {"number":40,"title":"Use the parser","body":"Needs the parser.","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":41,"title":"Split the parser","body":"Parser first.","labels":[],"assignees":[],"author":{"login":"owner"}}
        ]
        """);
        Plan("""{"status":"working","route":[{"item":"#41"},{"item":"#40","after":["#41"]}]}""");
        var config = Fast();
        (config.Triage, config.Ramp) = (true, TimeSpan.FromSeconds(1));
        await StartAsync(config);
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 41).State == ItemState.Open, "#41 reaches a pull request");
        File.WriteAllText(Path.Combine(_gh, "pr-7.json"), """{"state":"CLOSED"}""");
        await Until(r => Item(r, 40).State == ItemState.NeedsOwner, "#40 asks instead of waiting on a change that will never land");
        Assert.StartsWith("it was planned after #41 Split the parser, whose pull request was closed without merging", await On(r => Item(r, 40).Note));
        Assert.False(await On(r => Supervisor.Problem(Item(r, 40)))); // a question, not a broken run: no dot
        await Tool("agents_queue", """{"action":"answer","item":"#40"}""");
        await Until(r => Item(r, 40).State == ItemState.Open, "answered, it runs without the prerequisite");
        _sup!.Dispose();
    }

    [Fact]
    public async Task AStalledRunIsResumedAndARestartReattachesInsteadOfStartingAgain()
    {
        Issues("""[{"number":40,"title":"Stuck","body":"[fake:stall]","labels":[],"assignees":[],"author":{"login":"owner"}},{"number":41,"title":"Slow","body":"[fake:slow]","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        var config = Fast();
        config.Stall = TimeSpan.FromSeconds(10);
        await StartAsync(config);
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 40).Failures == 1, "the stalled run is stopped");
        // Only that run may stall: a busy machine must not make the resumption or the review look stalled too.
        await _loop.Run(() => { config.Stall = TimeSpan.FromHours(1); return 0; });
        await Until(r => Item(r, 40).State == ItemState.Open, "the stalled run is resumed");
        // The stalled work run, its resumption in the same conversation, then the review.
        Assert.Equal(3, Runs.Length);
        Assert.Contains("--resume", Runs[1]);

        await Until(r => Item(r, 41).State == ItemState.Working && Item(r, 41).Run is { Kind: "work", Pid: > 0 }, "the slow run starts");
        var pid = await On(r => Item(r, 41).Run!.Pid);
        await _loop.Run(() => { _sup!.Dispose(); return 0; }); // the tray exits; the run carries on
        Assert.False(Process.GetProcessById(pid).HasExited);
        await StartAsync();
        await Until(r => Item(r, 41).State == ItemState.Open, "the re-attached run finishes");
        Assert.Equal(5, Runs.Length); // #40's three, then #41's work and review: no second work run after the restart
        Assert.Equal(0, await On(r => Item(r, 41).Failures));
        _sup!.Dispose();
    }

    [Fact]
    public async Task PullRequestFeedbackGetsAnotherPassOnTheSameBranch()
    {
        Issues("""[{"number":50,"title":"Shiny","body":"Polish.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 50).State == ItemState.Open, "the pull request opens");
        File.WriteAllText(Path.Combine(_gh, "pr-7.json"), $$"""
        {"state":"OPEN","mergeable":"MERGEABLE",
         "statusCheckRollup":[{"__typename":"CheckRun","name":"test","status":"COMPLETED","conclusion":"FAILURE","detailsUrl":"https://github.com/o/r/actions/runs/123/job/9"}],
         "comments":[{"author":{"login":"owner"},"body":"Please also update the README.","createdAt":"{{DateTimeOffset.UtcNow.AddMinutes(1):O}}"}],
         "reviews":[]}
        """);
        await Until(r => Item(r, 50).Followups == 1, "the feedback is picked up");
        File.WriteAllText(Path.Combine(_gh, "pr-7.json"), """{"state":"OPEN","mergeable":"MERGEABLE","statusCheckRollup":[],"comments":[],"reviews":[]}""");
        await Until(r => Item(r, 50).State == ItemState.Open && Item(r, 50).Followup == null, "the follow-up is pushed");
        var prompts = Read(_log + ".prompts");
        Assert.Contains("These checks failed: test.", prompts);
        Assert.True(prompts.Contains("FAIL greeting.test: expected hello"), $"no failed-check log in the follow-up; patch-goblins.log:\n{Read(Path.Combine(P("data"), "patch-goblins.log"))}");
        Assert.Contains("owner: Please also update the README.", prompts);
        var gh = Read(Path.Combine(_gh, "gh.log"));
        Assert.Contains("run view 123 --log-failed", gh);
        Assert.Single(gh.Split('\n'), l => l.StartsWith("pr create")); // the same pull request, updated
        _sup!.Dispose();
    }

    [Fact]
    public async Task PauseStopsTheRunWithoutCountingItAndResumeContinuesIt()
    {
        Issues("""[{"number":60,"title":"Long","body":"[fake:slow]","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 60).State == ItemState.Working && Item(r, 60).Run is { Pid: > 0 }, "the run starts");
        var pid = await On(r => Item(r, 60).Run!.Pid);
        Assert.StartsWith("Paused.", await Tool("agents_control", """{"action":"pause"}"""));
        await Until(r => Item(r, 60).State == ItemState.Queued && Item(r, 60).Run is { Live: false }, "the run stops");
        await Gone(pid);
        Assert.Equal(0, await On(r => Item(r, 60).Failures));
        Assert.Contains("agents paused", await Tool("agents_status", "{}"));
        Assert.Equal("Resumed.", await Tool("agents_control", """{"action":"resume"}"""));
        await Until(r => Item(r, 60).State == ItemState.Open, "the resumed run finishes");
        Assert.Contains(Runs, r => r.Contains("--resume"));
        _sup!.Dispose();
    }

    [Fact]
    public async Task AnAccountYieldedForTheOwnersLoginRestsWhileItsWorkContinuesElsewhere()
    {
        Issues("""[{"number":64,"title":"Long","body":"[fake:slow]","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 64).State == ItemState.Working && Item(r, 64).Run is { Pid: > 0 }, "the run starts");
        var (pid, worktree, email) = await On(r => (Item(r, 64).Run!.Pid, Item(r, 64).Worktree!, Item(r, 64).Run!.Email));
        var held = Accounts.Single(a => a.Email == email);
        var runs = Runs.Length;
        Assert.Equal(1, await _loop.Run(() => _sup!.RunsOn(held)));
        Assert.Equal(1, await _loop.Run(() => _sup!.YieldAsync(held, TimeSpan.FromMinutes(30))));
        await Gone(pid, seconds: 0); // gone before the tray switches the default login there: no wait
        await Until(r => Item(r, 64).State == ItemState.Open, "the work continues on another account");
        Assert.DoesNotContain(Runs.Skip(runs), r => r.StartsWith($"run {held.Number} ")); // it rests: nothing starts on it again
        Assert.NotEmpty(Runs.Skip(runs));
        Assert.Equal(worktree, await On(r => Item(r, 64).Worktree)); // in the same worktree
        Assert.Equal(0, await On(r => Item(r, 64).Failures)); // yielding is not the work's fault
        Assert.Equal(0, await _loop.Run(() => _sup!.YieldAsync(held, TimeSpan.FromMinutes(30)))); // nothing left on it
        _sup!.Dispose();
    }

    [Fact]
    public async Task ARunBeingPreparedOnAnAccountThatYieldsStartsElsewhere()
    {
        Issues("""[{"number":65,"title":"Quick","body":"","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        var held = Accounts[1]; // the most headroom: the pick
        await Tool("agents_control", """{"action":"start"}""");
        // Yields while the run's worktree is prepared: picked, not started yet, so not among the runs a yield stops.
        var until = DateTime.UtcNow.AddSeconds(90);
        while (!await On(r =>
        {
            if (r.Items.FirstOrDefault(i => i.Issue == 65) is not { State: ItemState.Working, Run: null }) return false;
            _ = _sup!.YieldAsync(held, TimeSpan.FromMinutes(30));
            return true;
        }))
        {
            Assert.True(DateTime.UtcNow < until, "the run is never prepared");
            await Task.Delay(5);
        }
        await Until(r => Item(r, 65).State == ItemState.Open, "the work starts on another account");
        Assert.DoesNotContain(Runs, r => r.StartsWith($"run {held.Number} "));
        _sup!.Dispose();
    }

    [Fact]
    public async Task ClosingTheTerminalPausesTheWorkAndReopeningContinuesIt()
    {
        Issues("""[{"number":61,"title":"Long","body":"[fake:slow]","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 61).State == ItemState.Working && Item(r, 61).Run is { Pid: > 0 }, "the run starts");
        var pid = await On(r => Item(r, 61).Run!.Pid);
        await Terminal(open: false);
        await Until(r => Item(r, 61).State == ItemState.Queued && Item(r, 61).Run is { Live: false }, "the run stops with its terminal");
        await Gone(pid);
        Assert.Equal(0, await On(r => Item(r, 61).Failures));
        var runs = Runs.Length;
        await Task.Delay(3000);
        Assert.Equal(runs, Runs.Length); // nothing starts in the background
        Assert.Equal(ItemState.Queued, await On(r => Item(r, 61).State));
        Assert.True(await On(r => r.Running)); // still authorized, not paused: it waits for the terminal

        await Terminal(open: true);
        await Until(r => Item(r, 61).State == ItemState.Open, "the work continues once the terminal is open again");
        Assert.Contains("--resume", Runs[runs]);
        _sup!.Dispose();
    }

    [Fact]
    public async Task ClosingTheTerminalDuringTheBaselineCheckRunsItAgainLater()
    {
        Issues("""[{"number":63,"title":"Small","body":"Polish.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        var ready = P("ready");
        await StartAsync();
        await Tool("agents_control", JsonSerializer.Serialize(new { action = "start", verify = OperatingSystem.IsWindows() ? $"if exist \"{ready}\" (exit 0) else (ping -n 120 127.0.0.1 >nul)" : $"test -f '{ready}' || sleep 120" }));
        await Task.Delay(3000); // the baseline check is under way (or about to be)
        await Terminal(open: false);
        await Task.Delay(3000);
        Assert.False(await On(r => r.Paused)); // stopped, not failed: no "fails on main"
        Assert.Null(await On(r => r.Note));
        File.WriteAllText(ready, "");
        await Terminal(open: true);
        await Until(r => Item(r, 63).State == ItemState.Open, "the baseline runs again and the work goes on"); // a baseline left running would hold it for 2 minutes
        _sup!.Dispose();
    }

    [Fact]
    public async Task AnOfflineIssueScanIsNoProblemAndTheNextScanReadsThem()
    {
        Issues("""[{"number":66,"title":"Later","body":"[fake:slow]","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        File.WriteAllText(Path.Combine(_gh, "offline"), "");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => r.LastScan != null, "the scan runs offline");
        Assert.Null(await On(r => r.Note)); // not "gh is not signed in"
        Assert.False(await _loop.Run(() => _sup!.HasProblem));
        File.Delete(Path.Combine(_gh, "offline"));
        await _loop.Run(() => { _sup!.RepoFor(_repo)!.LastScan = null; _sup.UpdateAccounts(Accounts, []); return 0; });
        await Until(r => r.Items.Any(i => i.Issue == 66), "back online, the issue is read");
        _sup!.Dispose();
    }

    [Fact]
    public async Task ARestartWithoutItsTerminalStopsTheRunUntilItOpens()
    {
        Issues("""[{"number":62,"title":"Long","body":"[fake:slow]","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 62).State == ItemState.Working && Item(r, 62).Run is { Pid: > 0 }, "the run starts");
        var pid = await On(r => Item(r, 62).Run!.Pid);
        await _loop.Run(() => { _sup!.Dispose(); return 0; }); // the tray exits; the terminal is closed meanwhile
        await StartAsync(terminal: false);
        await Until(r => Item(r, 62).State == ItemState.Queued && Item(r, 62).Run is { Live: false }, "the re-attached run stops");
        await Gone(pid);
        Assert.Equal(0, await On(r => Item(r, 62).Failures));
        await Terminal(open: true);
        await Until(r => Item(r, 62).State == ItemState.Open, "the work continues once the terminal opens");
        _sup!.Dispose();
    }

    [Fact]
    public async Task SkippingOrClosingAnIssueMidRunEndsItsWorkForGood()
    {
        Issues("""
            [{"number":70,"title":"Skip me","body":"[fake:long]","labels":[],"assignees":[],"author":{"login":"owner"}},
             {"number":71,"title":"Close me","body":"[fake:long]","labels":[],"assignees":[],"author":{"login":"owner"}}]
            """);
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 70).Run is { Pid: > 0 } && Runs.Length == 1, "the first run starts");
        var first = await On(r => Item(r, 70).Run!.Pid);
        Assert.Contains("is skipped", await Tool("agents_queue", """{"action":"skip","item":"#70"}"""));
        await Until(r => r.Items.All(i => i.Issue != 70) && Item(r, 71).Run is { Pid: > 0 } && Runs.Length == 2, "work moves on to the next issue");
        await Gone(first);

        var second = await On(r => Item(r, 71).Run!.Pid);
        Issues("[]"); // the owner closes #71 on GitHub
        await _loop.Run(() => { _sup!.RepoFor(_repo)!.LastScan = null; _sup.UpdateAccounts(Accounts, []); return 0; });
        await Until(r => r.Items.Count == 0, "the closed issue's work ends");
        await Gone(second);
        await Task.Delay(1500);
        Assert.Equal(2, Runs.Length); // one each, and neither came back
        Assert.Empty(Git(_repo, "branch", "--list", "patch-goblins/*"));
        Assert.DoesNotContain("pr create", Read(Path.Combine(_gh, "gh.log")));
        _sup!.Dispose();
    }

    [Fact]
    public async Task SkippingAnItemDuringVerificationStopsItAndRemovesTheWorktree()
    {
        Issues("""[{"number":90,"title":"Gated","body":"Do it.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        var verifying = P("verifying");
        await StartAsync();
        // Passes on main (the baseline); in the item's worktree it hangs until something stops it.
        await Tool("agents_control", JsonSerializer.Serialize(new { action = "start", verify = OperatingSystem.IsWindows() ? $"if exist issue-90.txt (type nul > \"{verifying}\" & ping -n 120 127.0.0.1 >nul) else (exit 0)" : $"if test -f issue-90.txt; then touch '{verifying}'; sleep 120; fi" }));
        await AgentProcessTests.WaitUntil(() => File.Exists(verifying));
        var wt = await On(r => Item(r, 90).Worktree!);
        Assert.Contains("is skipped", await Tool("agents_queue", """{"action":"skip","item":"#90"}"""));
        await Until(r => r.Items.Count == 0 && !Directory.Exists(wt), "the verification stops and its worktree goes", 30);
        Assert.Empty(Git(_repo, "branch", "--list", "patch-goblins/*"));
        _sup!.Dispose();
    }

    [Fact]
    public async Task RepositoriesTakeTurnsAndAnAssignedFolderKeepsItsAccount()
    {
        Issues("""
        [
          {"number":80,"title":"One","body":"[fake:slow] a","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":81,"title":"Two","body":"[fake:slow] b","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":82,"title":"Three","body":"[fake:slow] c","labels":[],"assignees":[],"author":{"login":"owner"}}
        ]
        """);
        var other = NewRepo("other");
        var config = Fast();
        config.MaxConcurrent = 0; // automatic: three usable accounts, up to three runs at once
        config.Ramp = TimeSpan.FromSeconds(1);
        await StartAsync(config, terminal: false);
        // #2, the best account, is this folder's own. The other folder, listed and served first, has three
        // items of its own: it must neither take #2 nor every turn.
        await _loop.Run(() => { _sup!.UpdateAccounts([.. Accounts, A(4, 50)], [new ProjectMapping(_repo, "a2@example.com", "")]); return 0; });
        await Tool("agents_control", """{"action":"start"}""", other);
        await Tool("agents_control", """{"action":"start"}""");
        await _loop.Run(() => { _sup!.Attach(other); _sup.Attach(_repo); return 0; });
        await Until(r => r.Items.Any(i => i.State == ItemState.Working && i.Run is { Email: "a2@example.com" }), "this folder runs on its own account");
        Assert.True(await On(r => r.Items.Any(i => i.State is ItemState.Working or ItemState.Checking), other), "while the other folder runs on the rest");
        Assert.DoesNotContain("a2@example.com", await On(r => r.Items.Select(i => i.Run?.Email).ToList(), other));
        _sup!.Dispose();
    }

    [Fact]
    public async Task IssuesAtOnceCountsEveryFolder()
    {
        Issues("""[{"number":83,"title":"Held","body":"[fake:hold]","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        var other = NewRepo("other");
        var config = Fast();
        config.Ramp = TimeSpan.FromMilliseconds(100);
        await StartAsync(config, terminal: false); // the default: one issue at once
        await Tool("agents_control", """{"action":"start"}""", other);
        await Tool("agents_control", """{"action":"start"}""");
        await _loop.Run(() => { _sup!.Attach(other); _sup.Attach(_repo); return 0; });
        Func<RepoAgents, bool> busy = r => r.Items.Any(i => i.State is ItemState.Working or ItemState.Checking);
        var until = DateTime.UtcNow.AddSeconds(90);
        while (!await On(busy) && !await On(busy, other)) { Assert.True(DateTime.UtcNow < until, "one folder starts"); await Task.Delay(100); }
        await Task.Delay(3000);
        Assert.False(await On(busy) && await On(busy, other), "both folders work at once");
        _sup!.Dispose();
    }

    [Fact]
    public async Task AFolderWaitingForItsOwnAccountNeitherAlarmsNorFlickersWhileOthersWork()
    {
        Issues("""[{"number":85,"title":"Slow","body":"[fake:slow]","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        var other = NewRepo("other");
        var config = Fast();
        config.Ramp = TimeSpan.FromSeconds(2);
        config.MaxConcurrent = 2; // one each: the cap counts every folder, and this one waits for its account, not for a slot
        await StartAsync(config);
        // This folder's account is out of usage; #3 still has room for the other folder.
        await _loop.Run(() => { _sup!.UpdateAccounts([A(1, 0, active: true), A(2, 95), A(3, 30)], [new ProjectMapping(_repo, "a2@example.com", "")]); return 0; });
        await Tool("agents_control", """{"action":"start"}""");
        await Until(_ => _sup!.Waiting?.StartsWith(AccountPicker.AllNearLimits) == true, "this folder waits for its account");
        await Tool("agents_control", """{"action":"start"}""", other);
        await Terminal(open: true, other);
        var until = DateTime.UtcNow.AddSeconds(90);
        while (!await On(r => r.Items.Any(i => i.State == ItemState.Open), other))
        {
            Assert.NotNull(await _loop.Run(() => _sup!.Waiting)); // the gap between starts forgets nobody
            Assert.True(DateTime.UtcNow < until, "the other folder reaches a pull request");
            await Task.Delay(100);
        }
        Assert.Equal("a3@example.com", await On(r => Item(r, 85).Run!.Email, other));
        Assert.DoesNotContain(_notes, n => n.StartsWith("Agents waiting for usage") || n.StartsWith("Agents resumed")); // #3 was never out
        _sup!.Dispose();
    }

    [Fact]
    public async Task AGoalBecomesOrderedStepsThatReachPullRequests()
    {
        Issues("""
        [
          {"number":5,"title":"Write the docs page","body":"Docs.","labels":[],"assignees":[],"author":{"login":"owner"}},
          {"number":6,"title":"Link the docs","body":"Blocked by #5.","labels":[],"assignees":[],"author":{"login":"owner"}}
        ]
        """);
        Plan("""
            {"status":"working","summary":"greeting first","items":[{"key":"a","title":"Add the greeting module","body":"Add greeting.txt."},{"key":"b","title":"Use the greeting","body":"Print it.","after":["a"]},{"key":"c","issue":5},{"key":"d","issue":6,"after":["c"]}],"memory":["greetings live in greeting.txt"]}
            """);
        await StartAsync(GoalConfig(), terminal: false);
        Assert.Equal("Agents were never started for this repository; start them first (agents_control start).", await Goal(new { action = "set", title = "x" }));
        await Tool("agents_control", """{"action":"start"}""");
        Assert.Equal("Goal g1 set: Greetings. The Director plans the first steps.", await Goal(new { action = "set", title = "Greetings", text = "Greet people." }));
        await Terminal(true);

        // The scan took #5 (it unblocks #6, which waits); the plan orders the steps and puts both issues to work.
        await Until(r => G(r).Plans == 1 && !Directory.Exists(PlanCheckout(r)), "the first plan is in and its checkout is gone");
        var key = await On(r => r.Key);
        var goal = key + "/g1";
        Assert.Equal(goal, await On(r => W(r, "w1").Goal));
        Assert.Equal([key + "/w1"], await On(r => W(r, "w2").After.ToList()));
        Assert.Equal((goal, 10098), await On(r => (Item(r, 5).Goal, Item(r, 5).Rank)));
        Assert.Equal((goal, 10097), await On(r => (Item(r, 6).Goal, Item(r, 6).Rank)));
        Assert.Equal([key + "/5"], await On(r => Item(r, 6).After.ToList()));
        Assert.Contains("Edit Write NotebookEdit", Runs[0]); // the Director reads; it changes nothing
        var director = Assert.Single(DirectorPrompts());
        Assert.Contains("The owner's goal g1: Greetings", director);
        Assert.Contains("- #6 Link the docs", director);
        Assert.Contains("[agent · plan g1] greetings live in greeting.txt", await Tool("agents_memory", """{"action":"list"}"""));

        // Each step waits for the one it builds on.
        await Until(r => W(r, "w1").State == ItemState.Open && Item(r, 5).State == ItemState.Open, "the first steps reach pull requests");
        Assert.True(await On(r => W(r, "w2") is { State: ItemState.Queued, Run: null } && Item(r, 6) is { State: ItemState.Queued, Run: null }));
        var status = await Tool("agents_status", "{}");
        Assert.Contains("Goal g1 Greetings: 0 in progress, 2 pull requests open, 2 queued", status);
        Assert.Contains("Pull request #7 for w1 Add the greeting module (goal g1)", status);

        File.WriteAllText(Path.Combine(_gh, "pr-7.json"), """{"state":"MERGED"}""");
        await Until(r => W(r, "w1") is { State: ItemState.Done, Merged: true } && W(r, "w2").State == ItemState.Open, "the next step starts once the first is merged");
        Assert.Equal(ItemState.Queued, await On(r => Item(r, 6).State));
        Assert.Single(DirectorPrompts()); // the goal's work moved, but the last plan is recent

        Assert.StartsWith("Goal g1: the Director plans again", await Goal(new { action = "replan", goal = "g1" }));
        await Until(_ => DirectorPrompts().Length == 2, "the owner's replan plans now");
        _sup!.Dispose();
    }

    [Fact]
    public async Task AGoalPlansStepsInAnotherRepositoryThatWaitAcrossThem()
    {
        var other = NewRepo("other");
        Plan("""
            {"status":"working","summary":"server first","items":[{"key":"a","title":"Serve greetings","body":"Add serve.txt.","repo":"other"},{"key":"b","title":"Use the server","after":["a"]}]}
            """);
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""", other);
        await Tool("agents_control", """{"action":"start"}""");
        await Tool("agents_queue", """{"action":"add","title":"Tidy the readme","text":"x"}""", other);
        await Goal(new { action = "set", title = "Greetings everywhere" });

        // The step in the other repository waits for its terminal: agents never work there in the background.
        await Until(r => G(r).Plans == 1, "the first plan is in");
        var (key, otherKey) = (await On(r => r.Key), await On(r => r.Key, other));
        Assert.Equal((key + "/g1", ItemState.Queued), await On(r => (W(r, "w2").Goal, W(r, "w2").State), other));
        Assert.Contains("Part of goal g1 of repo: Greetings everywhere", await On(r => W(r, "w2").Body, other));
        Assert.Equal([otherKey + "/w2"], await On(r => W(r, "w1").After.ToList()));
        var director = Assert.Single(DirectorPrompts());
        Assert.Contains("- other (pull requests target main)", director);
        Assert.Contains("- other:w1 Tidy the readme: queued", director); // the other repository's work, so it is not duplicated
        Assert.Contains("Up next: w2 Serve greetings (goal g1 of repo)", await Tool("agents_status", "{}", other));

        await Terminal(true, other);
        await Until(r => W(r, "w2").State == ItemState.Open, "the other repository's step reaches a pull request", repo: other);
        Assert.Equal(ItemState.Queued, await On(r => W(r, "w1").State)); // this repository's step waits for it
        var pr = await On(r => W(r, "w2").Pr!.Value, other);
        File.WriteAllText(Path.Combine(_gh, $"pr-{pr}.json"), """{"state":"MERGED"}""");
        await Until(r => W(r, "w1").State == ItemState.Open, "this repository's step starts once the other one is merged");
        _sup!.Dispose();
    }

    [Fact]
    public async Task AGoalWhoseStepIsInAStoppedRepositoryAsksTheOwner()
    {
        var other = NewRepo("other");
        Plan("""
            {"status":"working","summary":"server first","items":[{"key":"a","title":"Serve greetings","repo":"other"},{"key":"b","title":"Use the server","after":["a"]}]}
            """);
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""", other);
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Greetings everywhere" });
        await Until(r => G(r).Plans == 1, "the first plan is in");

        await Tool("agents_control", """{"action":"stop"}""", other);
        await Until(r => G(r).State == GoalState.NeedsOwner, "a step agents will never take up asks the owner");
        Assert.Equal("w1 Serve greetings is in other, where agents are stopped; skip it there, or start them there and ask for a new plan", await On(r => G(r).Note));
        // The path the note names works: once agents run there again, a new plan is not sent straight back to the owner.
        await Tool("agents_control", """{"action":"start"}""", other);
        Assert.StartsWith("Goal g1: the Director plans again", await Goal(new { action = "replan", goal = "g1" }));
        await Until(_ => DirectorPrompts().Length == 2, "the owner's replan plans now");
        _sup!.Dispose();
    }

    [Fact]
    public async Task SkippingTheStepAGoalAskedAboutPutsTheGoalBackToWork()
    {
        var other = NewRepo("other");
        Plan("""
            {"status":"working","summary":"server first","items":[{"key":"a","title":"Serve greetings","repo":"other"},{"key":"b","title":"Use the server","after":["a"]}]}
            """);
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""", other);
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Greetings everywhere" });
        await Until(r => G(r).Plans == 1, "the first plan is in");
        await Tool("agents_control", """{"action":"stop"}""", other);
        await Until(r => G(r).State == GoalState.NeedsOwner, "a step agents will never take up asks the owner");

        Assert.Contains("is skipped", await Tool("agents_queue", """{"action":"skip","item":"w1"}""", other)); // what the note offers
        Assert.Equal((GoalState.Active, (string?)null), await On(r => (G(r).State, G(r).Note)));
        await Until(_ => DirectorPrompts().Length == 2, "the goal plans again without the skipped step");
        _sup!.Dispose();
    }

    [Fact]
    public async Task AClaimIsRefutedByAFailingCheckThenAchievedOnceItPasses()
    {
        var ready = P("ready");
        var check = OperatingSystem.IsWindows() ? $"if exist \"{ready}\" (exit 0) else (exit 1)" : $"test -f '{ready}'";
        Plan("""{"status":"achieved","summary":"already there","evidence":"it already works"}""");
        Plan("""{"status":"achieved","summary":"ready now","evidence":"ready is in place"}""");
        var since = DateTimeOffset.Now;
        await StartAsync(GoalConfig(TimeSpan.FromSeconds(8)));
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Be ready", checks = new[] { check } });

        await Until(r => G(r).Refuted?.Contains("failed on main") == true, "Patch Goblins' own check refutes the claim");
        Assert.Null(await On(r => G(r).Claim));
        File.WriteAllText(ready, "");
        await Until(r => G(r).State == GoalState.Achieved, "the next claim passes the check");
        var evidence = await On(r => G(r).Evidence!);
        Assert.StartsWith("ready is in place · verified on main at ", evidence);
        Assert.Contains($"`{check}` passed", evidence);
        Assert.Equal(1, await On(r => G(r).Idle)); // a claim again right after a refutation counts toward the idle cap
        Assert.Contains("The last plan said this goal was achieved, but Patch Goblins' check of it failed:", DirectorPrompts()[^1]);
        Assert.Contains("Goal achieved: repo · Be ready", _notes);
        Assert.Contains("[agent · goal g1] Achieved goal Be ready", await Tool("agents_memory", """{"action":"list"}"""));
        Assert.Contains("1 goal achieved", await _loop.Run(() => _sup!.Summary(since)));
        Assert.Contains("Goal g1 Be ready: achieved: ready is in place", await Tool("agents_status", "{}"));
        _sup!.Dispose();
    }

    [Fact]
    public async Task AClaimWaitsForItsOpenWorkAndMergedWorkProvesIt()
    {
        Plan("""{"status":"working","summary":"one step","items":[{"key":"a","title":"Add thing","body":"Add it."}]}""");
        Plan("""{"status":"achieved","summary":"once merged","evidence":"thing is in"}""");
        await StartAsync(GoalConfig(TimeSpan.FromSeconds(1)));
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Thing" });

        // The step's pull request moved the goal's work, so the Director planned again; it claims the goal.
        await Until(r => W(r, "w1").State == ItemState.Open && G(r).Claim == "thing is in", "the second plan claims the goal while its step is open");
        await Task.Delay(2000);
        Assert.Equal((GoalState.Active, 2), await On(r => (G(r).State, G(r).Plans))); // nothing is checked while its work is open

        File.WriteAllText(Path.Combine(_gh, "pr-7.json"), """{"state":"MERGED"}""");
        await Until(r => G(r).State == GoalState.Achieved, "merged work proves the claim");
        Assert.Equal("thing is in · merged #7", await On(r => G(r).Evidence));
        Assert.Equal(2, DirectorPrompts().Length); // the check won over a replan
        _sup!.Dispose();
    }

    [Fact]
    public async Task BeingOfflineDuringTheCheckIsNoFailure()
    {
        Plan("""{"status":"achieved","summary":"ready","evidence":"it is ready"}""");
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""");
        Git(_repo, "remote", "set-url", "origin", "http://offline.invalid/r.git"); // fetching fails, as when offline
        await Goal(new { action = "set", title = "Be ready", checks = new[] { "exit 0" } });
        var fetched = typeof(Supervisor).GetField("_fetched", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        // The check fetches the base afresh (strict); forget earlier fetches until it tries.
        await Until(r =>
        {
            ((System.Collections.IDictionary)fetched.GetValue(_sup)!).Clear();
            return G(r).Log.Any(l => l.Contains("check postponed"));
        }, "the check waits for the network");
        Assert.Equal((GoalState.Active, 0), await On(r => (G(r).State, G(r).Failures)));
        _sup!.Dispose();
    }

    [Fact]
    public async Task AClaimWithNothingToProveItAsksAndAnAnswerWithACheckSettlesIt()
    {
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""");
        await _loop.Run(() => { _sup!.ConfirmChecks = (_, _) => Task.FromResult(false); return 0; });
        Assert.Equal("The owner did not confirm the checks in the Patch Goblins dialog, so nothing changed.",
            await Goal(new { action = "set", title = "Nothing to show", checks = new[] { "exit 0" } }));
        Assert.Empty(await On(r => r.Goals.ToList()));
        await _loop.Run(() => { _sup!.ConfirmChecks = (_, _) => Task.FromResult(true); return 0; });

        Plan("""{"status":"achieved","summary":"done","evidence":"it is there"}""");
        await Goal(new { action = "set", title = "Nothing to show" });
        await Until(r => G(r).State == GoalState.NeedsOwner, "a claim nothing can prove asks the owner");
        Assert.Contains("there is no check to prove it", await On(r => G(r).Note));
        Assert.Contains(_notes, n => n.StartsWith("Agents need you: repo · goal g1: the Director believes this is achieved (it is there)"));
        Assert.False(await _loop.Run(() => _sup!.HasProblem)); // a question, not a breakdown
        Assert.Equal("attention", await On(AgentsTerminal.StateOf));

        Plan("""{"status":"achieved","summary":"done","evidence":"exit 0 passes"}""");
        Assert.StartsWith("Goal g1: the Director plans again", await Goal(new { action = "answer", goal = "g1", checks = new[] { "exit 0" } }));
        await Until(r => G(r).State == GoalState.Achieved, "the owner's check proves the next claim");
        Assert.Contains("`exit 0` passed", await On(r => G(r).Evidence));

        await Goal(new { action = "set", title = "Second" });
        Assert.Equal("Goal g2 is achieved.", await Goal(new { action = "done", goal = "g2" }));
        Assert.Equal((GoalState.Achieved, "confirmed by the owner"), await On(r => (G(r, "g2").State, G(r, "g2").Evidence)));
        _sup!.Dispose();
    }

    [Fact]
    public async Task AGoalCheckTheRunnersShellCannotRunIsRefusedBeforeTheOwnerIsAsked()
    {
        var c = GoalConfig();
        c.Shell = CheckShell.Kind.Cmd; // Windows, as it runs: cmd.exe
        await StartAsync(c);
        await Tool("agents_control", """{"action":"start"}""");
        var asked = 0;
        await _loop.Run(() => { _sup!.ConfirmChecks = (_, _) => { asked++; return Task.FromResult(true); }; return 0; });
        const string posix = "for i in $(seq 1 60); do pg_isready && break; sleep 2; done && DB_URL=postgres://x npx vitest run";
        var refused = await Goal(new { action = "set", title = "Api", checks = new[] { "npm test", posix } });
        Assert.StartsWith("Nothing changed. The check `for i in $(seq 1 60)", refused);
        Assert.Contains("cmd.exe", refused);
        Assert.Contains("powershell -NoProfile", refused); // and how to write it so it runs
        Assert.Equal(0, asked); // never shown to the owner
        Assert.Empty(await On(r => r.Goals.ToList()));

        // Written for an explicit shell, the same check is that shell's to run, and the owner sees it exactly.
        IReadOnlyList<string>? shown = null;
        await _loop.Run(() => { _sup!.ConfirmChecks = (_, cs) => { shown = cs; return Task.FromResult(true); }; return 0; });
        var wrapped = $"bash -c '{posix}'";
        Assert.StartsWith("Goal g1 set", await Goal(new { action = "set", title = "Api", checks = new[] { "npm test", wrapped } }));
        Assert.Equal(["npm test", wrapped], shown);
        Assert.Equal(["npm test", wrapped], await On(r => G(r).Checks.ToList()));

        // A check refused in an answer leaves the goal's confirmed checks as they were.
        Assert.StartsWith("Nothing changed.", await Goal(new { action = "answer", goal = "g1", checks = new[] { "X=1 npm test" } }));
        Assert.Equal(["npm test", wrapped], await On(r => G(r).Checks.ToList()));
        _sup!.Dispose();
    }

    [Fact]
    public async Task OnLinuxOrMacTheSameUnixCheckIsAcceptedAndACmdOneRefused()
    {
        var c = GoalConfig();
        c.Shell = CheckShell.Kind.Posix;
        await StartAsync(c);
        await Tool("agents_control", """{"action":"start"}""");
        Assert.StartsWith("Goal g1 set", await Goal(new { action = "set", title = "Api", checks = new[] { "DB_URL=postgres://x npx vitest run" } }));
        Assert.StartsWith("Nothing changed. The check `set DB_URL=x&& npx vitest run`", await Goal(new { action = "answer", goal = "g1", checks = new[] { "set DB_URL=x&& npx vitest run" } }));
        _sup!.Dispose();
    }

    [Fact]
    public async Task ASavedCheckThisShellCannotRunAsksTheOwnerInsteadOfFailingTheClaim()
    {
        var c = GoalConfig();
        c.Shell = CheckShell.Kind.Cmd;
        await StartAsync(c);
        await Tool("agents_control", """{"action":"start"}""");
        Plan("""{"status":"working","summary":"later"}""");
        await Goal(new { action = "set", title = "Api", checks = new[] { "exit 0" } });
        await Until(r => G(r).Plans == 1, "the first plan is in");
        // A goal saved before checks were judged (Drill Desk's g2): kept as it is, never rewritten.
        await _loop.Run(() => { G(_sup!.RepoFor(_repo)!).Checks = ["exit 0", "DB_URL=x npx vitest run; rc=$?; exit $rc"]; return 0; });
        Plan("""{"status":"achieved","summary":"done","evidence":"vitest passes"}""");
        await Goal(new { action = "replan", goal = "g1" });
        await Until(r => G(r).State == GoalState.NeedsOwner, "the owner replaces the check");
        var note = await On(r => G(r).Note!);
        Assert.Contains("`DB_URL=x npx vitest run; rc=$?; exit $rc` cannot run here", note);
        Assert.Null(await On(r => G(r).Refuted)); // not run, so not a failed check
        Assert.Equal(["exit 0", "DB_URL=x npx vitest run; rc=$?; exit $rc"], await On(r => G(r).Checks.ToList()));
        _sup!.Dispose();
    }

    [Fact]
    public async Task ANewPolicyCheckOrVerificationTheShellCannotRunStopsTheStart()
    {
        var c = Fast();
        c.Shell = CheckShell.Kind.Cmd;
        CommitPolicy("""{"version":1,"checks":[{"run":"CI=1 git --version"}]}""");
        await StartAsync(c);
        var refused = await Tool("agents_control", """{"action":"start"}""");
        Assert.StartsWith("Nothing started: The command `CI=1 git --version` cannot run here", refused);
        Assert.Contains("\"os\"", refused);
        Assert.False(await On(r => r?.Authorized ?? false));
        // Marked for the system it was written for, it is skipped here, as policy checks for another system always were.
        CommitPolicy("""{"version":1,"checks":[{"run":"CI=1 git --version","os":"linux"}]}""");
        Assert.StartsWith("Nothing started: The command `export CI=1", await Tool("agents_control", """{"action":"start","verify":"export CI=1 && git --version"}"""));
        Assert.StartsWith("Started.", await Tool("agents_control", """{"action":"start","verify":"set CI=1&& git --version"}"""));
        _sup!.Dispose();
    }

    [Fact]
    public async Task ABlockedPlanWaitsForTheOwnersAnswerAndTheNextPlanCarriesIt()
    {
        Plan("""{"status":"blocked","summary":"tone","question":"Formal or casual?"}""");
        Plan("""{"status":"working","summary":"formal","items":[{"key":"a","title":"Add a formal greeting","body":"Formal."}]}""");
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Greet" });

        await Until(r => G(r).State == GoalState.NeedsOwner, "the Director asks the owner");
        Assert.Contains("Goal g1 Greet: needs you: Formal or casual?", await Tool("agents_status", "{}"));
        Assert.Contains("Agents need you: repo · goal g1: Formal or casual?", _notes);

        Assert.Equal("Goal g1: the Director plans again with your answer.", await Goal(new { action = "answer", goal = "g1", text = "Formal." }));
        await Until(r => r.Items.Any(i => i.Id == "w1"), "the next plan adds a step");
        var last = DirectorPrompts()[^1];
        Assert.Contains("The owner's answer to the Director's last question (it binds you):", last);
        Assert.Contains("Formal.", last);
        Assert.Null(await On(r => G(r).Guidance)); // delivered once
        Assert.Contains("[owner] goal g1 Greet: Formal.", await Tool("agents_memory", """{"action":"list"}"""));
        _sup!.Dispose();
    }

    [Fact]
    public async Task PlanningThatKeepsFailingNeedsTheOwnerAndLightsTheDot()
    {
        for (var k = 0; k < 3; k++) Plan("not a plan");
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Fragile" });

        await Until(r => G(r).State == GoalState.NeedsOwner, "planning fails three times");
        Assert.Equal(3, await On(r => G(r).Failures));
        Assert.StartsWith("planning failed 3 times: its answer had no valid plan", await On(r => G(r).Note));
        Assert.True(await _loop.Run(() => _sup!.HasProblem));
        Assert.Contains(_notes, n => n.StartsWith("Agents need you: repo · goal g1: planning failed 3 times"));

        await Goal(new { action = "answer", goal = "g1", text = "Try again." });
        await Until(r => G(r).Plans == 1, "an answer plans again");
        Assert.Equal(0, await On(r => G(r).Failures));
        Assert.False(await _loop.Run(() => _sup!.HasProblem));
        _sup!.Dispose();
    }

    [Fact]
    public async Task PlansThatFindNothingParkTheGoal()
    {
        var config = GoalConfig(TimeSpan.FromSeconds(1));
        config.IdleReplan = TimeSpan.FromSeconds(1);
        await StartAsync(config);
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Vague" });

        await Until(r => G(r).State == GoalState.NeedsOwner, "three plans that change nothing park the goal");
        Assert.Equal((3, 3), (await On(r => G(r).Plans), DirectorPrompts().Length));
        Assert.StartsWith("the Director found nothing more to do", await On(r => G(r).Note));
        Assert.False(await _loop.Run(() => _sup!.HasProblem)); // a question, not a breakdown
        await Task.Delay(2500);
        Assert.Equal(3, DirectorPrompts().Length);
        _sup!.Dispose();
    }

    [Fact]
    public async Task AStepWhosePredecessorClosedUnmergedAsksTheOwnerAndSkippingAStepDetachesItsDependents()
    {
        await StartAsync(GoalConfig(TimeSpan.FromSeconds(1)));
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Chain" });
        await Until(r => G(r).Plans == 1, "the first plan is in");
        var key = await On(r => r.Key);
        await _loop.Run(() =>
        {
            var r = _sup!.RepoFor(_repo)!;
            r.Items.Add(new WorkItem { Id = "w1", Title = "Closed", State = ItemState.Done, Pr = 7, Goal = key + "/g1", Rank = 10100 });
            r.Items.Add(new WorkItem { Id = "w2", Title = "Waits", Goal = key + "/g1", After = [key + "/w1"], Rank = 10099 });
            r.Items.Add(new WorkItem { Id = "w3", Title = "Skipped", Goal = key + "/g1", After = [key + "/w2"], Rank = 10098 });
            r.Items.Add(new WorkItem { Id = "w4", Title = "Also waits", Goal = key + "/g1", After = [key + "/w1"], Rank = 10097 });
            _sup.Poke();
            return 0;
        });
        await Until(r => G(r).State == GoalState.NeedsOwner, "a step that can never start asks the owner");
        Assert.Contains("closed without merging", await On(r => G(r).Note));
        Assert.Equal(ItemState.Queued, await On(r => W(r, "w2").State)); // it never started
        Assert.Contains("is skipped", await Tool("agents_queue", """{"action":"skip","item":"w4"}"""));
        Assert.Equal(GoalState.NeedsOwner, await On(r => G(r).State)); // the question was about w2: it stays

        // Skipping a step takes what was planned on it out of the goal, so it cannot start without it, and a claim
        // that counted on it.
        await _loop.Run(() => G(_sup!.RepoFor(_repo)!).Claim = "done once w2 lands");
        Assert.Contains("is skipped", await Tool("agents_queue", """{"action":"skip","item":"w2"}"""));
        Assert.False(await On(r => r.Items.Any(i => i.Id == "w3"))); // a request planned on it is no longer in the goal
        Assert.Equal(GoalState.Active, await On(r => G(r).State)); // that answered the question about it
        Assert.Null(await On(r => G(r).Claim));
        _sup!.Dispose();
    }

    [Fact]
    public async Task SkippingAStepStopsTheGoalsRunningPlanner()
    {
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Chain", text = "[fake:hold] Plan slowly." });
        await Until(r => G(r).Run is { Live: true, Pid: > 0 }, "the planner runs");
        await _loop.Run(() =>
        {
            var r = _sup!.RepoFor(_repo)!;
            r.Items.Add(new WorkItem { Id = "w1", Title = "Step", Goal = r.Key + "/g1", Rank = 10100 });
            return 0;
        });
        Assert.Contains("is skipped", await Tool("agents_queue", """{"action":"skip","item":"w1"}"""));
        await Until(r => G(r).Run is not { Live: true }, "its stale plan is not waited for");
        _sup!.Dispose();
    }

    [Fact]
    public async Task AGoalStepWhoseIssueClosedClearsTheGoalsClaim()
    {
        // Held while it works: the issue closes during the work, never during its check (which finishes what it started).
        const string docs = """{"number":5,"title":"Write the docs page","body":"[fake:hold] Docs.","assignees":[],"author":{"login":"owner"},"labels":[]}""";
        Issues($"[{docs}]");
        Plan("""{"status":"working","summary":"docs","items":[{"key":"b","issue":5}]}""");
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Docs" });
        await Until(r => r.Items.Any(i => i.Issue == 5 && i.Goal != null && i.State == ItemState.Working), "the planned issue is in the goal, at work");
        await _loop.Run(() => G(_sup!.RepoFor(_repo)!).Claim = "done once #5 lands");
        Issues("[]");
        await _loop.Run(() => { _sup!.RepoFor(_repo)!.LastScan = null; _sup.Poke(); return 0; });
        await Until(r => !r.Items.Any(i => i.Issue == 5), "the closed issue's item goes", 150); // its held run ends within the fake's minute
        Assert.Equal((null, GoalState.Active), await On(r => (G(r).Claim, G(r).State))); // not Achieved on work that ended unmerged
        File.WriteAllText(_log + ".release", "");
        _sup!.Dispose();
    }

    [Fact]
    public async Task PlannedIssueWorkTheOwnerHoldsLeavesTheGoalAndTheQueue()
    {
        const string docs = """{"number":5,"title":"Write the docs page","body":"Docs.","assignees":[],"author":{"login":"owner"},"labels":""";
        Issues($"[{docs}[]}}]");
        Plan("""{"status":"working","summary":"docs","items":[{"key":"a","title":"Held one","body":"[fake:hold] one"},{"key":"b","issue":5},{"key":"c","title":"Link the docs","body":"Link them.","after":["b"]}]}""");
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Docs" });
        await Until(r => W(r, "w1").State == ItemState.Working && Item(r, 5).Goal != null && W(r, "w2").After.Count == 1, "the first step works while the planned issue waits");

        // The owner puts #5 on hold before it starts: the next scan judges it as it judges any work it found.
        Issues($$"""[{{docs}}[{"name":"on hold"}]}]""");
        await _loop.Run(() => { _sup!.RepoFor(_repo)!.LastScan = null; _sup.Poke(); return 0; });
        await Until(r => !r.Items.Any(i => i.Issue == 5), "the held issue leaves the queue");
        Assert.False(await On(r => r.Items.Any(i => i.Id == "w2"))); // planned on it, so it goes too, not started without it
        Assert.Equal(ItemState.Working, await On(r => W(r, "w1").State));
        File.WriteAllText(_log + ".release", "");
        _sup!.Dispose();
    }

    const string Gh12 = """{"number":12,"title":"Twelve","body":"One.","assignees":[],"author":{"login":"owner"},"labels":[]}""";
    const string Gh8 = """{"number":8,"title":"Eight","body":"Blocked by #12.","assignees":[],"author":{"login":"owner"},"labels":[]}""";

    /// A busy step holds the only slot; the plan adopts #12 and #8 (blocked by #12), which wait on the ramp.
    async Task AdoptTwelveAndEightAsync()
    {
        Issues($"[{Gh12},{Gh8}]");
        Plan("""{"status":"working","summary":"three","items":[{"key":"a","title":"Busy","body":"[fake:hold] busy"},{"key":"b","issue":12},{"key":"c","issue":8}]}""");
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Pair" });
        await Until(r => W(r, "w1").State == ItemState.Working && Item(r, 12).Goal != null && Item(r, 8).Goal != null, "the busy step works while both wait");
    }

    async Task RescanAsync()
    {
        await _loop.Run(() => { _sup!.RepoFor(_repo)!.LastScan = null; _sup.Poke(); return 0; });
        // The scan stamps LastScan as it starts, inside the pass that then judges (a planner reads the same pull request list).
        await Until(r => r.LastScan != null && !_sup!.Pumping, "the scan judged the queue");
    }

    [Fact]
    public async Task APlannedStepStaysWhenItsMergedBlockersIssueIsStillOpen()
    {
        await AdoptTwelveAndEightAsync();
        await _loop.Run(() => { var i = Item(_sup!.RepoFor(_repo)!, 12); (i.State, i.Pr, i.Merged, i.Worktree) = (ItemState.Done, 3, true, null); return 0; });
        await RescanAsync();
        Assert.NotNull(await On(r => Item(r, 8).Goal)); // agents' pull requests do not close issues: #12 stays open after it merged
        File.WriteAllText(_log + ".release", "");
        _sup!.Dispose();
    }

    [Fact]
    public async Task ClosingAMergedPredecessorsIssueLeavesWhatWaitsOnIt()
    {
        await AdoptTwelveAndEightAsync();
        await _loop.Run(() => { var i = Item(_sup!.RepoFor(_repo)!, 12); (i.State, i.Pr, i.Merged, i.Worktree, i.Goal) = (ItemState.Done, 3, true, null, null); return 0; });
        Issues($"[{Gh8.Replace("Blocked by #12.", "")}]"); // the owner closed #12
        await RescanAsync();
        Assert.Equal((ItemState.Queued, true), await On(r => (Item(r, 8).State, Item(r, 8).Goal != null)));
        File.WriteAllText(_log + ".release", "");
        _sup!.Dispose();
    }

    [Fact]
    public async Task AClosedUnmergedPredecessorHoldsWhatWaitsOnIt()
    {
        await AdoptTwelveAndEightAsync();
        await _loop.Run(() => { var i = Item(_sup!.RepoFor(_repo)!, 12); (i.State, i.Pr, i.Merged, i.Worktree, i.Goal) = (ItemState.Done, 3, false, null, null); return 0; });
        Issues($"[{Gh8}]"); // the owner closed #12 without its pull request merging
        await RescanAsync();
        Assert.Equal(ItemState.NeedsOwner, await On(r => Item(r, 8).State));
        File.WriteAllText(_log + ".release", "");
        _sup!.Dispose();
    }

    [Fact]
    public async Task APredecessorPrunedByTheScanHoldsWhatWaitsOnIt()
    {
        await AdoptTwelveAndEightAsync();
        await _loop.Run(() => { var i = Item(_sup!.RepoFor(_repo)!, 12); (i.Goal, i.OwnRank, i.Rank, i.After) = (null, null, 0, []); return 0; }); // an ordinary find now
        Issues($"[{Gh12.Replace("\"labels\":[]", "\"labels\":[{\"name\":\"on hold\"}]")},{Gh8}]");
        await RescanAsync();
        Assert.False(await On(r => r.Items.Any(i => i.Issue == 12)));
        Assert.Equal(ItemState.NeedsOwner, await On(r => Item(r, 8).State));
        File.WriteAllText(_log + ".release", "");
        _sup!.Dispose();
    }

    [Fact]
    public async Task AnsweringAHeldPlannedIssueKeepsItThroughTheNextScan()
    {
        await AdoptTwelveAndEightAsync();
        Assert.Contains("is skipped", await Tool("agents_queue", """{"action":"skip","item":"#12"}"""));
        Assert.Equal(ItemState.NeedsOwner, await On(r => Item(r, 8).State));
        await Tool("agents_queue", """{"action":"answer","item":"#8"}""");
        await RescanAsync();
        Assert.Equal((ItemState.Queued, true), await On(r => (Item(r, 8).State, Item(r, 8).Goal != null))); // the owner chose to run it
        File.WriteAllText(_log + ".release", "");
        _sup!.Dispose();
    }

    [Fact]
    public async Task ClosingTheTerminalStopsAPlannerFreeAndARestartReattachesIt()
    {
        Plan("""{"status":"working","summary":"one step","items":[{"key":"a","title":"Greet","body":"Say hello."}]}""");
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Greet", text = "[fake:hold] Greet people." });
        await Until(r => G(r).Run is { Live: true, Pid: > 0 } && DirectorPrompts().Length == 1, "the planner starts");
        var pid = await On(r => G(r).Run!.Pid);

        await Terminal(open: false);
        await Until(r => G(r).Run is { Live: false } && !Directory.Exists(PlanCheckout(r)), "the planner stops with its terminal and its checkout goes");
        await Gone(pid);
        Assert.Equal((0, true, 0), await On(r => (G(r).Failures, G(r).Replan, G(r).Plans))); // nothing failed: it plans again

        await Terminal(open: true);
        await Until(r => G(r).Run is { Live: true, Pid: > 0 } run && run.Pid != pid, "the planner starts again once the terminal opens");
        var second = await On(r => G(r).Run!.Pid);
        await _loop.Run(() => { _sup!.Dispose(); return 0; }); // the tray exits; the planner carries on
        Assert.False(Process.GetProcessById(second).HasExited);
        await StartAsync(GoalConfig());
        Assert.Equal((second, true), await On(r => (G(r).Run!.Pid, G(r).Run!.Live))); // attached, not reaped and planned again
        File.WriteAllText(_log + ".release", ""); // the planner answers only now, to the new supervisor
        await Until(r => G(r).Plans == 1, "the re-attached planner's plan is used");
        Assert.Equal(2, DirectorPrompts().Length);
        Assert.Equal(0, await On(r => G(r).Failures));
        Assert.True(await On(r => r.Items.Any(i => i.Id == "w1")));
        _sup!.Dispose();
    }

    [Fact]
    public async Task DroppingAGoalRemovesItsUnstartedStepsAndKeepsWorkUnderWay()
    {
        Plan("""{"status":"working","summary":"two","items":[{"key":"a","title":"Slow one","body":"[fake:slow] one"},{"key":"b","title":"Second","body":"two","after":["a"]}]}""");
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Two steps" });
        await Until(r => r.Items.Any(i => i.Id == "w1" && i.State == ItemState.Working), "the first step starts");

        Assert.Equal("Goal g1 dropped. 1 planned step(s) that had not started no longer serve it. Work under way continues as ordinary items.",
            await Goal(new { action = "drop", goal = "g1" }));
        Assert.False(await On(r => r.Items.Any(i => i.Id == "w2")));
        await Until(r => W(r, "w1").State == ItemState.Open, "the step under way still reaches a pull request");
        Assert.Equal(GoalState.Dropped, await On(r => G(r).State));
        Assert.DoesNotContain("Goal g1", await Tool("agents_status", "{}"));
        Assert.Contains("Goal g1 Two steps: dropped (dropped by the owner)", await Tool("agents_status", """{"detail":true}"""));
        _sup!.Dispose();
    }

    [Fact]
    public async Task APlannerAtItsLimitMovesToAnotherAccount()
    {
        Plan("""{"status":"working","summary":"one","items":[{"key":"a","title":"Greet","body":"Say hello."}]}""");
        await StartAsync(GoalConfig());
        await Tool("agents_control", """{"action":"start"}""");
        await Goal(new { action = "set", title = "Quota", text = "[fake:quota] Greet." });
        await Until(r => G(r).Plans == 1, "the plan comes from another account");
        Assert.StartsWith("run 2 ", Runs[0]);
        Assert.StartsWith("run 3 ", Runs[1]);
        Assert.All(Runs[..2], r => Assert.Contains("NotebookEdit", r));
        Assert.Equal(0, await On(r => G(r).Failures)); // a limit is not the plan's fault
        _sup!.Dispose();
    }

    [Fact]
    public async Task AHungGhCallIsKilledAndLoggedAtItsTimeout()
    {
        var hung = new Cli(_cli.Git, Engine.FindOnPath(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh")!) { GhTimeout = TimeSpan.FromSeconds(1) };
        var r = await hung.GhAsync(_repo, "-NoProfile", "-Command", "Start-Sleep 60");
        Assert.Equal(124, r.Code);
        Assert.Contains("timed out after 1 s", Read(Path.Combine(P("data"), "patch-goblins.log")));
    }

    /// The repository's own policy, merged on main as the owner would.
    void CommitPolicy(string json)
    {
        File.WriteAllText(Path.Combine(_repo, PatchGoblins.Policy.FileName), json);
        Git(_repo, "add", ".");
        Git(_repo, "commit", "--quiet", "-m", "policy");
        Git(_repo, "push", "--quiet", "origin", "main");
    }

    [Fact]
    public async Task ASensitiveChangeGetsAnAdversarialReviewAndWaitsForTheOwnerEvenAcrossARestart()
    {
        CommitPolicy("""{"version":1,"sensitive":["deploy/**"],"checks":[{"run":"git --version","paths":["issue-*.txt"]},{"run":"exit 1","paths":["never/**"]},{"run":"exit 1","os":"linux"}]}""");
        Issues("""[{"number":50,"title":"Deploy script","body":"[fake:touch=deploy/run.txt] Add it.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        string? shown = null;
        await _loop.Run(() => { _sup!.Authorize = (_, v, _) => { shown = v; return Task.FromResult(true); }; return 0; });
        Assert.StartsWith("Started.", await Tool("agents_control", """{"action":"start"}"""));
        Assert.Equal("`git --version` when issue-*.txt change\n`exit 1` when never/** change\n`exit 1` (linux only)", shown); // the owner confirms the policy's checks

        await Until(r => Item(r, 50) is { State: ItemState.NeedsOwner, HoldFor: "sensitive" }, "the sensitive change waits for the owner");
        Assert.DoesNotContain("patch-goblins/50", Git(_repo, "ls-remote", "origin"));
        Assert.Contains("deploy/run.txt", await On(r => Item(r, 50).Note!));
        Assert.Contains("--model opus --effort high", Runs.Single(r => r.Contains("NotebookEdit")));
        var review = Read(_log + ".prompts").Split("---- account").Single(p => p.Contains("You are reviewing"));
        Assert.Contains("Review them adversarially", review);
        Assert.Contains("deploy/run.txt", review);

        // Patch Goblins restarting changes nothing: still held, still not pushed.
        await _loop.Run(() => { _sup!.Dispose(); return 0; });
        await StartAsync();
        await Task.Delay(3000);
        Assert.Equal("sensitive", await On(r => Item(r, 50).HoldFor));
        Assert.DoesNotContain("patch-goblins/50", Git(_repo, "ls-remote", "origin"));

        await Tool("agents_queue", """{"action":"answer","item":"#50"}""");
        await Until(r => Item(r, 50).State == ItemState.Open, "the owner's yes pushes it");
        var body = Read(Path.Combine(_gh, "pr-body-7.md"));
        Assert.Contains("passed: `git --version` at", body); // selected by the changed paths
        Assert.Contains("not run here: `exit 1` needs linux", body); // covers the change, but proves nothing here
        Assert.DoesNotContain("never/**", body);
        Assert.Contains("pushed with the owner's approval", body);

        // CI that only skipped proves nothing, and a head someone else pushed was never verified.
        File.WriteAllText(Path.Combine(_gh, "pr-7.json"), """{"state":"OPEN","mergeable":"MERGEABLE","statusCheckRollup":[{"name":"build","status":"COMPLETED","conclusion":"SKIPPED"}],"comments":[],"reviews":[],"headRefOid":"0123456789abcdef0123456789abcdef01234567"}""");
        await Until(r => Item(r, 50).Verification?.LastOrDefault() == "CI on that unverified head: skipped: no evidence", "the pull request's CI is judged");
        Assert.Contains("(CI on that unverified head: skipped: no evidence)", await Tool("agents_status", "{}"));
        Assert.Contains("stale: the pull request's head is now 0123456", await Tool("agents_details", """{"item":"#50"}"""));
        _sup!.Dispose();
    }

    [Fact]
    public async Task ACommitThatSkipsCiIsReworkedAndAnAskPolicyHoldsEveryPush()
    {
        CommitPolicy("""{"version":1,"push":"ask","deny":["Bash(ddc *)"]}""");
        Issues("""[{"number":51,"title":"Quiet fix","body":"[fake:skipci] Fix it.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start","verify":"git --version"}""");
        await Until(r => Item(r, 51) is { State: ItemState.NeedsOwner, HoldFor: "push" }, "the push waits for the owner");
        Assert.Contains("tells GitHub to skip CI", Read(_log + ".prompts"));
        Assert.False(PatchGoblins.Policy.SkipsCi(Git(await On(r => Item(r, 51).Worktree!), "log", "--format=%B", "origin/main..HEAD")));
        Assert.DoesNotContain("patch-goblins/51", Git(_repo, "ls-remote", "origin"));
        Assert.All(Runs.Where(r => !r.Contains("NotebookEdit")), r => Assert.Contains("Bash(ddc *)", r));
        _sup!.Dispose();
    }

    [Fact]
    public async Task WithoutApprovalsAnAskPolicyPushesWithoutWaiting()
    {
        CommitPolicy("""{"version":1,"push":"ask"}""");
        Issues("""[{"number":52,"title":"Quiet fix","body":"Fix it.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        var c = Fast();
        c.AskApproval = false;
        await StartAsync(c);
        await Tool("agents_control", """{"action":"start","verify":"git --version"}""");
        await Until(r => Item(r, 52).Pr != null, "the pull request opens without the owner's yes");
        Assert.Null(await On(r => Item(r, 52).HoldFor));
        _sup!.Dispose();
    }

    [Fact]
    public async Task AnUnreadablePolicyOrAMovedPinStopsTheStart()
    {
        await StartAsync();
        var local = Path.Combine(P("data"), "policies", "repo.json"); // the owner's, outside the repository
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, """{"version":2}""");
        var refused = await Tool("agents_control", """{"action":"start"}""");
        Assert.StartsWith("Nothing started:", refused);
        Assert.Contains("policy version 2", refused);
        File.WriteAllText(local, """{"version":1,"base":"main@0000000"}""");
        refused = await Tool("agents_control", """{"action":"start"}""");
        Assert.StartsWith("Nothing started: origin/main is at", refused);
        Assert.Contains("not the pinned 0000000", refused);
        Assert.False(await On(r => r?.Authorized ?? false));
        File.WriteAllText(local, """{"version":1,"base":"main@"}"""); // an empty pin would match any tip
        Assert.Contains("base is a branch, or branch@commit", await Tool("agents_control", """{"action":"start"}"""));
        File.WriteAllText(local, """{"version":1,"base":"develop"}""");
        Assert.StartsWith("Nothing started: the policy names develop as the base, not main", await Tool("agents_control", """{"action":"start","base":"main"}"""));
        File.Delete(local);
        Directory.CreateDirectory(local); // there, but it does not read: never taken for no policy
        Assert.StartsWith("Nothing started: could not read", await Tool("agents_control", """{"action":"start"}"""));
        Directory.Delete(local);
        Assert.False(await On(r => r?.Authorized ?? false));
        File.WriteAllText(local, $$"""{"version":1,"base":"main@{{Git(_repo, "rev-parse", "origin/main")[..10]}}"}""");
        Assert.StartsWith("Started.", await Tool("agents_control", """{"action":"start"}"""));
        _sup!.Dispose();
    }

    /// The owner's policy, outside the repository, pinning main where it is now.
    void PinLocally(string extra = "")
    {
        var local = Path.Combine(P("data"), "policies", "repo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, $$"""{"version":1,"base":"main@{{Git(_repo, "rev-parse", "origin/main")}}"{{extra}}}""");
    }

    /// Someone else's commit lands on main.
    void MoveMain()
    {
        File.AppendAllText(Path.Combine(_repo, "moved.txt"), "x\n");
        Git(_repo, "add", ".");
        Git(_repo, "commit", "--quiet", "-m", "moved");
        Git(_repo, "push", "--quiet", "origin", "main");
    }

    [Fact]
    public async Task APinThatMovesAfterTheStartStopsTheNextPushAndTheNextRun()
    {
        PinLocally(""","sensitive":["deploy/**"]""");
        Issues("""[{"number":54,"title":"Deploy note","body":"[fake:touch=deploy/run.txt] Add it.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        Assert.StartsWith("Started.", await Tool("agents_control", """{"action":"start"}"""));
        await Until(r => Item(r, 54) is { State: ItemState.NeedsOwner, HoldFor: "sensitive" }, "the change waits for the owner");

        // main moves past the pin while the push waits: the owner's yes does not push it.
        MoveMain();
        await Tool("agents_queue", """{"action":"answer","item":"#54"}""");
        await Until(r => r.Paused, "agents pause on the moved pin");
        Assert.Contains("not the pinned", await On(r => r.Note!));
        Assert.DoesNotContain("patch-goblins/54", Git(_repo, "ls-remote", "origin"));

        // Nor does a new run start: resumed without a new pin, agents pause again before any worker.
        var runs = Runs.Length;
        await Tool("agents_queue", """{"action":"add","title":"Greet","text":"Say hello."}""");
        await _loop.Run(() => { _sup!.Resume(); return 0; });
        await Until(r => r.Paused, "agents pause again");
        await Task.Delay(1500);
        Assert.Equal(runs, Runs.Length);
        Assert.NotEqual(ItemState.Working, await On(r => W(r, "w1").State));
        Assert.DoesNotContain("patch-goblins/54", Git(_repo, "ls-remote", "origin"));
        Assert.StartsWith("Nothing started: origin/main is at", await Tool("agents_control", """{"action":"start"}""")); // starting needs a new pin
        _sup!.Dispose();
    }

    [Fact]
    public async Task ChecksChangedOnTheBaseAreConfirmedAgainBeforeAnythingRunsUnderThem()
    {
        CommitPolicy("""{"version":1,"checks":[{"run":"git --version"}]}""");
        await StartAsync();
        string? shown = null;
        await _loop.Run(() => { _sup!.Authorize = (_, v, _) => { shown = v; return Task.FromResult(true); }; return 0; });
        Assert.StartsWith("Started.", await Tool("agents_control", """{"action":"start"}"""));
        Assert.Equal("`git --version`", shown);

        // Someone merges other checks: the owner never saw them.
        CommitPolicy("""{"version":1,"checks":[{"run":"git status --short"}]}""");
        await Tool("agents_queue", """{"action":"add","title":"Greet","text":"Say hello."}""");
        await Until(r => r.Paused, "agents pause on the changed checks");
        Assert.Contains("the policy's checks changed since you confirmed them", await On(r => r.Note!));
        Assert.Empty(Runs);
        Assert.Equal(ItemState.Queued, await On(r => W(r, "w1").State));

        // Starting again shows them; once confirmed, they are what verifies the work.
        Assert.StartsWith("Started.", await Tool("agents_control", """{"action":"start"}"""));
        Assert.Equal("`git status --short`", shown);
        await Until(r => W(r, "w1").State == ItemState.Open, "the work reaches a pull request");
        var body = Read(Path.Combine(_gh, "pr-body-7.md"));
        Assert.Contains("passed: `git status --short` at", body);
        Assert.DoesNotContain("git --version", body);
        _sup!.Dispose();
    }

    [Fact]
    public async Task AnApprovalIsForTheCommitTheOwnerSawAndARenameOrAQuotedNameStillCounts()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".github", "workflows"));
        File.WriteAllText(Path.Combine(_repo, ".github", "workflows", "ci.yml"), "on: pull_request\n");
        CommitPolicy("""{"version":1,"push":"ask"}""");
        Issues("""[{"number":52,"title":"Quiet fix","body":"Fix it.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 52) is { State: ItemState.NeedsOwner, HoldFor: "push" }, "the push waits for the owner");
        var wt = await On(r => Item(r, 52).Worktree!);
        var seen = Git(wt, "rev-parse", "HEAD");
        Assert.Single(Runs, r => r.Contains("NotebookEdit"));

        // Before the owner answers, the branch changes: the CI workflow moves away and another arrives under a name git quotes.
        Git(wt, "mv", ".github/workflows/ci.yml", "ci.yml.off");
        File.WriteAllText(Path.Combine(wt, ".github", "workflows", "déploy.yml"), "on: push\n");
        Git(wt, "add", ".");
        Git(wt, "commit", "--quiet", "-m", "more");
        await Tool("agents_queue", """{"action":"answer","item":"#52"}""");

        // The yes was for the commit the owner saw: the new one is reviewed again, adversarially, and waits again.
        await Until(r => Item(r, 52) is { State: ItemState.NeedsOwner, HoldFor: "sensitive" }, "the new commit waits for the owner");
        Assert.DoesNotContain("patch-goblins/52", Git(_repo, "ls-remote", "origin"));
        Assert.Equal(2, Runs.Count(r => r.Contains("NotebookEdit")));
        var note = await On(r => Item(r, 52).Note!);
        Assert.Contains(".github/workflows/ci.yml", note);
        Assert.Contains("déploy.yml", note);
        var head = Git(wt, "rev-parse", "HEAD");
        Assert.NotEqual(seen, head);

        // The owner's yes for this commit pushes exactly it.
        await Tool("agents_queue", """{"action":"answer","item":"#52"}""");
        await Until(r => Item(r, 52).State == ItemState.Open, "the approved commit is pushed");
        Assert.Contains(head + "\trefs/heads/patch-goblins/52", Git(_repo, "ls-remote", "origin"));
        Assert.Equal("origin/" + await On(r => Item(r, 52).Branch!), Git(wt, "rev-parse", "--abbrev-ref", "@{upstream}"));
        _sup!.Dispose();
    }

    [Fact]
    public async Task ASecretInAnEarlierCommitOrBehindAnAttributeIsStillSensitive()
    {
        CommitPolicy("""{"version":1,"push":"ask"}""");
        Issues("""[{"number":55,"title":"Tidy","body":"Tidy it.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start"}""");
        await Until(r => Item(r, 55) is { State: ItemState.NeedsOwner, HoldFor: "push" }, "the push waits for the owner");
        var wt = await On(r => Item(r, 55).Worktree!);

        // A key committed and deleted again is still in what a push uploads; the base's own change is not the branch's.
        File.WriteAllText(Path.Combine(wt, "id_ed25519"), "synthetic\n");
        Git(wt, "add", ".");
        Git(wt, "commit", "--quiet", "-m", "add");
        Git(wt, "rm", "--quiet", "id_ed25519");
        Git(wt, "commit", "--quiet", "-m", "remove");
        File.WriteAllText(Path.Combine(_repo, "base-only.pem"), "synthetic\n");
        Git(_repo, "add", ".");
        Git(_repo, "commit", "--quiet", "-m", "base");
        Git(_repo, "push", "--quiet", "origin", "main");
        await Tool("agents_queue", """{"action":"answer","item":"#55"}""");
        await Until(r => Item(r, 55) is { State: ItemState.NeedsOwner, HoldFor: "sensitive" }, "the secret in the history waits for the owner");
        var note = await On(r => Item(r, 55).Note!);
        Assert.Contains("id_ed25519", note);
        Assert.DoesNotContain("base-only.pem", note);

        // A -diff attribute makes git call a file binary: its token is still found.
        File.WriteAllText(Path.Combine(wt, ".gitattributes"), "notes.txt -diff\n");
        File.WriteAllText(Path.Combine(wt, "notes.txt"), "aws_key = AKIA" + "IOSFODNN7EXAMPLE\n");
        Git(wt, "add", ".");
        Git(wt, "commit", "--quiet", "-m", "notes");
        await Tool("agents_queue", """{"action":"answer","item":"#55"}""");
        await Until(r => Item(r, 55).Note?.Contains("notes.txt (a token- or key-shaped string)") == true, "the hidden token is found");
        Assert.Contains(".gitattributes", await On(r => Item(r, 55).Note!));
        Assert.DoesNotContain("patch-goblins/55", Git(_repo, "ls-remote", "origin"));
        _sup!.Dispose();
    }

    [Fact]
    public async Task ATokenShapedStringMakesAChangeSensitive()
    {
        Issues("""[{"number":53,"title":"Configure","body":"[fake:token=settings.txt] Configure it.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync();
        await Tool("agents_control", """{"action":"start","verify":"git --version"}""");
        await Until(r => Item(r, 53) is { State: ItemState.NeedsOwner, HoldFor: "sensitive" }, "the change waits for the owner");
        Assert.Contains("settings.txt (a token- or key-shaped string)", await On(r => Item(r, 53).Note!));
        Assert.DoesNotContain("patch-goblins/53", Git(_repo, "ls-remote", "origin"));
        Assert.DoesNotContain("IOSFODNN7EXAMPLE", await Tool("agents_details", """{"item":"#53"}""")); // named by its place, never quoted
        _sup!.Dispose();
    }

    /// A dry run on a copy of a real repository (PATCH_GOBLINS_DRYRUN_REPO, e.g. an Easy Tile checkout): the doctor's
    /// bootstrap turns its pull-request CI into the policy's checks, and an issue goes through work, those real checks,
    /// review and a pull request. Its origin is a local bare copy and GitHub is the fake, so nothing reaches the real
    /// repository; the checkout itself is only cloned. Skipped (passes) without the variable.
    [Fact]
    public async Task ADryRunOnACopyOfARealRepositoryVerifiesWithItsOwnCi()
    {
        if (Environment.GetEnvironmentVariable("PATCH_GOBLINS_DRYRUN_REPO") is not { Length: > 0 } source) return;
        var head = Git(source, "rev-parse", "HEAD");
        var status = Git(source, "status", "--porcelain");
        var dry = P("dry");
        Git(_tmp.Path, "clone", "--quiet", "--bare", "--no-local", source, dry + ".git");
        Git(_tmp.Path, "clone", "--quiet", dry + ".git", dry);
        Git(dry, "config", "user.email", "agents@example.com");
        Git(dry, "config", "user.name", "Patch Goblins test");

        var report = await Doctor.AssessAsync(_cli, dry, null, "owner");
        Assert.NotEmpty(report.CiCommands);
        var policy = Path.Combine(P("data"), "policies", "dry.json");
        Directory.CreateDirectory(Path.GetDirectoryName(policy)!);
        File.WriteAllText(policy, JsonSerializer.Serialize(new RepoPolicy { Checks = report.CiCommands }, Json.Options));

        Issues("""[{"number":1,"title":"Note the dry run","body":"Add a line about it.","labels":[],"assignees":[],"author":{"login":"owner"}}]""");
        await StartAsync(terminal: false);
        await Terminal(true, dry);
        Assert.StartsWith("Started.", await Tool("agents_control", """{"action":"start"}""", dry));
        await Until(r => r.Items.Any(i => i.Issue == 1 && i.State == ItemState.Open), "the issue reaches a pull request", 300, dry);
        var body = Read(Path.Combine(_gh, "pr-body-7.md"));
        foreach (var c in report.CiCommands) Assert.Contains($"passed: `{c.Run}` at", body);
        Assert.Contains("patch-goblins/1-note-the-dry-run", Git(dry, "ls-remote", "origin")); // the local bare copy
        Assert.Equal((head, status), (Git(source, "rev-parse", "HEAD"), Git(source, "status", "--porcelain")));
        if (Environment.GetEnvironmentVariable("PATCH_GOBLINS_DRYRUN_OUT") is { Length: > 0 } output)
            File.WriteAllText(output, report.Text() + "\n\n--- pull request body\n" + body + "\n--- calls to the fake GitHub\n" + Read(Path.Combine(_gh, "gh.log")));
        _sup!.Dispose();
    }

    public void Dispose()
    {
        if (_sup != null) _loop.Run(() => { _sup.Dispose(); return 0; }).Wait(TimeSpan.FromSeconds(5));
        _loop.Dispose();
        // Runs outlive the supervisor by design; a test's must not outlive the test.
        foreach (var run in _store.Load().Repos.SelectMany(r => r.Items.Select(i => i.Run).Concat(r.Goals.Select(g => g.Run))).OfType<Run>().Where(r => r.Live))
            using (var p = AgentProcess.Attach(run.Job, run.Pid, run.Created))
                p?.StopAsync(TimeSpan.Zero).Wait(TimeSpan.FromSeconds(10));
        Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", _dataWas);
        AgentProcess.BelowNormal = true;
        for (var i = 0; i < 5; i++)
        {
            try { Directory.Delete(_tmp.Path, recursive: true); break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                foreach (var f in Directory.EnumerateFiles(_tmp.Path, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Thread.Sleep(300);
            }
        }
    }
}

/// A single-threaded SynchronizationContext: what the tray's UI thread is to the supervisor.
sealed class Loop : SynchronizationContext, IDisposable
{
    readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = [];

    public Loop()
    {
        new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }) { IsBackground = true, Name = "agents test loop" }.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { _queue.Add((d, state)); }
        catch (InvalidOperationException) { } // disposed
    }

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public Task<T> Run<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try { tcs.SetResult(await work()); }
            catch (Exception e) { tcs.SetException(e); }
        }, null);
        return tcs.Task;
    }

    public Task<T> Run<T>(Func<T> work) => Run(() => Task.FromResult(work()));

    public void Dispose() => _queue.CompleteAdding();
}
