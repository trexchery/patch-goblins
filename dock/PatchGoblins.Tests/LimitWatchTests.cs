using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

/// A usage-limit error reaching the switch: seen in a file, deduped, charged to an account, the default login moved.
/// Synthetic accounts, files and engine calls only: nothing here touches a real login or process.
[Collection("own process")] // one test makes this process a bridge host, which hides it from ResumeStoppedTests' scans
public class LimitWatchTests
{
    readonly DateTimeOffset Now = DateTimeOffset.UtcNow; // per test (xunit builds one instance each), so a slow run never ages a hit past the watcher's freshness window

    static string LimitLine(DateTimeOffset at, DateTimeOffset resets, string window = "five_hour", string error = "usage_limit_reached") =>
        $$$"""{"type":"assistant","timestamp":"{{{at:O}}}","apiError":"{{{error}}}","isApiErrorMessage":true,"apiErrorParams":{"rate_limit_info":{"status":"rejected","resetsAt":{{{resets.ToUnixTimeSeconds()}}},"rateLimitType":"{{{window}}}"}},"message":{"role":"assistant","content":[{"type":"text","text":"You've hit your limit"}]}}""";

    static Account A(int n, double five, bool active = false, DateTimeOffset? resets = null, bool disabled = false) =>
        new(n, $"{n}@example.com", active, "ok", new Usage(new Window(five, resets), new Window(10, null), null, null), Disabled: disabled);

    static string TempRoot() => Directory.CreateTempSubdirectory().FullName;

    static string Write(string root, string rel, params string[] lines)
    {
        var path = Path.Combine(root, "projects", "C--w", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllLines(path, lines);
        return path;
    }

    // ---- LimitWatch: reading the files --------------------------------------------------------------------------

    [Fact]
    public void PollReadsOnlyWhatWasAddedAndOnlyRealFreshLimits()
    {
        var root = TempRoot();
        try
        {
            var hits = new List<LimitHit>();
            using var w = new LimitWatch(hits.Add, _ => { });
            var f = Write(root, "sid.jsonl", """{"type":"user","message":{"role":"user","content":"hi"}}""");
            w.Poll([(f, root)]);
            Assert.Empty(hits);
            File.AppendAllLines(f, [LimitLine(Now, Now.AddHours(1))]);
            w.Poll([(f, root)]);
            var hit = Assert.Single(hits);
            Assert.Equal("five_hour", hit.Limit.Window);
            Assert.Equal(root, hit.ConfigDir);
            w.Poll([(f, root)]);
            Assert.Single(hits); // read once
            File.AppendAllLines(f, [LimitLine(Now.AddMinutes(-10), Now.AddHours(1)), LimitLine(Now, Now.AddMinutes(-1)),
                LimitLine(Now, Now.AddHours(1), error: "rate_limit")]);
            w.Poll([(f, root)]);
            Assert.Single(hits); // history, a reset already past, and a server throttle are not limits
            File.AppendAllLines(f, [LimitLine(Now.AddSeconds(1), Now.AddHours(2), "seven_day")]);
            w.Poll([(f, root)]);
            Assert.Equal(2, hits.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ACopiedTranscriptDoesNotCountTwice()
    {
        var root = TempRoot();
        try
        {
            var hits = new List<LimitHit>();
            using var w = new LimitWatch(hits.Add, _ => { });
            var f = Write(root, "sid.jsonl", LimitLine(Now, Now.AddHours(1)));
            w.Poll([(f, root)]);
            var copy = Path.Combine(root, "other", "sid.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(f, copy);
            w.Poll([(copy, root)]); // what the tray makes when it moves a window: the same error, in another profile
            Assert.Single(hits);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AFileWatcherSeesASubagentFileAndASecondErrorInIt()
    {
        var root = TempRoot();
        try
        {
            var hits = new System.Collections.Concurrent.BlockingCollection<LimitHit>();
            using var w = new LimitWatch(hits.Add, _ => { });
            Directory.CreateDirectory(Path.Combine(root, "projects", "C--w"));
            w.Sync([root]);
            Assert.True(w.Watching(root));
            var f = Write(root, Path.Combine("sid", "subagents", "workflows", "wf_1", "agent-a.jsonl"), LimitLine(Now, Now.AddHours(1)));
            Assert.True(hits.TryTake(out var hit, 10_000), "no hit from the watcher");
            Assert.Equal(f, hit.File);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WatcherOverflowFallsBackToThePollAndIsRestarted()
    {
        var root = TempRoot();
        try
        {
            var hits = new List<LimitHit>();
            var log = new List<string>();
            using var w = new LimitWatch(hits.Add, log.Add);
            Directory.CreateDirectory(Path.Combine(root, "projects", "C--w"));
            w.Sync([root]);
            w.Fail(root, new InternalBufferOverflowException("too many changes"));
            Assert.False(w.Watching(root));
            Assert.Contains(log, l => l.Contains("poll"));
            var f = Write(root, Path.Combine("sid", "subagents", "agent-a.jsonl"), LimitLine(Now, Now.AddHours(1)));
            w.Poll([(f, root)]);
            Assert.Single(hits); // nothing was lost
            w.Sync([root]);
            Assert.True(w.Watching(root)); // and the watcher is back
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- LimitSwitch: what an error does ------------------------------------------------------------------------

    /// A switch with fake engine calls: the snapshot, the default login and the live profiles are variables the test moves.
    sealed class Rig
    {
        public Snapshot Snap;
        public int Default = 1;
        public DateTime Changed = DateTime.UtcNow.AddHours(-1); // when the default login last changed
        public HashSet<int> Live = [];
        public List<int> Switched = [];
        public List<string> Reports = [];
        public List<string> Log = [];
        public HashSet<int> Failing = [];
        public LimitSwitch Switch;
        public double Line = ResumeStopped.Full, Hysteresis;
        public Func<string, Task> Report = _ => Task.CompletedTask;
        public Task SwitchGate = Task.CompletedTask;

        public Rig(params Account[] accounts)
        {
            Snap = new Snapshot(1, 1, [.. accounts]);
            Switch = new LimitSwitch(() => Snap, () => (Default, Changed), () => Live, d => { Reports.Add(d); return Report(d); },
                async m =>
                {
                    await SwitchGate;
                    if (Failing.Contains(m)) throw new EngineException("lock timeout");
                    Switched.Add(m);
                    var from = Default;
                    (Default, Changed) = (m, DateTime.UtcNow);
                    return new SwitchResult(true, new AccountRef(from, $"{from}@example.com"), new AccountRef(m, $"{m}@example.com"), null);
                }, new HashSet<string>(), Log.Add, threshold: () => Line, hysteresis: () => Hysteresis);
        }

        public Task<LimitOutcome?> Hit(DateTimeOffset at, DateTimeOffset resets, string? dir = null, string file = @"C:\x\sid.jsonl") =>
            Switch.OnLimitAsync(new LimitHit(file, dir ?? ResumeStopped.DefaultDir, new Limit("five_hour", resets, at)));
    }

    [Fact]
    public async Task OnLimitDedupesOnAccountAndReset()
    {
        var resets = Now.AddHours(1);
        var rig = new Rig(A(1, 50, active: true, resets: resets), A(2, 20), A(3, 60));
        var first = await rig.Hit(Now, resets);
        Assert.Equal(2, first!.Switched!.To!.Number);
        Assert.Null(await rig.Hit(Now.AddSeconds(1), resets, file: @"C:\x\sid\subagents\a.jsonl")); // another window, same refusal
        Assert.Equal([2], rig.Switched);
        var doc = Assert.Single(rig.Reports); // the engine was told once, the refused account at 100%
        Assert.Contains("1@example.com", doc);
        Assert.Contains(rig.Log, l => l.StartsWith("limit seen") && l.Contains("acct 1"));
        Assert.Contains(rig.Log, l => l.StartsWith("switch 1->2 ") && l.EndsWith(" ms"));
    }

    [Fact]
    public async Task AccountWithALiveProfileWindowIsNeverTheTarget()
    {
        var rig = new Rig(A(1, 50, active: true), A(2, 5), A(3, 60)) { Live = [2] };
        var o = await rig.Hit(Now, Now.AddHours(1));
        Assert.Equal([3], rig.Switched); // 2 has the most room, but a window of `cswap run 2` holds its login
        Assert.Equal(3, o!.Switched!.To!.Number);
        Assert.Equal([3], ResumeStopped.SwitchTargets(rig.Snap.Accounts, 1, new HashSet<int> { 2 }, new Dictionary<int, DateTimeOffset>(), Now));
    }

    [Fact]
    public async Task RoomOnlyOnAHeldAccountIsHeldNotNoRoom()
    {
        var rig = new Rig(A(1, 50, active: true), A(2, 5)) { Live = [2] };
        var o = await rig.Hit(Now, Now.AddHours(1));
        Assert.True(o is { Held: true, NoRoom: false }); // the move pass frees #2 or says what holds it; not "all accounts near their limits"
        Assert.Empty(rig.Switched);
    }

    [Fact]
    public async Task RoomOnlyOnAHeldAccountTooCloseToFreeIsNoRoom()
    {
        // #2 has room but a `cswap run 2` window holds it, and it is within the hysteresis: the move pass would never free it.
        var rig = new Rig(A(1, 50, active: true), A(2, 92), A(3, 99)) { Live = [2], Line = 97, Hysteresis = 10 };
        var o = await rig.Hit(Now, Now.AddHours(1));
        Assert.True(o is { Held: false, NoRoom: true }); // told, not a silent stall
        Assert.Empty(rig.Switched);
    }

    [Fact]
    public void EveryFreeablePinnedAccountIsACandidate()
    {
        // #2 has the most room but a busy pinned window: #4, held by idle windows or agents, must still be tried.
        Assert.Equal([2, 4], ResumeStopped.FreeTargets([A(1, 98, active: true), A(2, 20), A(4, 40)], new HashSet<int> { 2, 4 },
            new Dictionary<int, DateTimeOffset>(), Now, 97, 10));
    }

    [Fact]
    public async Task SecondRefusalHopsOnAndNeverGoesBackToTheFirstRefuser()
    {
        var rig = new Rig(A(1, 10, active: true), A(2, 20), A(3, 60)); // 1's reading is low and stale: it just said no
        Assert.Equal(2, (await rig.Hit(Now, Now.AddHours(1)))!.Switched!.To!.Number);
        rig.Snap = rig.Snap with { ActiveAccountNumber = 2, Accounts = [.. rig.Snap.Accounts.Select(a => a with { Active = a.Number == 2 })] };
        var o = await rig.Hit(Now.AddMinutes(1), Now.AddHours(2));
        Assert.Equal(3, o!.Switched!.To!.Number); // not 1, whatever its reading says, until it resets
        Assert.Equal([2, 3], rig.Switched);
        Assert.Equal(Now.AddHours(1).ToUnixTimeSeconds(), rig.Switch.RefusedUntil[1].ToUnixTimeSeconds());
    }

    [Fact]
    public async Task NoAccountWithRoomMeansOneNoticeWithTheEarliestResetAndNoSwitch()
    {
        var soon = Now.AddMinutes(40);
        var rig = new Rig(A(1, 100, active: true), A(2, 100, resets: soon), A(3, 40, disabled: true));
        var o = await rig.Hit(Now, Now.AddHours(1));
        Assert.True(o!.NoRoom);
        Assert.Equal(soon.ToUnixTimeSeconds(), o.Earliest!.Value.ToUnixTimeSeconds());
        Assert.Empty(rig.Switched);
    }

    [Fact]
    public async Task FailedSwitchTriesTheNextAccountThenGivesUp()
    {
        var rig = new Rig(A(1, 50, active: true), A(2, 20), A(3, 30)) { Failing = [2] };
        Assert.Equal(3, (await rig.Hit(Now, Now.AddHours(1)))!.Switched!.To!.Number);
        var all = new Rig(A(1, 50, active: true), A(2, 20)) { Failing = [2] };
        var o = await all.Hit(Now, Now.AddHours(1));
        Assert.NotNull(o!.Error); // the tray then falls back to the engine's own check
        Assert.Contains(all.Log, l => l.Contains("switch 1->2 failed"));
    }

    [Fact]
    public async Task ARefusalOnAnotherLoginOrProfileDoesNotMoveTheDefaultLogin()
    {
        var resets = Now.AddHours(3);
        var rig = new Rig(A(1, 50, active: true, resets: resets), A(2, 20), A(3, 60));
        var o = await rig.Hit(Now, resets, dir: Path.Combine(Path.GetTempPath(), "3-c_example.com")); // a `cswap run 3` window
        Assert.Null(o!.Switched);
        Assert.Empty(rig.Switched);
        Assert.Contains(3, rig.Switch.RefusedUntil.Keys); // but 3 is out until its reset, for every window
        rig.Default = 2; // the engine's own check already moved the login on: nothing more to do for a refusal of 1
        var late = await rig.Hit(Now.AddMinutes(5), resets); // 1's window, as the reset time says
        Assert.Null(late!.Switched);
        Assert.Empty(rig.Switched);
    }

    [Fact]
    public async Task AFableRefusalOnTheDefaultLoginMovesNothingButTheFableWindows()
    {
        // Claude Code labels Fable's weekly refusal `seven_day`; it resets with #1's Fable window, not its general one.
        var fableReset = Now.AddDays(4);
        Account F(int n, double fable, bool active = false) => new(n, $"{n}@example.com", active, "ok",
            new Usage(new Window(30, Now.AddHours(2)), new Window(40, Now.AddDays(2)), [new Window(fable, fableReset, "Fable")], null));
        var rig = new Rig(F(1, 100, active: true), F(2, 10), F(3, 20));
        var root = TempRoot();
        try
        {
            var file = Write(root, "sid.jsonl", """{"type":"assistant","message":{"model":"claude-fable-5-1","content":[]}}""");
            var o = await rig.Switch.OnLimitAsync(new LimitHit(file, ResumeStopped.DefaultDir, new Limit("seven_day", fableReset, Now)));
            Assert.Null(o!.Switched);
            Assert.Empty(rig.Switched); // the default login stays: every window not on Fable still has #1's general room
            Assert.DoesNotContain(1, rig.Switch.RefusedUntil.Keys);
            Assert.Equal(fableReset, rig.Switch.ScopedRefusedUntil[(1, "Fable")]);
            var doc = Assert.Single(rig.Reports);
            Assert.Contains("\"sevenDay\":{\"pct\":40", doc); // the general weekly window is not marked full
            Assert.Contains(rig.Log, l => l.Contains("only windows on Fable move"));

            // The same refusal from a window on Sonnet is the account's own weekly limit: as before, the login switches.
            var sonnet = Write(root, "other.jsonl", """{"type":"assistant","message":{"model":"claude-sonnet-5-5","content":[]}}""");
            var general = await rig.Switch.OnLimitAsync(new LimitHit(sonnet, ResumeStopped.DefaultDir, new Limit("seven_day", Now.AddDays(2), Now.AddSeconds(1))));
            Assert.Equal(2, general!.Switched!.To!.Number);
            Assert.Contains(1, rig.Switch.RefusedUntil.Keys);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RefusalIsChargedToTheAccountThatSaidNo()
    {
        var resets = Now.AddHours(2);
        var l = new Limit("five_hour", resets, Now);
        Account[] accounts = [A(1, 30, active: true), A(2, 90, resets: resets.AddSeconds(30)), A(3, 20)];
        Assert.Equal(3, ResumeStopped.Refuser(l, 3, accounts, (1, DateTime.UtcNow.AddHours(-1)))); // a profile's own
        Assert.Equal(2, ResumeStopped.Refuser(l, null, accounts, (1, DateTime.UtcNow))); // the window that resets then, though the login just changed
        Assert.Equal(1, ResumeStopped.Refuser(l, null, [A(1, 30, active: true), A(3, 20)], (1, DateTime.UtcNow.AddHours(-1))));
        Assert.Null(ResumeStopped.Refuser(l, null, [A(1, 30, active: true), A(3, 20)], (1, DateTime.UtcNow))); // too soon after a switch: unknown
        Assert.Equal(3, ResumeStopped.ProfileAccount(@"C:\b\sessions\3-nick_example.com"));
        Assert.Null(ResumeStopped.ProfileAccount(ResumeStopped.DefaultDir));
    }

    // ---- the windows themselves ---------------------------------------------------------------------------------

    [Fact]
    public void MoveCapCountsASessionsMovesInTheLastHour()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        // One stamp per move, so the same session bouncing between two accounts still adds up (an account key is overwritten).
        var moves = new Dictionary<string, DateTime> { ["s1#2"] = now.AddMinutes(-5), ["s1#3"] = now.AddMinutes(-30),
            [ResumeStopped.MoveStamp("s1", now.AddMinutes(-5))] = now.AddMinutes(-5), [ResumeStopped.MoveStamp("s1", now.AddMinutes(-35))] = now.AddMinutes(-35),
            [ResumeStopped.MoveStamp("s1", now.AddMinutes(-59))] = now.AddMinutes(-59), [ResumeStopped.MoveStamp("s1", now.AddMinutes(-61))] = now.AddMinutes(-61),
            [ResumeStopped.MoveStamp("s2", now.AddMinutes(-1))] = now.AddMinutes(-1), [ResumeStopped.MoveStamp("s10", now.AddMinutes(-1))] = now.AddMinutes(-1) };
        Assert.Equal(ResumeStopped.MovesPerHour, ResumeStopped.MovesInLastHour(moves, "s1", now));
        Assert.Equal(1, ResumeStopped.MovesInLastHour(moves, "s2", now));
        Assert.Equal(0, ResumeStopped.MovesInLastHour(moves, "s9", now));
    }

    [Fact]
    public void ProfileWithARunMarkerOrALiveClaudeIsInUse()
    {
        var root = TempRoot();
        try
        {
            string Profile(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            var marked = Profile("3-c_example.com");
            Directory.CreateDirectory(Path.Combine(marked, ".cswap-runs"));
            File.WriteAllText(Path.Combine(marked, ".cswap-runs", Environment.ProcessId.ToString()), "");
            var stale = Profile("4-d_example.com");
            Directory.CreateDirectory(Path.Combine(stale, ".cswap-runs"));
            File.WriteAllText(Path.Combine(stale, ".cswap-runs", "2147483000"), ""); // no such process
            var quiet = Profile("5-e_example.com");
            var self = System.Diagnostics.Process.GetCurrentProcess();
            var windowed = Profile("6-f_example.com");
            Directory.CreateDirectory(Path.Combine(windowed, "sessions"));
            File.WriteAllText(Path.Combine(windowed, "sessions", "1.json"),
                $$"""{"pid":{{self.Id}},"sessionId":"sid","procStart":"{{self.StartTime.ToUniversalTime().ToFileTimeUtc()}}"}""");
            var live = ResumeStopped.LiveProfiles([(marked, 3), (stale, 4), (quiet, 5), (windowed, 6), (ResumeStopped.DefaultDir, 1)]);
            Assert.Equal([3, 6], live.Order().ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    static void Stage(string root, string status, params string[] lines)
    {
        var self = System.Diagnostics.Process.GetCurrentProcess();
        var project = Path.Combine(root, "projects", "C--w");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        File.WriteAllLines(Path.Combine(project, "sid.jsonl"), lines);
        File.WriteAllText(Path.Combine(root, "sessions", "1.json"),
            $$"""{"pid":{{self.Id}},"sessionId":"sid","cwd":"C:\\w","status":"{{status}}","kind":"interactive","procStart":"{{self.StartTime.ToUniversalTime().ToFileTimeUtc()}}"}""");
    }

    [Fact]
    public void BusyOrWaitingWindowAtALimitIsSeenButNeverMovable()
    {
        var root = TempRoot();
        try
        {
            var later = DateTime.UtcNow.AddSeconds(10);
            foreach (var status in new[] { "busy", "shell", "waiting" })
            {
                Stage(root, status, LimitLine(Now, Now.AddHours(1)));
                Assert.Empty(ResumeStopped.Scan([(root, 3)], later, DateTime.MinValue)); // the default scan never finds it
                var s = Assert.Single(ResumeStopped.Scan([(root, 3)], later, DateTime.MinValue, includeBusy: true));
                Assert.False(s.Idle);
                Assert.True(s.AtLimit);
                // A shell at its limit has finished its turn: movable, its background command outlives the close. The others: the last check refuses them too.
                Assert.Equal(status == "shell", s.Movable);
                Assert.Equal(status == "shell", ResumeStopped.Unchanged(s));
            }
            Stage(root, "idle", LimitLine(Now, Now.AddHours(1)));
            Assert.True(Assert.Single(ResumeStopped.Scan([(root, 3)], later, DateTime.MinValue, includeBusy: true)).Idle);
            // the poll reads a live window's subagent files too
            Write(root, Path.Combine("sid", "subagents", "agent-a.jsonl"), LimitLine(Now, Now.AddHours(1)));
            var files = ResumeStopped.LiveTranscripts([(root, 3)], DateTime.UtcNow).Select(f => Path.GetFileName(f.File)).Order().ToArray();
            Assert.Equal(["agent-a.jsonl", "sid.jsonl"], files);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AnAccountRefusedUntilItsResetIsNotATargetForAnyWindow()
    {
        var s = new StoppedSession(1, "id", @"C:\w", @"C:\cfg", 3, @"C:\cfg\projects\w\id.jsonl");
        Account[] accounts = [A(1, 10), A(2, 30), A(3, 100)];
        var until = new Dictionary<int, DateTimeOffset> { [1] = Now.AddHours(1) };
        Assert.Equal(1, ResumeStopped.Target(s, accounts, _ => true, now: Now));
        Assert.Equal(2, ResumeStopped.Target(s, accounts, _ => true, now: Now, refusedUntil: until)); // 1 said no an hour ago, its reading is just old
        Assert.Equal(1, ResumeStopped.Target(s, accounts, _ => true, now: Now.AddHours(2), refusedUntil: until));
    }

    // ---- review fixes --------------------------------------------------------------------------------------------

    [Fact]
    public void ABackgroundNoticeAfterTheErrorDoesNotHideTheStop()
    {
        var notice = """{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>done</task-notification>"}}""";
        var typed = """{"type":"user","origin":{"kind":"human"},"message":{"role":"user","content":"carry on"}}""";
        var limit = LimitLine(Now, Now.AddHours(1));
        Assert.NotNull(ResumeStopped.LimitOf([limit, notice]));
        Assert.Null(ResumeStopped.LimitOf([limit, typed])); // someone typing does
        Assert.Null(ResumeStopped.LimitOf([limit, notice, typed]));
    }

    [Fact]
    public async Task ARefusalSeenAgainStillSwitchesWhileTheDefaultLoginIsBackOnTheRefuser()
    {
        var resets = Now.AddHours(1);
        var rig = new Rig(A(1, 50, active: true, resets: resets), A(2, 20), A(3, 60));
        Assert.Equal(2, (await rig.Hit(Now, resets))!.Switched!.To!.Number);
        rig.Default = 1; // the user (or the engine) moved the login back onto the account that said no
        var again = await rig.Hit(Now.AddSeconds(5), resets);
        Assert.Equal(2, again!.Switched!.To!.Number);
        Assert.Single(rig.Reports); // but it is reported once
        Assert.Single(rig.Log, l => l.StartsWith("limit seen"));
    }

    [Fact]
    public async Task AHitBeforeTheFirstListingIsNotConsumed()
    {
        var resets = Now.AddHours(1);
        var rig = new Rig(A(1, 50, active: true, resets: resets), A(2, 20));
        var snap = rig.Snap;
        rig.Snap = null!;
        Assert.Null(await rig.Hit(Now, resets));
        rig.Snap = snap;
        Assert.Equal(2, (await rig.Hit(Now, resets))!.Switched!.To!.Number); // the same refusal still counts once there is a listing
    }

    [Fact]
    public void OnlyTheWindowsTheEngineDecidesOnCountForAFree()
    {
        // The default login's Opus-only week is full, its 5-hour and weekly windows are not: the engine stays, so nothing is freed.
        var login = new Account(1, "1@example.com", true, "ok", new Usage(new Window(30, null), new Window(30, null), [new Window(98, null, "Opus")], null));
        Account[] accounts = [login, A(2, 20)];
        var none = new Dictionary<int, DateTimeOffset>();
        Assert.Equal(98, Format.Peak(login)); // shown
        Assert.Empty(ResumeStopped.FreeTargets(accounts, new HashSet<int> { 2 }, none, Now, 97, 10));
        try
        {
            ResumeStopped.Models = new HashSet<string>(["opus"], StringComparer.OrdinalIgnoreCase); // autoswitch.model names it
            Assert.Equal([2], ResumeStopped.FreeTargets(accounts, new HashSet<int> { 2 }, none, Now, 97, 10));
        }
        finally { ResumeStopped.Models = new HashSet<string>(); }
    }

    [Fact]
    public void SwitchTargetsStayBelowTheEnginesThresholdWhileAnyAre()
    {
        Account[] accounts = [A(1, 100, active: true), A(2, 98), A(3, 60)];
        var none = new Dictionary<int, DateTimeOffset>();
        Assert.Equal([3], ResumeStopped.SwitchTargets(accounts, 1, new HashSet<int>(), none, Now, threshold: 97));
        Assert.Equal([2], ResumeStopped.SwitchTargets([accounts[0], accounts[1]], 1, new HashSet<int>(), none, Now, threshold: 97)); // only above it: better than nothing
    }

    [Fact]
    public void APinnedAccountIsFreedOnlyWhenTheDefaultLoginHasNowhereElse()
    {
        var none = new Dictionary<int, DateTimeOffset>();
        var pinned1 = new HashSet<int> { 1 };
        // Oct 20: the default login on #3 at 100%, #2 full, #1 with room but held by a `cswap run 1` window.
        Assert.Equal(1, ResumeStopped.FreeTarget([A(1, 40), A(2, 100), A(3, 100, active: true)], pinned1, none, Now, 97, 10));
        Assert.Equal(1, ResumeStopped.FreeTarget([A(1, 40), A(2, 100), A(3, 97, active: true)], pinned1, none, Now, 97, 10)); // at the line, before the limit
        Assert.Null(ResumeStopped.FreeTarget([A(1, 40), A(2, 100), A(3, 90, active: true)], pinned1, none, Now, 97, 10)); // below the line: nothing due
        Assert.Null(ResumeStopped.FreeTarget([A(1, 40), A(2, 50), A(3, 100, active: true)], pinned1, none, Now, 97, 10)); // #2 is free: the engine's own switch
        Assert.Null(ResumeStopped.FreeTarget([A(1, 90), A(2, 100), A(3, 100, active: true)], pinned1, none, Now, 97, 10)); // within the hysteresis: it would trade back
        Assert.Null(ResumeStopped.FreeTarget([A(1, 40), A(2, 100), A(3, 100, active: true)], new HashSet<int>(), none, Now, 97, 10)); // nothing pinned: the engine's
        Assert.Null(ResumeStopped.FreeTarget([A(1, 40, disabled: true), A(2, 100), A(3, 100, active: true)], pinned1, none, Now, 97, 10));
        Assert.Null(ResumeStopped.FreeTarget([A(1, 40), A(3, 100, active: true)], pinned1, new Dictionary<int, DateTimeOffset> { [1] = Now.AddHours(1) }, Now, 97, 10)); // #1 refused
        // A refusal of the default login counts, whatever a stale reading says; the most room wins among pinned ones.
        Assert.Equal(4, ResumeStopped.FreeTarget([A(1, 40), A(3, 20, active: true), A(4, 5)], new HashSet<int> { 1, 4 },
            new Dictionary<int, DateTimeOffset> { [3] = Now.AddHours(1) }, Now, 97, 10));
    }

    static StoppedSession Window(string status, string id = "s") => new(1, id, @"C:\w", @"C:\p", 1, @"C:\p\t.jsonl", AtLimit: false, Status: status);

    [Fact]
    public void AProfileIsReleasedOnlyWhenEveryWindowInItIsIdle()
    {
        Assert.True(ResumeStopped.Releasable([Window("idle"), Window("idle", "t")], records: 2, runs: 2));
        Assert.True(ResumeStopped.Releasable([Window("idle")], records: 1, runs: 0)); // claude started without the wrapper
        Assert.False(ResumeStopped.Releasable([Window("idle"), Window("busy", "t")], records: 2, runs: 2));
        Assert.False(ResumeStopped.Releasable([Window("idle"), Window("shell", "t")], records: 2, runs: 2)); // background work would die
        Assert.False(ResumeStopped.Releasable([Window("idle")], records: 2, runs: 2)); // one window the scan did not see as done: working, or mid-prompt
        Assert.False(ResumeStopped.Releasable([Window("idle")], records: 1, runs: 2)); // a headless run (an agent) holds it too
        Assert.False(ResumeStopped.Releasable([], records: 0, runs: 1));
        Assert.False(ResumeStopped.Releasable([], records: 0, runs: 0)); // nothing to free
    }

    [Fact]
    public void OnlyAgentRunsBesideIdleWindowsAreAskedToYieldTheProfile()
    {
        Assert.True(ResumeStopped.AgentsOnly([], records: 0, runs: 2, agentRuns: 2));
        Assert.True(ResumeStopped.AgentsOnly([Window("idle")], records: 1, runs: 2, agentRuns: 1));
        Assert.False(ResumeStopped.AgentsOnly([], records: 0, runs: 2, agentRuns: 1)); // a headless run the owner started: never stopped
        Assert.False(ResumeStopped.AgentsOnly([Window("busy")], records: 1, runs: 2, agentRuns: 1)); // a working window holds it anyway
        Assert.False(ResumeStopped.AgentsOnly([], records: 1, runs: 2, agentRuns: 1)); // a window the scan did not see as done
        Assert.False(ResumeStopped.AgentsOnly([Window("idle")], records: 1, runs: 1, agentRuns: 0)); // no run holds it: Releasable's
    }

    [Fact]
    public void WhatHoldsAProfileIsNamedForTheUser()
    {
        Assert.Empty(ResumeStopped.Holders([Window("idle")], records: 1, runs: 1));
        Assert.Equal(["w (busy)", "w (shell)"], ResumeStopped.Holders([Window("idle"), Window("busy", "t"), Window("shell", "u")], records: 3, runs: 3));
        Assert.Equal(["a window still working"], ResumeStopped.Holders([Window("idle")], records: 2, runs: 2));
        Assert.Equal(["2 agent or headless runs"], ResumeStopped.Holders([Window("idle")], records: 1, runs: 3));
        Assert.Equal(["the agents terminal in w"], ResumeStopped.Holders([Window("agents")], records: 1, runs: 1));
    }

    [Fact]
    public void OnlyTheConsoleTheTrayOpenedIsEnded()
    {
        Assert.True(ResumeStopped.TrayConsole(@"C:\WINDOWS\system32\cmd.exe /k C:\Apps\patch-goblins\engine\cswap.exe run 3 --restore-rc -- --resume sid"));
        Assert.True(ResumeStopped.TrayConsole(@"""C:\WINDOWS\system32\cmd.exe"" /k ""title Claude · a · moved && ""C:\Program Files\patch-goblins\engine\cswap.exe"" run 3"""));
        Assert.True(ResumeStopped.TrayConsole(@"""C:\Program Files\PowerShell\7\pwsh.exe"" -NoLogo -NoExit -Command ""& 'C:\Apps\patch-goblins\engine\cswap.exe' 'run' '3' '--' '--resume' 'sid'"""));
        Assert.True(ResumeStopped.TrayConsole(@"C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoExit -Command ""$Host.UI.RawUI.WindowTitle = 'Claude · a'; & 'C:\Program Files\patch-goblins\engine\cswap.exe' 'run' '3'"""));
        Assert.False(ResumeStopped.TrayConsole(@"""C:\Program Files\PowerShell\7\pwsh.exe"" -NoLogo")); // the user's own shell
        Assert.False(ResumeStopped.TrayConsole(@"pwsh.exe -NoExit -Command ""& 'cswap.exe' 'list'""")); // not a window running Claude
        Assert.False(ResumeStopped.TrayConsole(@"C:\WINDOWS\system32\cmd.exe")); // an interactive cmd tab
        Assert.False(ResumeStopped.TrayConsole(@"cmd.exe /c claude & npm test")); // a script running claude
        Assert.False(ResumeStopped.TrayConsole(null));
    }

    [Fact]
    public void AClosedShellExitsCleanlySoTheTerminalTabCloses()
    {
        using var shell = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Engine.Cmd, "/k")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })!;
        ResumeStopped.EndShell(shell);
        Assert.True(shell.WaitForExit(10_000));
        Assert.Equal(0, shell.ExitCode); // not -1: Windows Terminal keeps a dead tab for that
    }

    [Fact]
    public void LiveCountsSeeRecordsAndWrappers()
    {
        var root = TempRoot();
        try
        {
            var self = System.Diagnostics.Process.GetCurrentProcess();
            Directory.CreateDirectory(Path.Combine(root, ".cswap-runs"));
            File.WriteAllText(Path.Combine(root, ".cswap-runs", Environment.ProcessId.ToString()), "");
            File.WriteAllText(Path.Combine(root, ".cswap-runs", "2147483000"), ""); // no such process
            Assert.Equal((0, 1), ResumeStopped.LiveCounts(root));
            Directory.CreateDirectory(Path.Combine(root, "sessions"));
            File.WriteAllText(Path.Combine(root, "sessions", $"{self.Id}.abc.key"), ""); // a live claude without its record
            File.WriteAllText(Path.Combine(root, "sessions", "2147483000.abc.key"), "");
            Assert.Equal((1, 1), ResumeStopped.LiveCounts(root));
            // A window nothing identifies is never seen as done, so its profile is held and it is never closed.
            Assert.Empty(ResumeStopped.Scan([(root, 2)], DateTime.UtcNow, includeBusy: true));
            Assert.Empty(ResumeStopped.LiveTranscripts([(root, 2)], DateTime.UtcNow));
            Assert.False(ResumeStopped.Releasable([], records: 1, runs: 1));
            Assert.Equal(["a window still working"], ResumeStopped.Holders([], records: 1, runs: 1));
            File.WriteAllText(Path.Combine(root, "sessions", "1.json"),
                $$"""{"pid":{{self.Id}},"sessionId":"sid","procStart":"{{self.StartTime.ToUniversalTime().ToFileTimeUtc()}}"}""");
            File.WriteAllText(Path.Combine(root, "sessions", "2.json"), """{"pid":2147483000,"sessionId":"old","procStart":"1"}""");
            Assert.Equal((1, 1), ResumeStopped.LiveCounts(root)); // its key and its record are one claude
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AClaudeRunningTheAgentsBridgeIsNeverAWindowToMove()
    {
        var root = TempRoot();
        try
        {
            Assert.DoesNotContain(Environment.ProcessId, ResumeStopped.BridgeHosts());
            var bridge = Path.Combine(root, "patch-goblins.exe"); // stands in for `Patch Goblins agents-mcp`, a child of this "claude"
            File.Copy(Engine.Cmd, bridge);
            using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(bridge, "/k")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })!;
            try { Assert.Contains(Environment.ProcessId, ResumeStopped.BridgeHosts()); }
            finally { child.Kill(); child.WaitForExit(10_000); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AHeadlessAgentRunIsARunNotAWindowAndAStaleMarkerIsNotLive()
    {
        var root = TempRoot();
        try
        {
            var self = System.Diagnostics.Process.GetCurrentProcess();
            var stamp = self.StartTime.ToUniversalTime().ToFileTimeUtc();
            Directory.CreateDirectory(Path.Combine(root, ".cswap-runs"));
            File.WriteAllText(Path.Combine(root, ".cswap-runs", self.Id.ToString()), "12345"); // a recycled pid: another process's stamp
            Assert.Equal((0, 0), ResumeStopped.LiveCounts(root));
            File.WriteAllText(Path.Combine(root, ".cswap-runs", self.Id.ToString()), stamp.ToString());
            Directory.CreateDirectory(Path.Combine(root, "sessions"));
            File.WriteAllText(Path.Combine(root, "sessions", $"{self.Id}.abc.key"), "");
            File.WriteAllText(Path.Combine(root, "sessions", $"{self.Id}.json"),
                $$"""{"pid":{{self.Id}},"sessionId":"sid","status":"busy","kind":"interactive","entrypoint":"sdk-cli","procStart":"{{stamp}}"}""");
            Assert.Equal((0, 1), ResumeStopped.LiveCounts(root)); // `cswap run N -- claude -p`: one run, no window
            Assert.True(ResumeStopped.AgentsOnly([], records: 0, runs: 1, agentRuns: 1));
            Assert.Empty(ResumeStopped.Scan([(root, 2)], DateTime.UtcNow, includeBusy: true));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ACopyTheTrayMadeIsNotANewRefusalButTheNextErrorThereIs()
    {
        var root = TempRoot();
        try
        {
            var hits = new List<LimitHit>();
            using var w = new LimitWatch(hits.Add, _ => { });
            w.Remember(Now); // before the copy lands
            var f = Write(root, "sid.jsonl", LimitLine(Now, Now.AddHours(1)));
            w.Poll([(f, root)]);
            Assert.Empty(hits);
            File.AppendAllLines(f, [LimitLine(Now.AddSeconds(2), Now.AddHours(1))]); // the reopened window's own refusal
            w.Poll([(f, root)]);
            Assert.Single(hits);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ALimitOnlyInASubagentFileStopsItsSessionToo()
    {
        var root = TempRoot();
        try
        {
            var agent = Write(root, Path.Combine("sid", "subagents", "a.jsonl"), LimitLine(Now, Now.AddHours(1)));
            Assert.Equal("sid", ResumeStopped.AgentSession(root, agent));
            Assert.Null(ResumeStopped.AgentSession(root, Path.Combine(root, "projects", "C--w", "sid.jsonl")));
            Stage(root, "idle", """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"waiting on the workflow"}],"stop_reason":"tool_use"}}""");
            var main = Path.Combine(root, "projects", "C--w", "sid.jsonl");
            File.SetLastWriteTimeUtc(main, Now.UtcDateTime.AddSeconds(-5)); // the main loop wrote nothing since the agent's error
            var later = DateTime.UtcNow.AddSeconds(10);
            Assert.Empty(ResumeStopped.Scan([(root, 3)], later, DateTime.MinValue));
            var agentLimit = new Limit("five_hour", Now.AddHours(1), Now);
            var s = Assert.Single(ResumeStopped.Scan([(root, 3)], later, DateTime.MinValue, agentLimits: new Dictionary<string, Limit> { ["sid"] = agentLimit }));
            Assert.True(s.AtLimit);
            Assert.True(ResumeStopped.Unchanged(s)); // the main transcript is as quiet as at the scan
            // The main loop carried on after the in-place switch (it wrote since): it is not stopped, so never closed and relaunched.
            File.SetLastWriteTimeUtc(main, Now.UtcDateTime.AddSeconds(5));
            Assert.Empty(ResumeStopped.Scan([(root, 3)], later, DateTime.MinValue, agentLimits: new Dictionary<string, Limit> { ["sid"] = agentLimit }));
            // And an agent limit whose reset has passed stops nothing.
            File.SetLastWriteTimeUtc(main, Now.UtcDateTime.AddSeconds(-5));
            Assert.Empty(ResumeStopped.Scan([(root, 3)], Now.UtcDateTime.AddHours(2), DateTime.MinValue, agentLimits: new Dictionary<string, Limit> { ["sid"] = agentLimit }));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ResetsThatFallTogetherAreTheLoginsOnceItIsTrustedElseNobodys()
    {
        var resets = Now.AddHours(2);
        Account[] both = [A(1, 100, active: true, resets: resets), A(2, 100, resets: resets.AddSeconds(30))]; // Auto-Prime: two accounts reset together
        var l = new Limit("five_hour", resets, Now);
        Assert.Equal(2, ResumeStopped.Refuser(l, null, both, (2, DateTime.UtcNow.AddMinutes(-5)))); // the login, long enough after its last switch
        Assert.Null(ResumeStopped.Refuser(l, null, both, (2, DateTime.UtcNow))); // too soon after one: no import, no switch
    }

    [Fact]
    public async Task ALaterRefusalWithAnAlignedResetStillMovesTheLogin()
    {
        var resets = Now.AddHours(2);
        var rig = new Rig(A(1, 10, active: true, resets: resets), A(2, 20, resets: resets.AddSeconds(20)), A(3, 60)); // 1 and 2 reset within seconds
        Assert.Equal(2, (await rig.Hit(Now, resets.AddSeconds(10)))!.Switched!.To!.Number);
        rig.Snap = rig.Snap with { ActiveAccountNumber = 2, Accounts = [.. rig.Snap.Accounts.Select(a => a with { Active = a.Number == 2 })] };
        rig.Changed = DateTime.UtcNow.AddMinutes(-90); // 2 ran for a while, then ran out too
        var o = await rig.Hit(Now.AddMinutes(90), resets.AddSeconds(10), file: @"C:\x\other.jsonl"); // another window, which ran on 2
        Assert.Equal(3, o!.Switched!.To!.Number); // not charged to 1 again, which would have switched nothing
        Assert.Equal([2, 3], rig.Switched);
        Assert.Contains(2, rig.Switch.RefusedUntil.Keys);
    }

    [Fact]
    public async Task AReplayedRefusalStaysWithItsFirstRefuser()
    {
        var resets = Now.AddHours(2);
        var rig = new Rig(A(1, 10, active: true, resets: resets), A(2, 20, resets: resets.AddSeconds(20)), A(3, 60));
        Assert.Equal(2, (await rig.Hit(Now, resets.AddSeconds(10)))!.Switched!.To!.Number);
        rig.Snap = rig.Snap with { ActiveAccountNumber = 2, Accounts = [.. rig.Snap.Accounts.Select(a => a with { Active = a.Number == 2 })] };
        rig.Changed = DateTime.UtcNow.AddMinutes(-5); // the stuck window refuses again, well after the switch
        Assert.Null(await rig.Hit(Now.AddMinutes(5), resets.AddSeconds(10))); // the same window and reset: still 1's, so 2 is not charged
        Assert.Equal([2], rig.Switched);
        Assert.DoesNotContain(2, rig.Switch.RefusedUntil.Keys);
    }

    [Fact]
    public async Task ASessionThatCarriedOnAfterTheSwitchIsRefusedByTheNewLogin()
    {
        var root = TempRoot();
        try
        {
            var resets = Now.AddHours(2);
            var rig = new Rig(A(1, 10, active: true, resets: resets), A(2, 20, resets: resets), A(3, 60)); // Auto-Prime: 1 and 2 reset together
            var f = Write(root, "sid.jsonl", LimitLine(Now, resets));
            Assert.Equal(2, (await rig.Hit(Now, resets, file: f))!.Switched!.To!.Number);
            rig.Snap = rig.Snap with { ActiveAccountNumber = 2, Accounts = [.. rig.Snap.Accounts.Select(a => a with { Active = a.Number == 2 })] };
            rig.Changed = DateTime.UtcNow.AddMinutes(-90);
            File.AppendAllLines(f, [$$$"""{"type":"assistant","timestamp":"{{{Now.AddMinutes(1):O}}}","message":{"role":"assistant","content":[{"type":"text","text":"carried on"}],"stop_reason":"end_turn"}}""",
                LimitLine(Now.AddMinutes(90), resets)]);
            Assert.Equal(3, (await rig.Hit(Now.AddMinutes(90), resets, file: f))!.Switched!.To!.Number); // ran on 2 since: 2's refusal, not 1's replayed
            Assert.Contains(2, rig.Switch.RefusedUntil.Keys);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AMovedSessionsEarlierRefusalsAreForgotten()
    {
        var resets = Now.AddHours(2);
        var rig = new Rig(A(1, 10, active: true, resets: resets), A(2, 20, resets: resets), A(3, 60));
        await rig.Hit(Now, resets); // sid is charged to 1
        rig.Snap = rig.Snap with { ActiveAccountNumber = 2, Accounts = [.. rig.Snap.Accounts.Select(a => a with { Active = a.Number == 2 })] };
        rig.Changed = DateTime.UtcNow.AddMinutes(-90);
        rig.Switch.Forget("sid"); // the tray reopened it
        Assert.Equal(3, (await rig.Hit(Now.AddMinutes(90), resets))!.Switched!.To!.Number);
    }

    [Fact]
    public void AWindowReopenedOnAnAccountIsNeverPickedBySwitchTargetsMeanwhile()
    {
        var resets = Now.AddHours(2);
        var accounts = new[] { A(1, 10, active: true, resets: resets), A(2, 20), A(3, 60) };
        var stamps = new Dictionary<string, DateTime> { ["sid#2"] = DateTime.UtcNow, ["old#3"] = DateTime.UtcNow.AddMinutes(-5), ["sid#@123"] = DateTime.UtcNow };
        var live = ResumeStopped.Launching(stamps, DateTime.UtcNow).ToHashSet();
        Assert.Equal([2], live); // the plan-time stamp counts; an old one and a move stamp do not
        Assert.Equal([3], ResumeStopped.SwitchTargets(accounts, 1, live, new Dictionary<int, DateTimeOffset>(), Now));
    }

    [Fact]
    public void AnythingTheOwnerDidAfterTheErrorMeansTheWindowIsNotStopped()
    {
        var limit = LimitLine(Now, Now.AddHours(1));
        var typed = """{"type":"user","origin":{"kind":"human"},"message":{"role":"user","content":"do Y instead"}}""";
        var interrupted = """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""";
        var bash = """{"type":"user","message":{"role":"user","content":"<bash-input>git status</bash-input>"}}""";
        var auto = """{"type":"user","origin":{"kind":"auto-continue"},"message":{"role":"user","content":"continue"}}""";
        Assert.Null(ResumeStopped.LimitOf([limit, typed, interrupted])); // the owner went on, then interrupted: nothing to relaunch
        Assert.Null(ResumeStopped.LimitOf([limit, bash]));
        Assert.Null(ResumeStopped.LimitOf([limit, auto])); // only a background task's notice is not the owner's
    }

    [Fact]
    public void AnOldAgentLimitDoesNotHideAReopenedSessionsFinishedTurn()
    {
        var root = TempRoot();
        try
        {
            Stage(root, "idle", """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn"}}""");
            var old = new Limit("five_hour", Now.AddHours(1), Now.AddDays(-1)); // from before this process started
            var s = Assert.Single(ResumeStopped.Scan([(root, 3)], DateTime.UtcNow.AddSeconds(30), DateTime.MinValue, agentLimits: new Dictionary<string, Limit> { ["sid"] = old }));
            Assert.False(s.AtLimit); // a finished turn: the threshold move still sees it
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- the per-window decision ---------------------------------------------------------------------------------

    static StoppedSession Stopped(string status = "idle", bool atLimit = true, string dir = @"C:\cfg") =>
        new(1, "sid", @"C:\w", dir, 3, dir + @"\projects\w\sid.jsonl", atLimit, Status: status);

    static ResumeStopped.Step Decide(StoppedSession s, int? target = 2, double idleSeconds = 60, bool recent = false, int moves = 0, bool unchanged = true) =>
        ResumeStopped.Decide(s, TimeSpan.FromSeconds(idleSeconds), TimeSpan.FromSeconds(5), target, recent, moves, () => unchanged);

    [Fact]
    public void ABusyOrWaitingWindowIsNeverMovedWhateverElseHolds()
    {
        foreach (var status in new[] { "busy", "waiting" })
        {
            var step = Decide(Stopped(status), unchanged: true);
            Assert.Equal(status == "waiting" ? ResumeStopped.Step.Wait : ResumeStopped.Step.Left, step); // one balloon names the left ones
        }
        Assert.Equal(ResumeStopped.Step.Move, Decide(Stopped())); // idle at the limit, with room: moved
        // In a shell at the limit (a background command running): its turn is over and it cannot carry on, so moved too, but
        // a finished turn below the limit with a background command is left alone.
        Assert.Equal(ResumeStopped.Step.Move, Decide(Stopped("shell")));
        Assert.Equal(ResumeStopped.Step.Ignore, Decide(Stopped("shell", atLimit: false)));
    }

    [Fact]
    public void AWindowWaitsForTypingAndForItsOwnStateToHold()
    {
        Assert.Equal(ResumeStopped.Step.Wait, Decide(Stopped(), idleSeconds: 2)); // mid-keystroke: the next poll
        Assert.Equal(ResumeStopped.Step.Wait, Decide(Stopped(), unchanged: false)); // something happened since the scan
        var ran = false;
        Assert.Equal(ResumeStopped.Step.NoRoom, ResumeStopped.Decide(Stopped(), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), null, false, 0, () => ran = true));
        Assert.False(ran); // the last look runs only when a close is about to follow
    }

    [Fact]
    public void ARecentOrOftenMovedWindowStaysPut()
    {
        Assert.Equal(ResumeStopped.Step.Recent, Decide(Stopped(), recent: true));
        Assert.Equal(ResumeStopped.Step.Capped, Decide(Stopped(), moves: ResumeStopped.MovesPerHour));
        Assert.Equal(ResumeStopped.Step.Move, Decide(Stopped(), moves: ResumeStopped.MovesPerHour - 1));
    }

    [Fact]
    public void AFinishedTurnMovesOnlyOffAProfileAfterAMinuteOfQuiet()
    {
        Assert.Equal(ResumeStopped.Step.Ignore, Decide(Stopped(atLimit: false, dir: ResumeStopped.DefaultDir)));
        Assert.Equal(ResumeStopped.Step.Ignore, Decide(Stopped(atLimit: false), idleSeconds: 30));
        Assert.Equal(ResumeStopped.Step.Move, Decide(Stopped(atLimit: false)));
        Assert.Equal(ResumeStopped.Step.Ignore, Decide(Stopped("busy", atLimit: false)));
    }

    // ---- shutdown and the engine gate ------------------------------------------------------------------------------

    [Fact]
    public void ADisposedWatchStartsNoWatcherAndPostsNoHit()
    {
        var root = TempRoot();
        try
        {
            var hits = new List<LimitHit>();
            var w = new LimitWatch(hits.Add, _ => { });
            var f = Write(root, "sid.jsonl", LimitLine(Now, Now.AddHours(1)));
            w.Dispose();
            w.Sync([root]);
            Assert.False(w.Watching(root)); // a Sync still running at exit leaks no watcher
            w.Poll([(f, root)]);
            Assert.Empty(hits); // and a Read still running at exit posts into a gone UI
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ASlowImportNeverDelaysTheSwitch()
    {
        var never = new TaskCompletionSource();
        var rig = new Rig(A(1, 50, active: true), A(2, 20)) { Report = _ => never.Task };
        var o = await rig.Hit(Now, Now.AddHours(1)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, o!.Switched!.To!.Number); // the import is still pending
    }

    [Fact]
    public async Task SwitchingIsSetWhileTheEngineSwitchRuns()
    {
        var gate = new TaskCompletionSource();
        var rig = new Rig(A(1, 50, active: true), A(2, 20)) { SwitchGate = gate.Task };
        var hit = rig.Hit(Now, Now.AddHours(1));
        Assert.True(rig.Switch.Switching);
        gate.SetResult();
        await hit;
        Assert.False(rig.Switch.Switching);
    }

    [Fact]
    public async Task RepeatedRefusalsDoNotRespawnFailingSwitchesEachTime()
    {
        var rig = new Rig(A(1, 50, active: true), A(2, 20)) { Failing = [2] };
        var resets = Now.AddHours(1);
        Assert.NotNull((await rig.Hit(Now, resets))!.Error);
        for (var i = 1; i <= 5; i++) Assert.Null(await rig.Hit(Now.AddSeconds(i), resets, file: $@"C:\x\sid\subagents\a{i}.jsonl")); // the burst: the login is still on the refuser
        Assert.Single(rig.Log, l => l.StartsWith("switch 1->2 failed"));
    }

    [Fact]
    public void RememberForgetsOnlyErrorsOlderThanTheFreshnessWindow()
    {
        var clock = Now;
        using var w = new LimitWatch(_ => { }, _ => { }, () => clock);
        var recent = Now.AddSeconds(-10);
        w.Remember(recent);
        for (var i = 0; i < 1100; i++) w.Remember(Now.AddHours(-1).AddSeconds(i)); // history: older than the window
        w.Remember(Now.AddSeconds(-5)); // over the count: old entries are pruned
        Assert.False(w.Remember(recent)); // a recent one is still known
    }
}
