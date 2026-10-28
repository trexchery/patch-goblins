using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

[Collection("own process")] // stages windows under this process's pid: never alongside LimitWatchTests' bridge test
public class ResumeStoppedTests
{
    const string Limit = """{"type":"assistant","isApiErrorMessage":true,"message":{"role":"assistant","content":[{"type":"text","text":"You've hit your session limit · resets 1:30am (America/Chicago)"}]}}""";
    const string Done = """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"All done."}]}}""";
    const string Typed = """{"type":"user","message":{"role":"user","content":"continue"}}""";
    const string Meta = """{"type":"system","subtype":"stop_hook_summary"}""";

    [Fact]
    public void LimitErrorAsLastMessageIsStopped() =>
        Assert.True(ResumeStopped.EndsAtLimit([Done, Typed, Limit, Meta, "{\"type\":\"api-req"]));

    [Fact]
    public void FinishedOrAnsweredSessionsAreNotStopped()
    {
        Assert.False(ResumeStopped.EndsAtLimit([Limit, Typed])); // someone typed after the limit
        Assert.False(ResumeStopped.EndsAtLimit([Limit, Done]));
        Assert.False(ResumeStopped.EndsAtLimit([Meta]));
        Assert.False(ResumeStopped.EndsAtLimit([
            """{"type":"assistant","isApiErrorMessage":true,"message":{"content":[{"type":"text","text":"API Error: 500 overloaded"}]}}"""]));
    }

    static Account A(int n, double five, bool active = false, bool disabled = false, string status = "ok") =>
        new(n, $"{n}@example.com", active, status, new Usage(new Window(five, null), new Window(10, null), null, null), Disabled: disabled);

    static StoppedSession On(int? account) => new(1, "id", @"C:\w", @"C:\cfg", account, @"C:\cfg\projects\w\id.jsonl");

    [Fact]
    public void OwnAccountWinsOnceItHasRoom() =>
        Assert.Equal(3, ResumeStopped.Target(On(3), [A(1, 0), A(3, 40, active: true)], _ => true));

    [Fact]
    public void ExhaustedAccountMovesToTheOneWithMostRoom() =>
        Assert.Equal(2, ResumeStopped.Target(On(3), [A(1, 50), A(2, 5), A(3, 100, active: true)], _ => true));

    [Fact]
    public void ExcludedUnreadableAndProfilelessAccountsAreSkipped()
    {
        Assert.Equal(4, ResumeStopped.Target(On(3),
            [A(1, 0, disabled: true), A(2, 0, status: "token_expired"), A(3, 100, active: true), A(4, 60), A(5, 1)], n => n != 5));
        Assert.Null(ResumeStopped.Target(On(3), [A(1, 100), A(3, 100, active: true)], _ => true)); // nowhere has room: wait
    }

    [Fact]
    public void ActiveAccountNeedsNoProfile() =>
        Assert.Equal(1, ResumeStopped.Target(On(3), [A(1, 10, active: true), A(3, 100)], _ => false));

    [Fact]
    public void TranscriptAndItsFolderAreCopiedNextToTheTarget()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var src = Path.Combine(root, "a", "projects", "C--w");
            Directory.CreateDirectory(Path.Combine(src, "sid", "subagents"));
            File.WriteAllText(Path.Combine(src, "sid.jsonl"), Limit);
            File.WriteAllText(Path.Combine(src, "sid", "subagents", "x.jsonl"), "{}");
            var s = new StoppedSession(1, "sid", @"C:\w", Path.Combine(root, "a"), 3, Path.Combine(src, "sid.jsonl"));
            ResumeStopped.CopyTranscript(s, Path.Combine(root, "b"));
            Assert.Equal(Limit, File.ReadAllText(Path.Combine(root, "b", "projects", "C--w", "sid.jsonl")));
            Assert.True(File.Exists(Path.Combine(root, "b", "projects", "C--w", "sid", "subagents", "x.jsonl")));
            ResumeStopped.CopyTranscript(s, Path.Combine(root, "a")); // same profile: nothing to do
        }
        finally { Directory.Delete(root, true); }
    }

    const string ToolCall = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{}}]}}""";
    const string ToolResult = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";

    [Fact]
    public void FinishedReplyEndsTheTurn()
    {
        Assert.True(ResumeStopped.EndsTurn([Typed, ToolCall, ToolResult, Done, Meta]));
        Assert.True(ResumeStopped.EndsTurn(["{\"type\":\"assis", Done])); // a line cut by the tail window is skipped
    }

    [Fact]
    public void SlashCommandsAfterTheReplyStillEndTheTurn() =>
        Assert.True(ResumeStopped.EndsTurn([Done,
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"<local-command-caveat>Caveat</local-command-caveat>"}}""",
            """{"type":"user","message":{"role":"user","content":"<command-name>/usage</command-name>"}}""",
            """{"type":"user","message":{"role":"user","content":"<local-command-stdout>83% used</local-command-stdout>"}}""",
            """{"type":"user","isCompactSummary":true,"message":{"role":"user","content":"This session is being continued"}}"""]));

    [Fact]
    public void RunningOrCutOffTurnsDoNotEnd()
    {
        Assert.False(ResumeStopped.EndsTurn([Done, Typed]));            // a message waiting for its answer
        Assert.False(ResumeStopped.EndsTurn([Typed, ToolCall]));        // a tool call still open
        Assert.False(ResumeStopped.EndsTurn([ToolCall, ToolResult]));   // the reply to a tool not written yet
        Assert.False(ResumeStopped.EndsTurn([Done, Limit]));            // stopped at the limit: the other path
        Assert.False(ResumeStopped.EndsTurn([
            """{"type":"assistant","message":{"role":"assistant","stop_reason":"max_tokens","content":[{"type":"text","text":"cut"}]}}"""]));
        Assert.False(ResumeStopped.EndsTurn([Meta]));
        Assert.False(ResumeStopped.EndsTurn([]));
    }

    static StoppedSession Done3 => On(3) with { AtLimit = false };

    [Fact]
    public void FinishedTurnMovesOnlyOnceItsAccountReachesTheThreshold()
    {
        Assert.Null(ResumeStopped.Target(Done3, [A(1, 0), A(3, 89)], _ => true, threshold: 90, hysteresis: 10));
        Assert.Equal(1, ResumeStopped.Target(Done3, [A(1, 0), A(3, 90)], _ => true, threshold: 90, hysteresis: 10));
    }

    [Fact]
    public void FinishedTurnTargetIsBelowTheThresholdByTheHysteresis()
    {
        // 85 is below the line but only 7 points better: no move, or two near-full accounts would trade the window.
        Assert.Null(ResumeStopped.Target(Done3, [A(1, 85), A(2, 95), A(3, 92)], _ => true, threshold: 90, hysteresis: 10));
        Assert.Equal(4, ResumeStopped.Target(Done3, [A(1, 85), A(3, 92), A(4, 60), A(5, 20)], n => n != 5, threshold: 90, hysteresis: 10));
    }

    [Fact]
    public void FinishedTurnLeavesTheDefaultLoginsNextAccountFree()
    {
        // The default login at 88 is near its line of 90: #4 is where it goes next, so the window takes #5, or stays without it.
        Assert.Equal(5, ResumeStopped.Target(Done3, [A(1, 88, active: true), A(3, 92), A(4, 20), A(5, 40)], _ => true, threshold: 90, hysteresis: 10));
        Assert.Null(ResumeStopped.Target(Done3, [A(1, 88, active: true), A(3, 92), A(4, 20)], _ => true, threshold: 90, hysteresis: 10));
        Assert.Equal(4, ResumeStopped.Target(Done3, [A(1, 50, active: true), A(3, 92), A(4, 20)], _ => true, threshold: 90, hysteresis: 10)); // far from it
        Assert.Equal(4, ResumeStopped.Target(On(3), [A(1, 88, active: true), A(3, 100), A(4, 20)], _ => true, threshold: 90, hysteresis: 10)); // stopped: cannot wait
    }

    [Fact]
    public void FinishedTurnStaysWhenItsOwnUsageIsUnreadable()
    {
        Assert.Null(ResumeStopped.Target(Done3, [A(1, 0), A(3, 95, status: "token_expired")], _ => true, threshold: 90, hysteresis: 10));
        Assert.Null(ResumeStopped.Target(Done3 with { Account = null }, [A(1, 0)], _ => true, threshold: 90, hysteresis: 10));
    }

    [Fact]
    public void LimitStopIgnoresTheThreshold() // hard-limit recovery: any room at all will do
        => Assert.Equal(1, ResumeStopped.Target(On(3), [A(1, 95), A(3, 100)], _ => true, threshold: 90, hysteresis: 10));

    [Fact]
    public void FinishedTurnReopensWithoutAPrompt()
    {
        Assert.Equal(["--resume", "id"], ResumeStopped.ResumeArgs(Done3));
        Assert.Equal(["--resume", "id", ResumeStopped.Prompt], ResumeStopped.ResumeArgs(On(3)));
        // Moved from a shell: told its background commands still run, so it doesn't start them twice.
        Assert.Equal(["--resume", "id", ResumeStopped.Prompt + ResumeStopped.Background], ResumeStopped.ResumeArgs(On(3) with { Status = "shell" }));
    }

    [Fact]
    public void CheckpointsAndTodosMoveWithTheTranscript()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var a = Path.Combine(root, "a");
            var src = Path.Combine(a, "projects", "C--w");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "sid.jsonl"), Done);
            Directory.CreateDirectory(Path.Combine(a, "file-history", "sid"));
            File.WriteAllText(Path.Combine(a, "file-history", "sid", "abc@v1"), "old");
            Directory.CreateDirectory(Path.Combine(a, "todos"));
            File.WriteAllText(Path.Combine(a, "todos", "sid-agent-sid.json"), "[]");
            File.WriteAllText(Path.Combine(a, "todos", "other-agent-other.json"), "[]");
            ResumeStopped.CopyTranscript(new StoppedSession(1, "sid", @"C:\w", a, 3, Path.Combine(src, "sid.jsonl")), Path.Combine(root, "b"));
            Assert.Equal("old", File.ReadAllText(Path.Combine(root, "b", "file-history", "sid", "abc@v1")));
            Assert.True(File.Exists(Path.Combine(root, "b", "todos", "sid-agent-sid.json")));
            Assert.False(File.Exists(Path.Combine(root, "b", "todos", "other-agent-other.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    /// A window record for this very process (a real, live pid), as `Unchanged` re-reads it.
    static string Rec(string status)
    {
        var self = System.Diagnostics.Process.GetCurrentProcess();
        return $$"""{"pid":{{self.Id}},"sessionId":"sid","status":"{{status}}","procStart":"{{self.StartTime.ToUniversalTime().ToFileTimeUtc()}}"}""";
    }

    [Fact]
    public void AnyChangeSinceTheScanCancelsTheMove()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var t = Path.Combine(root, "sid.jsonl");
            var rec = Path.Combine(root, "1.json");
            File.WriteAllText(t, Done);
            File.WriteAllText(rec, Rec("idle"));
            var s = new StoppedSession(Environment.ProcessId, "sid", root, root, 3, t, false, rec, File.GetLastWriteTimeUtc(t));
            Assert.True(ResumeStopped.Unchanged(s));
            File.WriteAllText(rec, Rec("busy"));
            Assert.False(ResumeStopped.Unchanged(s));
            File.WriteAllText(rec, Rec("idle"));
            File.AppendAllText(t, "\n" + Typed);
            File.SetLastWriteTimeUtc(t, s.Seen.AddSeconds(1));
            Assert.False(ResumeStopped.Unchanged(s));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MoveGuardSurvivesARestartAndForgetsOldEntries()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(root, "sub", "resumed.json");
            var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
            ResumeStopped.SaveMoves(path, new() { ["new"] = now.AddMinutes(-5), ["old"] = now.AddDays(-2) }, now);
            var back = ResumeStopped.LoadMoves(path);
            Assert.Equal(["new"], back.Keys);
            Assert.Equal(now.AddMinutes(-5), back["new"].ToUniversalTime());
            File.WriteAllText(path, "not json");
            Assert.Empty(ResumeStopped.LoadMoves(path));
            Assert.Empty(ResumeStopped.LoadMoves(Path.Combine(root, "missing.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    // The error as Claude Code writes it today: the structured API refusal with its window and reset.
    const string LimitV2 = """{"type":"assistant","timestamp":"2026-10-20T22:33:03.355Z","apiError":"usage_limit_reached","isApiErrorMessage":true,"apiErrorParams":{"rate_limit_info":{"status":"rejected","resetsAt":1792546800,"rateLimitType":"five_hour"}},"message":{"role":"assistant","content":[{"type":"text","text":"You've hit your limit"}]}}""";
    const string Notice = """{"type":"system","subtype":"informational","content":"Remote Control disconnected"}""";
    const string TurnTime = """{"type":"system","subtype":"turn_duration"}""";
    const string LocalCaveat = """{"type":"user","isMeta":true,"message":{"role":"user","content":"<local-command-caveat>Caveat</local-command-caveat>"}}""";
    const string LocalModel = """{"type":"user","message":{"role":"user","content":"<command-name>/model</command-name>"}}""";
    const string Compact = """{"type":"user","isCompactSummary":true,"message":{"role":"user","content":"Summary"}}""";
    const string LocalUsage = """{"type":"user","message":{"role":"user","content":"<command-name>/usage</command-name>"}}""";

    static string LimitAt(DateTimeOffset t) => LimitV2.Replace("2026-10-20T22:33:03.355Z", t.ToString("O"));

    static PatchGoblins.Limit LimitStop(DateTimeOffset? resets, DateTimeOffset at, string? window = "five_hour") => new(window, resets, at);

    [Fact]
    public void StructuredLimitErrorIsReadWithItsReset()
    {
        var l = ResumeStopped.LimitOf([LimitV2, Notice, TurnTime]);
        Assert.NotNull(l);
        Assert.Equal("five_hour", l.Window);
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T01:40:00Z"), l.ResetsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T22:33:03.355Z"), l.At);
        var old = ResumeStopped.LimitOf([Limit]); // an error without the details still counts
        Assert.NotNull(old);
        Assert.Null(old.Window);
        Assert.Null(old.ResetsAt);
    }

    [Fact]
    public void RewordedOrWeeklyLimitsStillCount()
    {
        Assert.True(ResumeStopped.EndsAtLimit(["""{"type":"assistant","apiError":"usage_limit_reached","isApiErrorMessage":true,"message":{"content":[{"type":"text","text":"Weekly limit reached"}]}}"""]));
        Assert.False(ResumeStopped.EndsAtLimit(["""{"type":"assistant","isApiErrorMessage":true,"message":{"content":[{"type":"text","text":"API Error: 500"}]}}"""]));
    }

    [Fact]
    public void LocalCommandsAfterTheLimitStillCount()
    {
        Assert.NotNull(ResumeStopped.LimitOf([LimitV2, LocalCaveat, LocalUsage]));
        Assert.Null(ResumeStopped.LimitOf([LimitV2, Typed]));
        Assert.Null(ResumeStopped.LimitOf([LimitV2, LocalModel])); // a command that changes the session is someone at the keyboard
        Assert.Null(ResumeStopped.LimitOf([LimitV2, Compact]));
    }

    /// A window record and transcript in `root` for this very process (a real, live pid, stamped with its start time).
    static void Stage(string root, string status, string[] lines, long? procStart = -1, int? pid = null)
    {
        var self = System.Diagnostics.Process.GetCurrentProcess();
        var stamp = procStart == -1 ? self.StartTime.ToUniversalTime().ToFileTimeUtc() : procStart;
        var project = Path.Combine(root, "projects", "C--w");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        File.WriteAllLines(Path.Combine(project, "sid.jsonl"), lines);
        File.WriteAllText(Path.Combine(root, "sessions", "1.json"),
            $$"""{"pid":{{pid ?? self.Id}},"sessionId":"sid","cwd":"C:\\w","status":"{{status}}","kind":"interactive"{{(stamp is { } f ? $",\"procStart\":\"{f}\"" : "")}}}""");
    }

    // `ahead` stands in for time passing: a stop must be this process's own (after it started), so staged errors are stamped now.
    static List<StoppedSession> ScanOf(string root, double ahead = 10) => ResumeStopped.Scan([(root, 3)], DateTime.UtcNow.AddSeconds(ahead), DateTime.MinValue);

    [Fact]
    public void FreshlyReopenedClaudeIsNotStoppedByItsCopiedTranscript()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Stage(root, "idle", [Typed, Limit]); // an error without a timestamp: only the copied file's old write time dates it
            File.SetLastWriteTimeUtc(Path.Combine(root, "projects", "C--w", "sid.jsonl"), DateTime.UtcNow.AddHours(-1));
            Assert.Empty(ScanOf(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FreshlyReopenedClaudeWithoutAStampIsNotStopped()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Stage(root, "idle", [Typed, Limit], procStart: null);
            File.SetLastWriteTimeUtc(Path.Combine(root, "projects", "C--w", "sid.jsonl"), DateTime.UtcNow.AddHours(-1));
            Assert.Empty(ScanOf(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BusyWindowIsToldAboutOnlyAfterTheGrace()
    {
        var since = new Dictionary<string, DateTime>();
        var t0 = DateTime.UtcNow;
        var grace = TimeSpan.FromSeconds(10);
        Assert.False(ResumeStopped.BusyPast(since, "k", t0, grace));
        Assert.False(ResumeStopped.BusyPast(since, "k", t0.AddSeconds(2), grace)); // later passes do not restart the clock
        Assert.True(ResumeStopped.BusyPast(since, "k", t0.AddSeconds(12), grace));
    }

    [Fact]
    public async Task EndedShellExitsWithCodeZero()
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true })!;
        ResumeStopped.EndShell(p);
        await p.WaitForExitAsync();
        Assert.Equal(0, p.ExitCode); // Process.Kill would leave -1 and Windows Terminal would keep the tab
    }

    [Fact]
    public async Task RenamedClaudeImageIsStillSeen()
    {
        // Claude Code's updater renames the running image: ProcessName becomes claude.exe.old.<stamp>.<pid>.
        var root = Directory.CreateTempSubdirectory().FullName;
        var exe = Path.Combine(root, "claude.exe.old.1792529213345.31388.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        using var old = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-n 120 127.0.0.1")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            Assert.StartsWith("claude.exe.old", old.ProcessName);
            var stamp = old.StartTime.ToUniversalTime().ToFileTimeUtc();
            var lines = new[] { Typed, LimitAt(DateTimeOffset.UtcNow) };
            Stage(root, "idle", lines, stamp, old.Id);
            var s = Assert.Single(ScanOf(root));
            Assert.Equal(old.Id, s.Pid);
            Assert.True(s.AtLimit);
            Assert.Equal("five_hour", s.Limit?.Window);
            Stage(root, "idle", lines, stamp + TimeSpan.TicksPerHour, old.Id); // a recycled pid: the stamp names another process
            Assert.Empty(ScanOf(root));
            Stage(root, "idle", lines, null, old.Id); // unstamped: the normalised name is claude
            Assert.Single(ScanOf(root));
        }
        finally
        {
            if (!old.HasExited) old.Kill();
            await old.WaitForExitAsync();
            await Task.Delay(200); // the image stays locked a moment after exit
            try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // best effort: a slow exit can keep it locked
        }
    }

    [Fact]
    public void OnlyAnIdleWindowIsEverFound()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string[] atLimit = [Typed, LimitAt(DateTimeOffset.UtcNow)];
            Stage(root, "idle", atLimit);
            Assert.Single(ScanOf(root));
            Stage(root, "idle", [Typed, LimitAt(DateTimeOffset.UtcNow.AddDays(-1))]);
            Assert.Empty(ScanOf(root)); // a stop from before this process started: a resumed window, not stopped again
            Stage(root, "idle", atLimit);
            // shell: idle with a background command still running; moving it would orphan the command.
            foreach (var status in new[] { "shell", "waiting", "busy" })
            {
                Stage(root, status, atLimit);
                Assert.Empty(ScanOf(root));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LimitStopNeedsNoQuiet()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Stage(root, "idle", [Typed, LimitAt(DateTimeOffset.UtcNow)]);
            Assert.Single(ScanOf(root, 0)); // an error is final: the record being idle is the whole predicate, no timer
            Stage(root, "idle", [Typed, Done]);
            File.SetLastWriteTimeUtc(Path.Combine(root, "projects", "C--w", "sid.jsonl"), DateTime.UtcNow.AddSeconds(-6));
            Assert.Empty(ScanOf(root, 0)); // a finished turn still waits the longer quiet
        }
        finally { Directory.Delete(root, true); }
    }

    static StoppedSession Refused(DateTimeOffset resets, bool own = true, string? window = "five_hour") =>
        On(3) with { Limit = LimitStop(resets, resets.AddHours(-5), window), OwnRefused = own };

    [Fact]
    public void RefusedAccountIsNotReusedOnAStaleReading()
    {
        var now = DateTimeOffset.UtcNow;
        Account[] stale = [A(1, 20), A(3, 80)]; // the cached reading predates the refusal
        Assert.Equal(1, ResumeStopped.Target(Refused(now.AddHours(1)), stale, _ => true, now: now));
        Assert.Equal(3, ResumeStopped.Target(Refused(now.AddMinutes(-1)), stale, _ => true, now: now));
        Assert.Equal(3, ResumeStopped.Target(Refused(now.AddHours(1), own: false), stale, _ => true, now: now));
    }

    [Fact]
    public void DefaultLoginStopBeforeTheSwitchBelongsToThePreviousAccount()
    {
        var at = DateTimeOffset.UtcNow;
        Assert.False(ResumeStopped.OwnStop(true, at, at.UtcDateTime.AddSeconds(10)));
        Assert.False(ResumeStopped.OwnStop(true, at, at.UtcDateTime.AddSeconds(-10))); // just after a switch: could be the old login's
        Assert.True(ResumeStopped.OwnStop(true, at, at.UtcDateTime.AddSeconds(-60)));
        Assert.False(ResumeStopped.OwnStop(true, at, DateTime.MaxValue));
        Assert.True(ResumeStopped.OwnStop(false, at, at.UtcDateTime.AddSeconds(10)));
    }

    [Fact]
    public void OnePassSpreadsWindowsByHeadroom()
    {
        Account[] accounts = [A(1, 20), A(2, 25), A(3, 100)];
        Assert.Equal(1, ResumeStopped.Target(On(3), accounts, _ => true));
        Assert.Equal(2, ResumeStopped.Target(On(3), accounts, _ => true, load: new Dictionary<int, int> { [1] = 1 }));
    }

    [Fact]
    public void LimitReportMarksTheRefusedWindowFullOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var resets = now.AddHours(1);
        var stop = Refused(resets) with { Limit = LimitStop(resets, now.AddSeconds(-10)) };
        Account[] accounts = [A(1, 20), A(3, 80)];
        var done = new HashSet<string>();
        var doc = ResumeStopped.LimitReport([stop], accounts, now, done);
        Assert.NotNull(doc);
        using (var j = System.Text.Json.JsonDocument.Parse(doc))
        {
            var row = j.RootElement.GetProperty("accounts")[0];
            Assert.Equal(1, j.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("3@example.com", row.GetProperty("email").GetString());
            Assert.Equal("", row.GetProperty("organizationUuid").GetString());
            Assert.Equal("ok", row.GetProperty("usageStatus").GetString());
            Assert.True(row.GetProperty("usageAgeSeconds").GetDouble() >= 0);
            var usage = row.GetProperty("usage");
            Assert.Equal(100, usage.GetProperty("fiveHour").GetProperty("pct").GetDouble());
            Assert.Equal(resets.ToUnixTimeSeconds(), usage.GetProperty("fiveHour").GetProperty("resetsAt").GetDateTimeOffset().ToUnixTimeSeconds());
            Assert.Equal(10, usage.GetProperty("sevenDay").GetProperty("pct").GetDouble()); // kept from the snapshot
        }
        Assert.Null(ResumeStopped.LimitReport([stop], accounts, now, done)); // already handed over
        Assert.Null(ResumeStopped.LimitReport([Refused(resets, window: "seven_day_opus")], accounts, now, new HashSet<string>()));
        Assert.Null(ResumeStopped.LimitReport([stop with { OwnRefused = false }], accounts, now, new HashSet<string>()));
        Assert.Null(ResumeStopped.LimitReport([stop], accounts, resets.AddMinutes(1), new HashSet<string>())); // reset already passed
    }

    [Fact]
    public void ALimitStopSurvivesClaudeCodesOwnNotices()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var t = Path.Combine(root, "sid.jsonl");
            var rec = Path.Combine(root, "1.json");
            File.WriteAllLines(t, [Typed, LimitV2]);
            File.WriteAllText(rec, Rec("idle"));
            var s = new StoppedSession(Environment.ProcessId, "sid", root, root, 3, t, true, rec, File.GetLastWriteTimeUtc(t), ResumeStopped.LimitOf([LimitV2]), true);
            Assert.True(ResumeStopped.Unchanged(s));
            File.AppendAllText(t, Notice + "\n");
            File.SetLastWriteTimeUtc(t, s.Seen.AddSeconds(1));
            Assert.True(ResumeStopped.Unchanged(s)); // Remote Control dropping on a switch is not someone typing
            File.AppendAllText(t, Typed + "\n");
            File.SetLastWriteTimeUtc(t, s.Seen.AddSeconds(2)); // a coarse file clock must not make "someone typed" look like no change
            Assert.False(ResumeStopped.Unchanged(s));
            File.WriteAllLines(t, [Typed, LimitV2]);
            foreach (var status in new[] { "busy", "shell" })
            {
                File.WriteAllText(rec, Rec(status));
                Assert.False(ResumeStopped.Unchanged(s));
            }
            File.WriteAllText(rec, Rec("idle").Replace("\"sid\"", "\"other\"")); // the pid now belongs to another session
            Assert.False(ResumeStopped.Unchanged(s));
        }
        finally { Directory.Delete(root, true); }
    }

    /// The whole move on real files and a real process named claude: scan finds the finished turn on the
    /// account at the threshold, picks the target, closes the window, and the target profile can resume it.
    [Fact]
    public async Task FinishedTurnAtTheThresholdMovesEndToEnd()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var exe = Path.Combine(root, "claude.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        using var claude = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-n 120 127.0.0.1")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            var from = Path.Combine(root, "sessions", "3-c_example.com");
            var to = Path.Combine(root, "sessions", "1-a_example.com");
            var project = Path.Combine(from, "projects", "C--w");
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(Path.Combine(from, "sessions"));
            Directory.CreateDirectory(to);
            var transcript = Path.Combine(project, "sid.jsonl");
            File.WriteAllLines(transcript, [Typed, ToolCall, ToolResult, Done]);
            var record = Path.Combine(from, "sessions", $"{claude.Id}.json");
            File.WriteAllText(record, $$"""{"pid":{{claude.Id}},"sessionId":"sid","cwd":"C:\\w","status":"idle","kind":"interactive"}""");
            var now = File.GetLastWriteTimeUtc(transcript).AddMinutes(1);

            var busy = ResumeStopped.Scan([(from, 3)], File.GetLastWriteTimeUtc(transcript)); // not quiet yet
            Assert.Empty(busy);
            var s = Assert.Single(ResumeStopped.Scan([(from, 3)], now));
            Assert.False(s.AtLimit);
            Assert.Equal(1, ResumeStopped.Target(s, [A(1, 20), A(3, 93)], _ => true, threshold: 90, hysteresis: 10));
            Assert.True(ResumeStopped.Unchanged(s));

            await ResumeStopped.CloseAsync(s.Pid, s.Record);
            Assert.True(claude.HasExited);
            Assert.False(File.Exists(record)); // killed, Claude Code would leave it for a reused pid to revive
            ResumeStopped.CopyTranscript(s, to);
            Assert.Equal(File.ReadAllText(transcript), File.ReadAllText(Path.Combine(to, "projects", "C--w", "sid.jsonl")));
            Assert.Equal(["run", "1", "--", "--resume", "sid"], Launch.Run(1, false, ResumeStopped.ResumeArgs(s)));
        }
        finally
        {
            if (!claude.HasExited) claude.Kill();
            claude.WaitForExit();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AMoveClosesItsWindowOnlyWhileThePlanStillHolds()
    {
        var now = DateTimeOffset.UtcNow;
        var none = new Dictionary<int, DateTimeOffset>();
        Assert.True(ResumeStopped.SafeToClose(2, none, false, 1, 1, now)); // the plain case
        Assert.False(ResumeStopped.SafeToClose(2, new Dictionary<int, DateTimeOffset> { [2] = now.AddHours(1) }, false, 1, 1, now)); // refused since the plan
        Assert.True(ResumeStopped.SafeToClose(2, new Dictionary<int, DateTimeOffset> { [2] = now.AddHours(-1) }, false, 1, 1, now)); // its reset has passed
        Assert.False(ResumeStopped.SafeToClose(2, none, true, 1, 1, now)); // a switch is running
        Assert.False(ResumeStopped.SafeToClose(2, none, false, 1, 3, now)); // the login moved
    }

    [Fact]
    public void OnMacAndLinuxOnlyTheRecordedProcessIsEverClosedButUnknownStillHoldsItsProfile()
    {
        // Synthetic process facts only: no real process is looked at, let alone killed.
        var started = new DateTime(2026, 10, 27, 9, 30, 15, DateTimeKind.Utc);
        const string Stamp = "Tue Oct 27 09:30:15 2026"; // macOS ps lstart, C locale, UTC
        Func<DateTime> At(DateTime t) => () => t;
        Func<DateTime> unreadable = () => throw new System.ComponentModel.Win32Exception();
        Func<string?> claude = () => "/usr/local/bin/claude --resume", none = () => null, other = () => "/usr/bin/vim";
        bool Mac(string? stamp, Func<DateTime> start, Func<string?> cmd, bool kill) => ResumeStopped.PosixLive(stamp, start, false, none, cmd, kill);

        // A close: the process must be the one that wrote the record.
        Assert.True(Mac(Stamp, At(started.AddMilliseconds(900)), other, kill: true)); // lstart drops the fraction
        Assert.False(Mac(null, At(started), claude, kill: true)); // no stamp: not provably that window
        Assert.False(Mac("  ", At(started), claude, kill: true));
        Assert.False(Mac("27/10/2026 9:30", At(started), claude, kill: true)); // unparseable
        Assert.False(Mac(Stamp, At(started.AddMinutes(5)), claude, kill: true)); // the pid went to a later Claude Code session
        Assert.False(Mac(Stamp, At(started.AddMinutes(5)), none, kill: true)); // ... or to one whose command line cannot be read
        Assert.False(Mac(Stamp, At(started.AddMinutes(-5)), claude, kill: true));
        Assert.Throws<System.ComponentModel.Win32Exception>(() => Mac(Stamp, unreadable, claude, kill: true)); // IsLiveClaude: false
        Assert.False(ResumeStopped.PosixLive("12345", At(started), true, () => "99999", claude, kill: true)); // Linux: other ticks

        // Anything else (is a profile still in use?): unknown means alive, as before.
        Assert.True(Mac(null, At(started), other, kill: false));
        Assert.True(Mac("27/10/2026 9:30", At(started), other, kill: false));
        Assert.True(Mac(Stamp, At(started.AddMinutes(5)), claude, kill: false));
        Assert.True(Mac(Stamp, At(started.AddMinutes(5)), none, kill: false));
        Assert.False(Mac(Stamp, At(started.AddMinutes(5)), other, kill: false)); // a later process that is not claude
        Assert.True(ResumeStopped.PosixLive("12345", At(started), true, () => "12345", other, kill: false));
        Assert.False(ResumeStopped.PosixLive("12345", At(started), true, () => "99999", claude, kill: false));
    }

    const string Parked429 = """{"type":"assistant","isApiErrorMessage":true,"error":"rate_limit","apiErrorStatus":429,"message":{"model":"<synthetic>","role":"assistant","content":[{"type":"text","text":"You've hit your limit"}]}}""";

    /// A synthetic workflow run under `project/<sid>/subagents/workflows/<run>`: agent `a1` (key k1) done, `a2` (k2) in flight, parked at a 429.
    static string StageRun(string project, string sid, string run, bool hidden = false)
    {
        var dir = Path.Combine(project, sid, "subagents", "workflows", run);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "journal.jsonl"), [
            hidden ? """{"type":"launched","hiddenScriptSha256":"abc"}""" : """{"type":"launched"}""",
            """{"type":"started","key":"k1","agentId":"a1","label":"one","phase":"P"}""",
            """{"type":"started","key":"k2","agentId":"a2","label":"two","phase":"P"}""",
            """{"type":"result","key":"k1","agentId":"a1","result":{"ok":true}}""",
        ]);
        File.WriteAllLines(Path.Combine(dir, "agent-a2.jsonl"), [Done, Parked429]);
        return Path.Combine(dir, "journal.jsonl");
    }

    static StoppedSession Window(bool atLimit, string root) =>
        new(1, "sid", @"C:\w", Path.Combine(root, "profile"), 3, Path.Combine(root, "sid.jsonl"), atLimit,
            Runs: [new WorkflowRun("sid", "wf_1", false, ["k2"], [])]);

    [Fact]
    public void AWindowRunningAWorkflowIsNeverMoved()
    {
        var root = Path.GetTempPath();
        ResumeStopped.Step Of(StoppedSession s) => ResumeStopped.Decide(s, TimeSpan.FromHours(1), TimeSpan.Zero, 4, false, 0, () => true);
        Assert.Equal(ResumeStopped.Step.Move, Of(Window(false, root) with { Runs = null })); // the same window without the run moves
        Assert.Equal(ResumeStopped.Step.Ignore, Of(Window(false, root)));
        Assert.Equal(ResumeStopped.Step.Move, Of(Window(true, root) with { Runs = null }));
        Assert.Equal(ResumeStopped.Step.Left, Of(Window(true, root))); // at its limit: reported, never closed
        Assert.Equal(ResumeStopped.Step.Left, Of(Window(true, root) with { Status = "shell" }));
        Assert.False(Window(false, root).Movable); // so `Unchanged`, the last look before any close, refuses it too
        Assert.False(ResumeStopped.Releasable([Window(false, root)], 1, 1)); // and a free never closes it
        Assert.Contains("w (workflow running)", ResumeStopped.Holders([Window(false, root)], 1, 1));
    }

    [Fact]
    public void RunningWorkflowsAreFoundInEverySessionFolderUntilTheySettle()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var project = Path.Combine(root, "projects", "C--w");
            var tasks = Path.Combine(root, "tmp");
            var since = DateTime.UtcNow.AddMinutes(-5);
            var journal = StageRun(project, "old-sid", "wf_a"); // a sibling session's folder: the run outlived a /clear
            var run = Assert.Single(ResumeStopped.RunningWorkflows(project, since, tasks));
            Assert.Equal(("old-sid", "wf_a", false), (run.SessionId, run.RunId, run.Hidden));
            Assert.Equal(["k2"], run.InFlight);
            Assert.Equal(["k2"], run.Parked);

            Assert.Empty(ResumeStopped.RunningWorkflows(project, DateTime.UtcNow.AddMinutes(5), tasks)); // written before the window started

            var outputs = Path.Combine(tasks, "old-sid", "tasks");
            Directory.CreateDirectory(outputs);
            File.WriteAllText(Path.Combine(outputs, "wabc.output"), ""); // launched, not settled
            Assert.Single(ResumeStopped.RunningWorkflows(project, since, tasks));
            File.WriteAllText(Path.Combine(outputs, "wold.output"), "an earlier run's result");
            File.SetLastWriteTimeUtc(Path.Combine(outputs, "wold.output"), File.GetLastWriteTimeUtc(journal).AddMinutes(-3));
            Assert.Single(ResumeStopped.RunningWorkflows(project, since, tasks)); // an earlier run's output settles nothing
            File.WriteAllText(Path.Combine(outputs, "wabc.output"), "the result");
            File.SetLastWriteTimeUtc(Path.Combine(outputs, "wabc.output"), File.GetLastWriteTimeUtc(journal).AddSeconds(1));
            Assert.Empty(ResumeStopped.RunningWorkflows(project, since, tasks)); // settled: its output written as it ended

            StageRun(project, "sid", "wf_b", hidden: true);
            Assert.True(Assert.Single(ResumeStopped.RunningWorkflows(project, since, tasks)).Hidden); // cannot resume elsewhere, still running
            Directory.CreateDirectory(Path.Combine(project, "sid", "workflows"));
            File.WriteAllText(Path.Combine(project, "sid", "workflows", "wf_b.json"), """{"status":"completed"}""");
            Assert.Empty(ResumeStopped.RunningWorkflows(project, since, tasks)); // settled: its snapshot exists
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ScanAttachesTheRunsOfTheWindowsProject()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Stage(root, "idle", [Typed, Done]);
            StageRun(Path.Combine(root, "projects", "C--w"), "other-sid", "wf_c");
            var s = Assert.Single(ScanOf(root, 60));
            Assert.Equal("wf_c", Assert.Single(s.Runs!).RunId);
            Assert.Equal(ResumeStopped.Step.Ignore, ResumeStopped.Decide(s, TimeSpan.FromHours(1), TimeSpan.Zero, 4, false, 0, () => true));
        }
        finally { Directory.Delete(root, true); }
    }
}

/// Read-only scan of this machine's real Claude windows; set PATCH_GOBLINS_LIVE_SCAN=1. Passes trivially otherwise.
public class ResumeStoppedLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void ScansRealWindows()
    {
        if (Environment.GetEnvironmentVariable("PATCH_GOBLINS_LIVE_SCAN") != "1") return;
        var dirs = ResumeStopped.ConfigDirs(null);
        foreach (var d in dirs) output.WriteLine($"dir {d.Account} {d.Dir}");
        foreach (var s in ResumeStopped.Scan(dirs, DateTime.UtcNow)) output.WriteLine($"stopped {s.Pid} {s.SessionId} {s.Cwd} #{s.Account}");
        // Every window record, found or not: all live windows should read as visible, with their transcript.
        foreach (var (dir, _) in dirs)
            foreach (var rec in Directory.Exists(Path.Combine(dir, "sessions")) ? Directory.GetFiles(Path.Combine(dir, "sessions"), "*.json") : [])
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(rec));
                    var r = doc.RootElement;
                    string? Get(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                    var pid = r.GetProperty("pid").GetInt32();
                    output.WriteLine($"record {pid} status={Get("status")} live={ResumeStopped.IsLiveClaude(pid, Get("procStart"))} " +
                        $"transcript={Get("sessionId") is { } id && Get("cwd") is { } cwd && ResumeStopped.TranscriptOf(dir, id, cwd) != null}");
                }
                catch (Exception e) when (e is IOException or System.Text.Json.JsonException or KeyNotFoundException) { }
            }
        foreach (var f in (Environment.GetEnvironmentVariable("PATCH_GOBLINS_LIVE_TRANSCRIPTS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            output.WriteLine($"atLimit={ResumeStopped.EndsAtLimit(File.ReadLines(f))} {Path.GetFileName(f)}");
    }
}
