using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PatchGoblins;

/// A Claude Code window waiting for someone to type: stopped on a usage-limit error (`AtLimit`), or done
/// with its turn. `Record` and `Seen` (the transcript's write time) let the move check nothing changed since.
/// `Limit` is the error as recorded; `OwnRefused`: the API refused the window's own account (not one it was
/// only labelled with after a switch). `Status` is the record's: only a `Movable` window is ever closed.
public sealed record StoppedSession(int Pid, string SessionId, string Cwd, string ConfigDir, int? Account, string Transcript,
    bool AtLimit = true, string? Record = null, DateTime Seen = default, Limit? Limit = null, bool OwnRefused = false, string Status = "idle",
    string? Model = null, IReadOnlyList<WorkflowRun>? Runs = null, string? Said = null, bool TurnOver = false)
{
    public bool Idle => Status == "idle";
    /// A workflow run is still going in the window's project folder: the window looks idle, but closing it would end the run.
    public bool Running => Runs is { Count: > 0 };
    /// Idle, or in a shell at its limit: its turn is over and it cannot carry on where it is. The close kills only claude,
    /// so the background command runs on to its end (the reopened window is told). A window running workflows keeps its record
    /// "busy" until they settle, so at its limit "busy" counts too when the main transcript says its turn is over (`TurnOver`:
    /// it ends at a finished reply or at its own limit error); only `Paused` then lets it move, and never before the limit.
    public bool Done => Idle || AtLimit && (Status == "shell" || Status == "busy" && Running && TurnOver);
    /// `Done` and no workflow running. (With MoveWorkflows on, a window running one moves only once `Paused`.)
    public bool Movable => Done && !Running;
}

/// A Claude Code workflow run that has not settled (`RunId` "wf_..." under session `SessionId`). `Hidden`: started from a hidden
/// script, which a new process cannot resume. `InFlight`: agent keys started without a result; `Parked`: those whose agent sits at a 429.
/// `Busy`: in-flight keys whose agent is mid-tool (its last message a tool call) or has a background command open. `Wrote`: the journal's
/// last write; `Script`: the run's script file; `Dir`: its run folder; `Mark`: its journal keys and in-flight agents' last messages, as one hash.
public sealed record WorkflowRun(string SessionId, string RunId, bool Hidden, IReadOnlyList<string> InFlight, IReadOnlyList<string> Parked,
    IReadOnlyList<string>? Busy = null, DateTime Wrote = default, string? Script = null, string? Dir = null, string? Mark = null);

/// A usage-limit stop as Claude Code recorded it: the window that ran out ("five_hour", "seven_day", ...), when it
/// resets, and when the stop happened. Default fields: an older error without the details.
public sealed record Limit(string? Window, DateTimeOffset? ResetsAt, DateTimeOffset At);

/// With auto-switch on: windows stopped at a limit are reopened (`claude --resume`) on an account with
/// room, the default login's own when it has some again, so their work carries on unattended. A `cswap run`
/// window whose account is at the switch threshold is moved the same way once its turn is done, before the
/// limit stops it. (A default-login window before its limit needs no move: the engine switches its account in place. One
/// already stopped does: its process keeps refusing until the reset, so it is reopened like the rest.)
/// Only an idle session, quiet a while, is touched: nothing that is working, or that someone has typed
/// into since, is ever stopped.
public static class ResumeStopped
{
    /// A reading at or above this is no room: the engine's own at-limit line.
    public const double Full = 99;
    static readonly TimeSpan Quiet = TimeSpan.FromSeconds(20);
    /// What a window already sent to an account this pass costs that account's room, so one pass spreads stopped windows.
    public const double PerWindow = 10;
    /// A window moved this often within an hour is left where it is: it keeps stopping, and reopening it again won't help.
    public const int MovesPerHour = 3;
    public const string Prompt = "A usage limit was hit. Continue.";

    /// Claude args that reopen `s` on another account: a stopped turn is told to carry on, a finished one
    /// just waits for its user again.
    /// A window paused with workflows running is told, per run, how to resume it; `configDir` is where it reopens (its copy of the script).
    public static string[] ResumeArgs(StoppedSession s, string? configDir = null) =>
        s.Running ? ["--resume", s.SessionId, string.Join(" ", new[] { s.AtLimit ? Prompt + (s.Status == "shell" ? Background : "") : null }.Concat(s.Runs!.Select(r =>
                $"Resume the paused workflow by calling Workflow with scriptPath {ScriptIn(s, r, configDir).Replace('\\', '/')} and resumeFromRunId {r.RunId}.")).OfType<string>())]
        : s.AtLimit ? ["--resume", s.SessionId, Prompt + (s.Status == "shell" ? Background : "")] : ["--resume", s.SessionId];

    /// A run's script as `CopyTranscript` puts it under `configDir` (`projects/<project>/<sid>/workflows/scripts/<name>`); null: where it is.
    /// ponytail: a quote or ';' in that path (a profile folder named so) would reach the prompt as is; none of Claude Code's own names have one.
    static string ScriptIn(StoppedSession s, WorkflowRun r, string? configDir) =>
        configDir == null ? r.Script! : Path.Combine(configDir, "projects", Path.GetFileName(Path.GetDirectoryName(s.Transcript)!), r.SessionId, "workflows", "scripts", Path.GetFileName(r.Script!));

    /// Quiet this long, with no agent in flight, a workflow is between steps: a move then repeats nothing (trigger A).
    public static readonly TimeSpan SafePoint = TimeSpan.FromSeconds(20);

    /// Whether a window's workflows can be stopped and resumed elsewhere now (MoveWorkflows). Every run must be this session's own (resume is
    /// same-session only), from a script on disk (a hidden one cannot resume in a new process), with no agent mid-tool or holding a background
    /// command (never cut off: waited on, however long). Then each run is either at a safe point (A: no agent in flight, journal quiet
    /// `SafePoint`) or held by the limit (B: every agent in flight parked at a 429).
    public static bool Paused(StoppedSession s, DateTime nowUtc) =>
        s.Runs is { Count: > 0 } runs && runs.All(r => Resumable(s, r) && r.Busy is not { Count: > 0 }
            && (r.InFlight.Count == 0 ? nowUtc - r.Wrote >= SafePoint : r.InFlight.All(r.Parked.Contains)));

    static bool Resumable(StoppedSession s, WorkflowRun r) => r.SessionId == s.SessionId && !r.Hidden && r.Script != null;

    /// Whether a workflow run is as it was read: same journal keys, same last message from each agent in flight, still no snapshot.
    static bool Same(WorkflowRun r) =>
        r.Dir != null && !File.Exists(Path.Combine(r.Dir, "..", "..", "..", "workflows", r.RunId + ".json")) && ReadRun(r.SessionId, r.Dir)?.Mark == r.Mark;

    /// The last user or assistant line: what a window said or was told last. Other lines (an api request, a notice) do not count.
    static string? LastMessage(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (Str(doc.RootElement, "type") is "user" or "assistant") return line;
            }
            catch (JsonException) { } // a line cut by the tail window, or one being written
        return null;
    }

    /// Said to a window moved while its background commands ran: they still do, so it doesn't start them twice.
    public const string Background = " Its background commands are still running.";

    /// The `autoswitch.model` names whose weekly windows the engine also decides on ("all": every one), read with its threshold.
    public static IReadOnlySet<string> Models { get; set; } = new HashSet<string>();

    /// The reading switches, frees and moves are judged on, as the engine's `relevant_windows`: the 5-hour and weekly windows and
    /// the scoped ones `Models` names. `Format.Peak` (every window) is for display only.
    public static double Peak(Account? a) =>
        a?.ShownUsage is { } u
            ? new[] { u.FiveHour?.Pct ?? 0, u.SevenDay?.Pct ?? 0 }
                .Concat((u.Scoped ?? []).Where(s => Models.Contains("all") || s.Name != null && Models.Contains(s.Name)).Select(s => s.Pct)).Max()
            : 0;

    /// The reading one window is judged on: the 5-hour and weekly windows, plus the scoped windows of the model it runs
    /// ("claude-fable-5-1" uses the "Fable" window as well as the account's general usage), and no other model's. A window
    /// on Fable moves when either runs out; one on another model never moves for Fable. Unknown model: `Peak`.
    public static double PeakFor(Account? a, string? model) =>
        model == null ? Peak(a)
        : a?.ShownUsage is { } u
            ? new[] { u.FiveHour?.Pct ?? 0, u.SevenDay?.Pct ?? 0 }.Concat((u.Scoped ?? []).Where(s => Uses(model, s)).Select(s => s.Pct)).Max()
            : 0;

    /// Whether a model id ("claude-fable-5-1") draws on a scoped window ("Fable"). An account without that window has no
    /// separate limit for the model (AccountPicker.ModelRoom): its general usage is all that binds there.
    public static bool Uses(string model, Window scoped) => scoped.Name is { Length: > 0 } n && model.Contains(n, StringComparison.OrdinalIgnoreCase);

    /// The model the conversation last ran: the newest assistant message's `model`. Claude Code's own error lines (a limit
    /// refusal among them) are `<synthetic>`, so the model before the stop is what counts. Null when the tail shows none.
    public static string? ModelOf(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (Str(doc.RootElement, "type") == "assistant" && doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
                    && Str(m, "model") is { Length: > 0 } model && !model.StartsWith('<'))
                    return model;
            }
            catch (JsonException) { } // a line cut by the tail window, or one being written
        }
        return null;
    }

    /// Which model's own window a refusal ran out ("Fable"), or null when it was the account's general usage. Claude Code
    /// labels a model's weekly refusal `seven_day`, as it does the general one: they are told apart by which window resets
    /// when the refusal says, and where both reset together, by which one reads full. Only a window the session's model uses.
    public static string? ScopeOf(Limit l, Account? a, string? model)
    {
        if (model == null || a?.ShownUsage is not { } u) return null;
        var mine = (u.Scoped ?? []).Where(w => Uses(model, w)).ToList();
        if (mine.FirstOrDefault(w => l.Window?.Contains(w.Name!, StringComparison.OrdinalIgnoreCase) == true) is { } named) return named.Name;
        if (l.Window == "five_hour") return null; // the 5-hour window is always the account's own; any other label is judged by the windows themselves
        bool At(Window? w) => w?.ResetsAt is { } r && l.ResetsAt is { } t && Math.Abs((r - t).TotalSeconds) <= 120;
        return mine.FirstOrDefault(w => At(w) && !At(u.SevenDay)
            || (At(w) || l.ResetsAt == null) && w.Pct >= Full && (u.SevenDay?.Pct ?? 0) < Full)?.Name;
    }

    /// `ModelOf` a transcript on disk; null when it cannot be read (gone, or locked).
    public static string? ModelOfFile(string path)
    {
        try { return ModelOf(Tail(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string DefaultDir => Path.Combine(StoreSecurity.UserProfile, ".claude");
    static string ProfilesDir => Path.Combine(StoreSecurity.BackupRoot, "sessions");

    /// Every Claude config dir with its account: the default login, then each `cswap run` profile (`N-email`).
    public static List<(string Dir, int? Account)> ConfigDirs(int? active)
    {
        var dirs = new List<(string, int?)> { (DefaultDir, active) };
        try
        {
            foreach (var d in Directory.EnumerateDirectories(ProfilesDir))
                if (int.TryParse(Path.GetFileName(d).Split('-')[0], out var n)) dirs.Add((d, n));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return dirs;
    }

    /// The default login's account and when the engine last wrote `sequence.json` (it does on every switch). Read
    /// errors: `(null, MaxValue)`, so nothing is attributed.
    public static (int? Account, DateTime ChangedAt) DefaultLogin()
    {
        try
        {
            var path = Path.Combine(StoreSecurity.BackupRoot, "sequence.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var n = doc.RootElement.TryGetProperty("activeAccountNumber", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var v) ? v : (int?)null; // null until the first switch
            return (n, File.GetLastWriteTimeUtc(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return (null, DateTime.MaxValue); }
    }

    /// A stop in a profile is always that profile's account; one in `~/.claude` belongs to today's default login
    /// only if it landed well after the last switch: a refusal just after one can still be the old login's (a
    /// request in flight, or one sent before Claude Code reread the login). Subtraction, as `defaultSince` can be MaxValue.
    public static bool OwnStop(bool defaultDir, DateTimeOffset at, DateTime defaultSince) => !defaultDir || at.UtcDateTime - defaultSince >= TimeSpan.FromSeconds(30);

    /// `includeBusy`: also windows stopped at a limit that are busy, in a shell or waiting (never moved: they are only told about).
    /// `agentLimits`: session id → a limit error seen in that session's subagent or workflow files.
    public static List<StoppedSession> Scan(IEnumerable<(string Dir, int? Account)> dirs, DateTime nowUtc, DateTime? defaultLoginChanged = null, bool includeBusy = false,
        IReadOnlyDictionary<string, Limit>? agentLimits = null)
    {
        var found = new List<StoppedSession>();
        var defaultSince = defaultLoginChanged ?? DefaultLogin().ChangedAt;
        var bridged = BridgeHosts();
        foreach (var (dir, account) in dirs)
        {
            string[] records;
            try { records = Directory.GetFiles(Path.Combine(dir, "sessions"), "*.json"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var rec in records)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(rec));
                    var r = doc.RootElement;
                    // Only idle: "shell" (idle with a background command running) would orphan that command if moved.
                    var status = Str(r, "status") ?? "";
                    if (status != "idle" && !includeBusy || Str(r, "kind") is { } k && k != "interactive" || Headless(r)) continue;
                    if (Str(r, "sessionId") is not { } id || Str(r, "cwd") is not { } cwd || !r.TryGetProperty("pid", out var pidEl)) continue;
                    var pid = pidEl.GetInt32();
                    // An agents terminal: reopened without its --mcp-config the agents would stop, so it is never closed or moved.
                    // With `includeBusy` it is always reported, as "agents" (never idle): stopped at a limit, so the owner is told to reopen it
                    // from the Agents menu; otherwise as what holds its profile, so a free names it instead of waiting for it to go idle.
                    if (bridged.Contains(pid)) { if (!includeBusy) continue; status = "agents"; }
                    var stamp = Str(r, "procStart");
                    if (!IsLiveClaude(pid, stamp)) continue;
                    var started = OperatingSystem.IsWindows() && long.TryParse(stamp, out var ft)
                        ? DateTime.FromFileTimeUtc(ft) : StartOf(pid)?.ToUniversalTime() ?? DateTime.MinValue;
                    if (TranscriptOf(dir, id, cwd) is not { } t)
                    {
                        if (status == "agents") found.Add(new StoppedSession(pid, id, cwd, dir, account, "", false, rec, Status: status));
                        continue;
                    }
                    var seen = File.GetLastWriteTimeUtc(t);
                    var tail = Tail(t).ToList();
                    var said = LastMessage(tail);
                    // A limit hit only in a subagent or workflow file stops the process too, but only while the main loop has written nothing since:
                    // once it has, its own transcript says whether it stopped (a refused follow-up is an error there), else it carried on after the switch.
                    var own = LimitOf(tail);
                    var limit = own ?? (agentLimits?.GetValueOrDefault(id) is { } agent && seen <= agent.At.UtcDateTime && (agent.ResetsAt is not { } until || until.UtcDateTime > nowUtc) ? agent : null);
                    // No quiet wait: the error is final, and `LimitOf` already looks past the notices Claude Code writes after it.
                    // This process's own stop only: a resumed (or tray-copied) transcript still ends at the error that stopped the window before it.
                    if (limit != null && (limit.At == default ? seen : limit.At.UtcDateTime) > started)
                        found.Add(new StoppedSession(pid, id, cwd, dir, account, t, true, rec, seen, limit, OwnStop(dir == DefaultDir, limit.At, defaultSince), status, ModelOf(tail),
                            RunningWorkflows(Path.GetDirectoryName(t)!, started), said, own != null || EndsTurn(tail)));
                    else if (status == "agents" || status == "idle" && nowUtc - seen >= Quiet && EndsTurn(tail))
                        found.Add(new StoppedSession(pid, id, cwd, dir, account, t, false, rec, seen, Status: status, Model: ModelOf(tail),
                            Runs: RunningWorkflows(Path.GetDirectoryName(t)!, started), Said: said));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
            }
        }
        return found;
    }

    /// The workflow runs still going in a project folder (`projects/<cwd>`), as of a window started at `sinceUtc`. Every session
    /// folder is looked at: after /clear or a fork a background run keeps writing under the old session id. A run counts when its
    /// journal was written since the window started (a run of a dead process is older) and it has no settlement snapshot
    /// (`<sid>/workflows/<run>.json`, written only as it settles). Task outputs (`w*.output`) are not used: they name no run.
    public static List<WorkflowRun> RunningWorkflows(string projectDir, DateTime sinceUtc)
    {
        var runs = new List<WorkflowRun>();
        try
        {
            foreach (var session in Directory.EnumerateDirectories(projectDir))
            {
                var root = Path.Combine(session, "subagents", "workflows");
                if (!Directory.Exists(root)) continue;
                var sid = Path.GetFileName(session);
                foreach (var run in Directory.EnumerateDirectories(root, "wf_*"))
                {
                    var journal = Path.Combine(run, "journal.jsonl");
                    if (!File.Exists(journal)) continue;
                    var wrote = File.GetLastWriteTimeUtc(journal);
                    var id = Path.GetFileName(run);
                    if (wrote <= sinceUtc || File.Exists(Path.Combine(session, "workflows", id + ".json"))) continue;
                    if (ReadRun(sid, run) is { } read) runs.Add(read);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return runs;
    }

    /// One run's journal: its keys in flight, which of them are parked or busy, and its `Mark`. Null when it cannot be read.
    static WorkflowRun? ReadRun(string sid, string run)
    {
        try
        {
            var journal = Path.Combine(run, "journal.jsonl");
            var hidden = false;
            // A resume appends to this journal (no "resumed" line): a killed agent's key is started again (new agent id). A key is done at its first result or failure.
            var open = new Dictionary<string, string>(); // started key -> latest agent id
            var done = new HashSet<string>();
            var keys = new StringBuilder();
            using (var reader = new StreamReader(new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                for (var line = reader.ReadLine(); line != null; line = reader.ReadLine())
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        switch (Str(r, "type"))
                        {
                            case "launched": hidden |= r.TryGetProperty("hiddenScriptSha256", out var h) && h.ValueKind == JsonValueKind.String; break;
                            case "started" when Str(r, "key") is { } k && !done.Contains(k): open[k] = Str(r, "agentId") ?? ""; keys.Append($"s {k} {open[k]}\n"); break;
                            case "result" or "failed" when Str(r, "key") is { } k: done.Add(k); open.Remove(k); keys.Append($"r {k}\n"); break;
                        }
                    }
                    catch (JsonException) { } // a line being written
            var id = Path.GetFileName(run);
            var agents = open.ToDictionary(a => a.Key, a => a.Value.Length > 0 ? AgentTail(Path.Combine(run, $"agent-{a.Value}.jsonl")) : []);
            foreach (var a in agents) keys.Append($"a {a.Key} {LastMessage(a.Value)}\n");
            var scripts = Path.Combine(run, "..", "..", "..", "workflows", "scripts");
            return new WorkflowRun(sid, id, hidden, [.. open.Keys], [.. agents.Where(a => Parked(a.Value)).Select(a => a.Key)],
                [.. agents.Where(a => MidTool(a.Value)).Select(a => a.Key)], File.GetLastWriteTimeUtc(journal),
                Directory.Exists(scripts) ? Directory.EnumerateFiles(scripts, $"*-{id}.js").Select(Path.GetFullPath).FirstOrDefault() : null, run,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(keys.ToString()))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// The end of a workflow agent's file; empty when it is not there yet or cannot be read.
    static List<string> AgentTail(string agentFile)
    {
        try { return [.. Tail(agentFile, 64 * 1024)]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// Whether an agent is mid-tool: its last message a tool call, or a background command it started (`run_in_background`) not yet
    /// reported done. ponytail: background commands are counted against task notifications in the tail, so one older than the tail is missed.
    static bool MidTool(List<string> lines)
    {
        var background = 0;
        foreach (var line in lines)
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (Str(r, "type") == "user" && IsNotice(r)) background--;
                else if (Str(r, "type") == "assistant" && r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
                    && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                    background += c.EnumerateArray().Count(b => b.ValueKind == JsonValueKind.Object && Str(b, "type") == "tool_use"
                        && b.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object && i.TryGetProperty("run_in_background", out var bg) && bg.ValueKind == JsonValueKind.True);
            }
            catch (JsonException) { }
        if (background > 0) return true;
        if (LastMessage(lines) is not { } last) return false;
        using var end = JsonDocument.Parse(last);
        var e = end.RootElement;
        return Str(e, "type") == "assistant" && !(e.TryGetProperty("isApiErrorMessage", out var err) && err.ValueKind == JsonValueKind.True) && !EndsTurn([last]);
    }

    /// Whether a workflow agent's file ends at Claude Code's own 429: parked at a usage limit until it resets.
    static bool Parked(List<string> lines)
    {
        try
        {
            if (lines.LastOrDefault() is not { } last) return false;
            using var doc = JsonDocument.Parse(last);
            var r = doc.RootElement;
            return Str(r, "type") == "assistant" && r.TryGetProperty("isApiErrorMessage", out var e) && e.ValueKind == JsonValueKind.True
                && r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object && Str(m, "model") == "<synthetic>"
                && (Str(r, "error") == "rate_limit" || r.TryGetProperty("apiErrorStatus", out var st) && st.ValueKind == JsonValueKind.Number && st.GetInt32() == 429);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException) { return false; }
    }

    /// Whether the conversation's last message is Claude Code's own usage-limit error.
    public static bool EndsAtLimit(IEnumerable<string> lines) => LimitOf(lines) != null;

    /// The usage-limit error the conversation ends with, or null. A look at the limit (/usage, /status) typed after it doesn't hide it; any other command does.
    public static Limit? LimitOf(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var type = Str(r, "type");
                if (type is not ("assistant" or "user") || type == "user" && (IsLook(r) || IsNotice(r))) continue;
                if (type == "user" || !r.TryGetProperty("isApiErrorMessage", out var err) || err.ValueKind != JsonValueKind.True) return null;
                JsonElement info = default;
                var refused = r.TryGetProperty("apiErrorParams", out var p) && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("rate_limit_info", out info) && info.ValueKind == JsonValueKind.Object;
                var text = r.TryGetProperty("message", out var m) ? m.ToString() : "";
                // "You've reached your Fable limit. Run /usage-credits…": a model's own limit, with no reset time; named for the model's window.
                var credits = Str(r, "apiError") == "model_requires_usage_credits";
                if (credits) return new Limit(Regex.Match(text, @"reached your (\w+) limit") is { Success: true } name ? name.Groups[1].Value : "model", null,
                    r.TryGetProperty("timestamp", out var cts) && cts.ValueKind == JsonValueKind.String && cts.TryGetDateTimeOffset(out var ct) ? ct : default);
                if (Str(r, "apiError") != "usage_limit_reached" && !(refused && Str(info, "status") == "rejected")
                    && !(text.Contains("limit", StringComparison.OrdinalIgnoreCase)
                        && (text.Contains("hit your", StringComparison.OrdinalIgnoreCase) || text.Contains("usage", StringComparison.OrdinalIgnoreCase)))) return null;
                var resets = refused && info.TryGetProperty("resetsAt", out var ra) && ra.ValueKind == JsonValueKind.Number && ra.TryGetInt64(out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix) : (DateTimeOffset?)null;
                var at = r.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String && ts.TryGetDateTimeOffset(out var t) ? t : default;
                return new Limit(refused ? Str(info, "rateLimitType") : null, resets, at);
            }
            catch (Exception e) when (e is JsonException or ArgumentOutOfRangeException) { } // a line cut by the tail window, or one being written
        }
        return null;
    }

    /// Whether the conversation's last message is Claude's own finished reply: no tool call left open, no
    /// error, nobody's message after it. A turn still running, or one cut off, never matches.
    public static bool EndsTurn(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var type = Str(r, "type");
                if (type is not ("assistant" or "user") || type == "user" && IsLocal(r)) continue;
                if (type == "user" || r.TryGetProperty("isApiErrorMessage", out var err) && err.ValueKind == JsonValueKind.True) return false;
                if (!r.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return false;
                if (Str(m, "stop_reason") is "tool_use" or "max_tokens" or "pause_turn") return false;
                return !m.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array
                    || !c.EnumerateArray().Any(b => b.ValueKind == JsonValueKind.Object && Str(b, "type") is "tool_use" or "server_tool_use");
            }
            catch (JsonException) { } // a line cut by the tail window, or one being written
        }
        return false;
    }

    /// A user line Claude never answers: a local slash command (/usage, /model…), its output, or a /compact summary.
    static bool IsLocal(JsonElement r) =>
        r.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True
        || r.TryGetProperty("isCompactSummary", out var cs) && cs.ValueKind == JsonValueKind.True
        || r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object && Str(m, "content") is { } text
           && (text.StartsWith("<command-name>") || text.StartsWith("<local-command-"));

    /// A user line nobody typed: a background task's notification. Other origins (auto-continue, a suggestion, a peer) answer the user's own input, so they count as it.
    static bool IsNotice(JsonElement r) =>
        r.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.Object && Str(o, "kind") == "task-notification";

    /// A user line that only looks at the limit: a /usage or /status command, or local command output. Nothing else (/model, /compact) is.
    static bool IsLook(JsonElement r) =>
        r.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True
        || r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object && Str(m, "content") is { } text
           && (text.StartsWith("<command-name>/usage<") || text.StartsWith("<command-name>/status<") || text.StartsWith("<local-command-"));

    /// How long since the last keyboard or mouse input anywhere in this session. A finished turn is only
    /// moved after a quiet spell, so a prompt someone is still typing is never closed under them.
    public static TimeSpan UserIdle()
    {
        if (OperatingSystem.IsMacOS()) return TimeSpan.FromSeconds(CGEventSourceSecondsSinceLastEventType(0, uint.MaxValue));
        if (!OperatingSystem.IsWindows()) return TimeSpan.Zero;
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
    }

    /// The account to reopen on. Stopped at a limit: the session's own once it has room again, else the one
    /// with the most room (not excluded, usage readable) whose profile exists, or the default login.
    /// Done with a turn: only when its own account reads at or above `threshold`, onto one below it by at
    /// least `hysteresis` points, so two accounts near the line never trade a window back and forth.
    /// Null: stay put (nowhere has room yet, or no move is due).
    /// An account the API refused the window on stays out until its reset, whatever a stale reading says; `load`
    /// (windows already sent to each account this pass) lowers an account's room, so one pass spreads them. `refusedUntil`
    /// (any window's refusal the tray saw, account → reset) keeps an account out the same way for every window. `live`: accounts
    /// other `cswap run` windows hold, which the default login cannot switch to either.
    public static int? Target(StoppedSession s, IReadOnlyList<Account> accounts, Func<int, bool> hasProfile,
        double threshold = Full, double hysteresis = 0, DateTimeOffset? now = null, IReadOnlyDictionary<int, int>? load = null,
        IReadOnlyDictionary<int, DateTimeOffset>? refusedUntil = null, ISet<int>? live = null,
        IReadOnlyDictionary<(int Account, string Scope), DateTimeOffset>? scopedRefused = null, Func<Account, double>? line = null, bool followsLogins = true)
    {
        // The automatic line (SwitchLine) is per account: this window's own account moves at its line, a target must be under its own.
        double Line(Account a) => line?.Invoke(a) ?? threshold;
        // Judged on what this window spends: a Fable window on its general usage and its Fable window, on every account.
        double Peak(Account a) => PeakFor(a, s.Model);
        double Weight(Account a) => Peak(a) + PerWindow * (load?.GetValueOrDefault(a.Number) ?? 0);
        bool Read(Account a) => !a.Disabled && a.UsageStatus == "ok" && a.Usage != null;
        // A model's own refusal ("Fable" out on #2) keeps only windows on that model off the account; the rest may still go there.
        bool ModelRefused(Account a) => s.Model is { } m && scopedRefused != null
            && scopedRefused.Any(r => r.Key.Account == a.Number && m.Contains(r.Key.Scope, StringComparison.OrdinalIgnoreCase) && r.Value > (now ?? DateTimeOffset.UtcNow));
        bool Room(Account a) => Read(a) && Peak(a) < Full && !(refusedUntil?.GetValueOrDefault(a.Number) > (now ?? DateTimeOffset.UtcNow)) && !ModelRefused(a);
        var own = accounts.FirstOrDefault(a => a.Number == s.Account);
        // A model's own refusal without a reset time ("Fable", from usage credits) holds until the reading says otherwise: never reopened where it was refused.
        var refused = s.OwnRefused && (s.Limit?.ResetsAt > (now ?? DateTimeOffset.UtcNow) || s.Limit is { ResetsAt: null, Window: not (null or "five_hour" or "seven_day") });
        if (s.AtLimit && own != null && Room(own) && !refused) return own.Number;
        if (!s.AtLimit && (own == null || !Read(own) || Peak(own) < Line(own))) return null;
        // A move before the limit leaves alone the account the default login, near its own line, would switch to: a window moved
        // there makes it a live profile, which the engine never switches onto. (A stopped window cannot wait, so it may take it.)
        var login = accounts.FirstOrDefault(a => a.Active);
        var kept = !s.AtLimit && login != null && login.Number != s.Account && Read(login) && ResumeStopped.Peak(login) >= threshold - hysteresis
            ? SwitchTargets(accounts, login.Number, live ?? new HashSet<int>(), refusedUntil ?? new Dictionary<int, DateTimeOffset>(), now ?? DateTimeOffset.UtcNow, threshold, followsLogins)
                .Select(n => (int?)n).FirstOrDefault()
            : null;
        return accounts.Where(a => a.Number != s.Account && a.Number != kept && Room(a) && (a.Active || hasProfile(a.Number)))
            .Where(a => s.AtLimit || Peak(a) < Line(a) && Peak(own!) - Peak(a) >= hysteresis)
            .OrderBy(Weight).ThenByDescending(a => a.Active)
            .Select(a => (int?)a.Number).FirstOrDefault();
    }

    /// The account a usage-limit error is charged to: a profile's own; in `~/.claude`, the one whose window resets when the
    /// error says (a refusal just after an in-place switch is the previous login's), else the default login when the
    /// stop landed well after its last switch (`OwnStop`). Null: unknown, so nothing is imported or switched on it.
    /// Resets that fall together (Auto-Prime) leave the match ambiguous: then it is the login's, once `OwnStop` trusts it; sooner, nobody's.
    public static int? Refuser(Limit l, int? profile, IReadOnlyList<Account> accounts, (int? Account, DateTime ChangedAt) login)
    {
        if (profile != null) return profile;
        var match = accounts.Where(a => a.ShownUsage is { } u && l.ResetsAt is { } t
            && Windows(u).Any(w => w.ResetsAt is { } r && Math.Abs((r - t).TotalSeconds) <= 120)).Select(a => a.Number).ToList();
        if (match.Count == 1) return match[0];
        return login.Account is { } n && OwnStop(true, l.At, login.ChangedAt) ? n : null;
    }

    static IEnumerable<Window> Windows(Usage u) => new[] { u.FiveHour, u.SevenDay }.Concat(u.Scoped ?? []).OfType<Window>();

    /// A profile dir's account (`N-email`); null for `~/.claude`, whose account is whichever login is live.
    public static int? ProfileAccount(string dir) =>
        dir != DefaultDir && int.TryParse(Path.GetFileName(dir.TrimEnd('\\', '/')).Split('-')[0], out var n) ? n : null;

    /// Where the default login can move when `refused` ran out, best (most room) first. Usage read and below full; enabled;
    /// not refused until a reset still ahead, whatever a reading older than the refusal says. An account a `cswap run` window
    /// holds (`liveProfiles`) is only tried after the others (two live copies of one rotating login can strand one), never
    /// left out: a window at its limit moves to any account with room. The engine refuses it if the profile's login is ahead.
    /// Below the engine's `threshold` first (above it the engine would switch again at once); only if none, anything below full.
    /// `followsLogins` false (macOS: the engine cannot keep the two copies of a login in sync and refuses a held account): held accounts are left out.
    public static List<int> SwitchTargets(IReadOnlyList<Account> accounts, int refused, ISet<int> liveProfiles,
        IReadOnlyDictionary<int, DateTimeOffset> refusedUntil, DateTimeOffset now, double threshold = Full, bool followsLogins = true)
    {
        var open = accounts.Where(a => a.Number != refused && !a.Disabled && a.UsageStatus == "ok" && a.Usage != null && Peak(a) < Full
                && !(refusedUntil.TryGetValue(a.Number, out var until) && until > now)
                && (followsLogins || !liveProfiles.Contains(a.Number))).ToList();
        return [.. (open.Any(a => Peak(a) < threshold) ? open.Where(a => Peak(a) < threshold) : open).OrderBy(a => liveProfiles.Contains(a.Number)).ThenBy(a => Peak(a)).Select(a => a.Number)];
    }

    /// When the first account frees up: the soonest reset ahead among the accounts refused and the windows read as full.
    public static DateTimeOffset? EarliestReset(IReadOnlyList<Account> accounts, IReadOnlyDictionary<int, DateTimeOffset> refusedUntil, DateTimeOffset now) =>
        refusedUntil.Values.Concat(accounts.Where(a => a.ShownUsage != null).SelectMany(a => Windows(a.ShownUsage!).Where(w => w.Pct >= Full).Select(w => w.ResetsAt ?? default)))
            .Where(t => t > now).Select(t => (DateTimeOffset?)t).Min();

    /// The `cswap run` account to free for the default login, or null. Due when the default login is at the engine's
    /// `threshold` (or refused) and every account with room below it is held by a live profile: the engine never puts the
    /// default login on a second copy of a rotating login, so it would sit at its limit while that account had room. Freed by
    /// closing the profile's idle windows and reopening them on the default login once it switched there: one copy at a time.
    /// Only an account `hysteresis` below the threshold, so the engine stays there and nothing trades back.
    public static int? FreeTarget(IReadOnlyList<Account> accounts, ISet<int> liveProfiles, IReadOnlyDictionary<int, DateTimeOffset> refusedUntil,
        DateTimeOffset now, double threshold, double hysteresis, bool followsLogins = true) =>
        FreeTargets(accounts, liveProfiles, refusedUntil, now, threshold, hysteresis, followsLogins).Select(n => (int?)n).FirstOrDefault();

    /// Every account `FreeTarget` could free, most room first: one a working window holds must not hide the next.
    public static List<int> FreeTargets(IReadOnlyList<Account> accounts, ISet<int> liveProfiles, IReadOnlyDictionary<int, DateTimeOffset> refusedUntil,
        DateTimeOffset now, double threshold, double hysteresis, bool followsLogins = true)
    {
        if (accounts.FirstOrDefault(a => a.Active) is not { } d) return [];
        var refused = refusedUntil.TryGetValue(d.Number, out var until) && until > now;
        if (!refused && !(d.UsageStatus == "ok" && d.Usage != null && Peak(d) >= threshold)) return [];
        bool Below(int n, double line) => Peak(accounts.First(a => a.Number == n)) < line;
        if (SwitchTargets(accounts, d.Number, liveProfiles, refusedUntil, now, threshold, followsLogins).Any(n => Below(n, threshold))) return []; // the engine has somewhere to go
        return [.. SwitchTargets(accounts, d.Number, new HashSet<int>(), refusedUntil, now, threshold)
            .Where(n => liveProfiles.Contains(n) && Below(n, threshold - hysteresis))];
    }

    /// Whether every window on a profile can be closed now: each live claude in it (`records`) was seen idle (`windows`, from
    /// `Scan`), and no wrapper runs without a window (`runs` above `records`: a headless run, an agent). One busy, shell,
    /// waiting or unread window keeps the whole profile, so nothing working is closed and the account is never half freed.
    public static bool Releasable(IReadOnlyCollection<StoppedSession> windows, int records, int runs) =>
        records > 0 && windows.Count == records && runs <= records && windows.All(w => w.Idle && !w.Running);

    /// Whether only agent runs keep a profile from being `Releasable`: every window in it idle, and each run without a window
    /// (`runs` above `records`) one of the `agentRuns` the agents have on that account, so they can yield it.
    public static bool AgentsOnly(IReadOnlyCollection<StoppedSession> windows, int records, int runs, int agentRuns) =>
        windows.Count == records && windows.All(w => w.Idle && !w.Running) && runs > records && agentRuns >= runs - records;

    /// What keeps a profile that is not `Releasable`, for the user: each window not idle by folder and status, a window the
    /// scan did not see as done, a run without a window (an agent's, or a headless `cswap run`).
    public static List<string> Holders(IReadOnlyCollection<StoppedSession> windows, int records, int runs)
    {
        var held = windows.Where(w => !w.Idle || w.Running).Select(w => w.Status == "agents" ? $"the agents terminal in {Path.GetFileName(w.Cwd.TrimEnd('\\'))}"
            : $"{Path.GetFileName(w.Cwd.TrimEnd('\\'))} ({(w.Running ? "workflow running" : w.Status)})").ToList();
        if (records > windows.Count) held.Add(records - windows.Count == 1 ? "a window still working" : $"{records - windows.Count} windows still working");
        if (runs > records) held.Add(runs - records == 1 ? "an agent or headless run" : $"{runs - records} agent or headless runs");
        return held;
    }

    /// Live claude records and live `cswap run` wrapper markers in a profile dir. Throws when it cannot be listed.
    /// A live claude with only a `{pid}.<hash>.key` counts as a record, as in the engine: one started after the key was
    /// written holds a recycled pid. Claude Code (2.1.30x) writes the key 0.4-4 s before the record at every start, and
    /// writes no record at all for a session that is not persisted, an agent-team member or a parked spare, or when
    /// registering fails. With no record its status is unknown (a turn may be running), so `Scan` never sees it as done
    /// and it holds its profile and is never closed; its id on the command line would not change that (`--fork-session`
    /// and `/resume` make it wrong anyway).
    public static (int Records, int Runs) LiveCounts(string dir)
    {
        var runs = Path.Combine(dir, ".cswap-runs");
        var records = Path.Combine(dir, "sessions");
        var found = Directory.Exists(records) ? Directory.EnumerateFiles(records, "*.json").Select(LivePid).OfType<(int Pid, bool Headless)>().ToList() : [];
        var live = found.Select(r => r.Pid).ToHashSet(); // a headless run's key is its own too, never a window's
        var count = found.Count(r => !r.Headless); // a `claude -p` run (an agent's) is a run, counted by its wrapper's marker
        if (Directory.Exists(records))
            count += Directory.EnumerateFiles(records, "*.key").Count(f => int.TryParse(Path.GetFileName(f).Split('.')[0], out var pid) && live.Add(pid)
                && IsRunning(pid) && !(StartOf(pid)?.ToUniversalTime() > File.GetLastWriteTimeUtc(f).AddSeconds(2)));
        return (count, Directory.Exists(runs) ? Directory.EnumerateFiles(runs).Count(MarkerLive) : 0);
    }

    /// Conservative preflight before stopping owned agents to free an account.
    /// The engine performs the authoritative check under its mutation lock.
    /// Here unknown records, missing directories and inaccessible processes all
    /// count as busy, so a failed switch does not needlessly interrupt agents.
    internal static bool DefaultProfileClear(string dir)
    {
        static bool RunningOrUnknown(int pid)
        {
            if (pid <= 1) return true;
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
            catch (ArgumentException) { return false; } // no such process
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
        }

        try
        {
            var sessions = Path.Combine(dir, "sessions");
            if ((File.GetAttributes(sessions) & FileAttributes.Directory) == 0) return false;
            foreach (var file in Directory.EnumerateFiles(sessions))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (!doc.RootElement.TryGetProperty("pid", out var value) || !value.TryGetInt32(out var pid) || RunningOrUnknown(pid)) return false;
                }
                else if (ext.Equals(".key", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(Path.GetFileName(file).Split('.')[0], out var pid) || RunningOrUnknown(pid)) return false;
                }
            }
            var runs = Path.Combine(dir, ".cswap-runs");
            try
            {
                if ((File.GetAttributes(runs) & FileAttributes.Directory) == 0 || Directory.EnumerateFiles(runs).Any(MarkerLive)) return false;
            }
            catch (FileNotFoundException) { } // a default profile may never have had a run wrapper
            catch (DirectoryNotFoundException) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return false; }
    }

    /// A wrapper's run marker names a live process: its pid runs and, when the marker holds the wrapper's creation stamp (a
    /// FILETIME), it is that process and not a recycled pid, as the engine's `_marker_is_live` checks.
    static bool MarkerLive(string marker)
    {
        if (!int.TryParse(Path.GetFileName(marker), out var pid) || !IsRunning(pid)) return false;
        try { if (!long.TryParse(File.ReadAllText(marker).Trim(), out var ft)) return true; return StartOf(pid) is not { } t || Math.Abs(t.ToUniversalTime().ToFileTimeUtc() - ft) < TimeSpan.TicksPerSecond * 2; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// A record of a headless claude (`claude -p`, the SDK): never a window to close, move or wait on.
    static bool Headless(JsonElement r) => Str(r, "entrypoint") is "sdk-cli" or "sdk-ts" or "sdk-py";

    /// Accounts with a `cswap run` window right now: a live claude in the profile, or its wrapper's run marker (a headless
    /// run writes no record). Not knowing (unlistable dir) counts as in use.
    public static HashSet<int> LiveProfiles(IEnumerable<(string Dir, int? Account)> dirs)
    {
        var live = new HashSet<int>();
        foreach (var (dir, account) in dirs)
        {
            if (account is not { } n || dir == DefaultDir) continue;
            try
            {
                if (LiveCounts(dir) is not (0, 0)) live.Add(n);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { live.Add(n); }
        }
        return live;
    }

    static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    static (int, bool)? LivePid(string rec)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(rec));
            return doc.RootElement.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var id) && IsLiveClaude(id, Str(doc.RootElement, "procStart")) ? (id, Headless(doc.RootElement)) : null;
        }
        catch (JsonException) { return null; } // half-written: the next look sees it
    }

    /// The transcripts of live windows, for the poll: each main transcript, and the subagent and workflow-agent files of its
    /// session written lately. ponytail: a walk of the session's folder per poll; a watcher event is the fast path anyway.
    public static List<(string File, string Dir)> LiveTranscripts(IEnumerable<(string Dir, int? Account)> dirs, DateTime nowUtc)
    {
        var found = new List<(string, string)>();
        foreach (var (dir, _) in dirs)
        {
            string[] records;
            try { records = Directory.GetFiles(Path.Combine(dir, "sessions"), "*.json"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var rec in records)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(rec));
                    var r = doc.RootElement;
                    if (Str(r, "sessionId") is not { } id || Str(r, "cwd") is not { } cwd || !r.TryGetProperty("pid", out var pid) || !IsLiveClaude(pid.GetInt32(), Str(r, "procStart"))) continue;
                    if (TranscriptOf(dir, id, cwd) is not { } t) continue;
                    found.Add((t, dir));
                    var extra = Path.Combine(Path.GetDirectoryName(t)!, id);
                    if (Directory.Exists(extra))
                        found.AddRange(Directory.EnumerateFiles(extra, "*.jsonl", SearchOption.AllDirectories)
                            .Where(f => nowUtc - File.GetLastWriteTimeUtc(f) < TimeSpan.FromMinutes(2)).Select(f => (f, dir)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
            }
        }
        return found;
    }

    /// What one scanned window gets in a pass. `Ignore`: nothing due. `Left`: busy at its limit, never closed (its turn
    /// is still running): told about once, moved when idle. `Wait`: look again on the next poll (waiting on a dialog, someone typing,
    /// or it changed since the scan). `NoRoom`, `Recent` (reopened there under ten minutes ago), `Capped` (`MovesPerHour`): left as it is.
    /// `Move`: close and reopen it on `target`.
    public enum Step { Ignore, Left, Wait, NoRoom, Recent, Capped, Move }

    /// The per-window decision, in the order that keeps a window safe. `unchanged` is the last look before a close, so it runs only then.
    /// A finished turn moves only off the default login, and only after a minute without input anywhere.
    /// `moveWorkflows` (the owner's opt-in): a `cswap run` window running workflows moves once they are `Paused`, and waits until then.
    public static Step Decide(StoppedSession s, TimeSpan userIdle, TimeSpan typingPause, int? target, bool recent, int moves, Func<bool> unchanged,
        bool moveWorkflows = false, DateTime nowUtc = default)
    {
        // A workflow still running in the window would die with it: never moved; at a limit, the owner is told it stays.
        // With MoveWorkflows on, a `cswap run` window whose runs can resume elsewhere waits for them instead.
        if (s.Running && !(moveWorkflows && s.Done && s.ConfigDir != DefaultDir && s.Runs!.All(r => Resumable(s, r)))) return s.AtLimit ? Step.Left : Step.Ignore;
        if (s.Running && !Paused(s, nowUtc)) return Step.Wait; // an agent at work: never cut off, looked at again on the next poll
        if (!s.Movable && !s.Running) return !s.AtLimit ? Step.Ignore : s.Status == "waiting" ? Step.Wait : Step.Left;
        if (!s.AtLimit && (s.ConfigDir == DefaultDir || userIdle < TimeSpan.FromSeconds(60))) return Step.Ignore;
        if (s.AtLimit && userIdle < typingPause) return Step.Wait;
        if (target == null) return Step.NoRoom;
        if (recent) return Step.Recent;
        if (moves >= MovesPerHour) return Step.Capped;
        return unchanged() ? Step.Move : Step.Wait;
    }

    /// The move guard's record key for one move of a session (`session#@ticks`): unlike `session#account` it is never overwritten.
    public static string MoveStamp(string sessionId, DateTime nowUtc) => $"{sessionId}#@{nowUtc.Ticks}";

    /// How often a session was reopened in the last hour, from the move guard's `MoveStamp` entries.
    public static int MovesInLastHour(IReadOnlyDictionary<string, DateTime> moves, string sessionId, DateTime nowUtc) =>
        moves.Count(m => m.Key.StartsWith(sessionId + "#@", StringComparison.Ordinal) && nowUtc - m.Value < TimeSpan.FromHours(1));

    /// The session a subagent or workflow file belongs to (`projects/<cwd>/<session>/...`); null for a main transcript.
    public static string? AgentSession(string configDir, string file)
    {
        var parts = Path.GetRelativePath(Path.Combine(configDir, "projects"), file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length >= 3 && parts[0] != ".." ? parts[1] : null;
    }

    /// Whether `s` is exactly as the scan saw it: still idle, transcript untouched. Checked right before the
    /// window is closed, so a turn started (or a message sent) since the scan is never cut off, nor a recycled pid hit. A stop at a limit
    /// holds while the error is still the last message: notices Claude Code writes after it don't cancel the move.
    /// `workflows`: the move of a window running workflows (MoveWorkflows), judged on what was said and the runs' journals, not write times
    /// (a live window writes request lines to its transcript all along). Without it, such a window is never unchanged: it is not `Movable`.
    public static bool Unchanged(StoppedSession s, bool workflows = false)
    {
        try
        {
            if (workflows && s.Running) { if (LastMessage(Tail(s.Transcript)) != s.Said || !s.Runs!.All(Same)) return false; }
            // A limit seen only in an agent file leaves the main transcript without one: it must just be as quiet as at the scan.
            else if (s.AtLimit && s.Limit is { } limit && LimitOf(Tail(s.Transcript)) is { } now ? now.At != limit.At : File.GetLastWriteTimeUtc(s.Transcript) != s.Seen) return false;
            if (s.Record == null) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(s.Record));
            var r = doc.RootElement;
            return (workflows && s.Running ? s.Done : s.Movable) && Str(r, "status") == s.Status && Str(r, "sessionId") == s.SessionId && IsLiveClaude(s.Pid, Str(r, "procStart"), kill: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// The `cswap import-usage -` document that hands the engine the limits the API just gave these windows (one
    /// row per account, 100% on the window that ran out), or null. `done` holds `account:reset` keys already sent.
    /// Only a refused own account with a known future reset on the 5-hour or 7-day window counts. The engine keeps
    /// a newer real reading over this one; `spend` is not carried (display only, back with the next real fetch).
    public static string? LimitReport(IEnumerable<StoppedSession> stops, IReadOnlyList<Account> accounts, DateTimeOffset now, ISet<string> done)
    {
        var rows = new List<object>();
        foreach (var s in stops)
        {
            if (!s.OwnRefused || s.Limit is not { ResetsAt: { } reset } l || reset <= now) continue;
            if (accounts.FirstOrDefault(a => a.Number == s.Account) is not { } a) continue;
            // A model's own window that ran out is what reads full, never the account's general weekly window it shares a label with.
            var scope = ScopeOf(l, a, s.Model);
            if (scope == null && l.Window is not ("five_hour" or "seven_day") || !done.Add($"{a.Number}:{scope}:{reset.ToUnixTimeSeconds()}")) continue;
            var u = a.ShownUsage;
            var full = new Window(100, reset);
            if (scope != null)
            {
                rows.Add(new
                {
                    email = a.Email,
                    organizationUuid = a.OrganizationUuid ?? "",
                    usageStatus = "ok",
                    usageAgeSeconds = Math.Max(0, (now - l.At).TotalSeconds),
                    usage = new { fiveHour = u?.FiveHour, sevenDay = u?.SevenDay, scoped = (u?.Scoped ?? []).Select(w => w.Name == scope ? full with { Name = w.Name } : w).ToList() },
                });
                continue;
            }
            rows.Add(new
            {
                email = a.Email,
                organizationUuid = a.OrganizationUuid ?? "",
                usageStatus = "ok",
                usageAgeSeconds = Math.Max(0, (now - l.At).TotalSeconds),
                usage = new { fiveHour = l.Window == "five_hour" ? full : u?.FiveHour, sevenDay = l.Window == "seven_day" ? full : u?.SevenDay, scoped = u?.Scoped },
            });
        }
        return rows.Count == 0 ? null
            : JsonSerializer.Serialize(new { schemaVersion = 1, accounts = rows }, LimitJson);
    }

    static readonly JsonSerializerOptions LimitJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, // usage_from_json rejects a null window
    };

    /// When each session was last reopened, kept across tray restarts so a restart never re-arms a move.
    public static Dictionary<string, DateTime> LoadMoves(string path)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path)) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    /// Saves `moves`, dropping entries a day old: by then no guard needs them.
    public static void SaveMoves(string path, Dictionary<string, DateTime> moves, DateTime nowUtc)
    {
        foreach (var k in moves.Where(m => nowUtc - m.Value > TimeSpan.FromDays(1)).Select(m => m.Key).ToList()) moves.Remove(k);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(moves));
    }

    public static string? ProfileDir(int account)
    {
        try { return Directory.EnumerateDirectories(ProfilesDir, $"{account}-*").FirstOrDefault(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// Copies the transcript (and its subagents/tool-results folder), file checkpoints and todos into
    /// `targetDir`, where `--resume` finds them.
    public static void CopyTranscript(StoppedSession s, string targetDir)
    {
        if (Path.GetFullPath(targetDir).TrimEnd('\\').Equals(Path.GetFullPath(s.ConfigDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        var project = Path.GetFileName(Path.GetDirectoryName(s.Transcript)!);
        var dest = Path.Combine(targetDir, "projects", project);
        Directory.CreateDirectory(dest);
        File.Copy(s.Transcript, Path.Combine(dest, Path.GetFileName(s.Transcript)), overwrite: true);
        var extra = Path.Combine(Path.GetDirectoryName(s.Transcript)!, s.SessionId);
        if (Directory.Exists(extra)) CopyDir(extra, Path.Combine(dest, s.SessionId));
        var history = Path.Combine(s.ConfigDir, "file-history", s.SessionId);
        if (Directory.Exists(history)) CopyDir(history, Path.Combine(targetDir, "file-history", s.SessionId));
        var todos = Path.Combine(s.ConfigDir, "todos");
        if (Directory.Exists(todos))
            foreach (var f in Directory.GetFiles(todos, s.SessionId + "*.json"))
            {
                Directory.CreateDirectory(Path.Combine(targetDir, "todos"));
                File.Copy(f, Path.Combine(targetDir, "todos", Path.GetFileName(f)), overwrite: true);
            }
    }

    /// Ends the stopped claude and returns; a `cswap run` wrapper around it is then left to exit on its own (it
    /// seals the profile's login) and the shell that held it is closed, so the window it sat in goes away. The
    /// new `cswap run` need not wait for that: it registers its run marker first, which a peer's seal respects.
    /// `record`, the killed claude's session record, is deleted once it exited: killed, Claude Code never removes it, and a
    /// pid reused later would make it read as a live window. True once claude has exited; false when it is still there after 10 s.
    public static async Task<bool> CloseAsync(int pid, string? record = null)
    {
        var chain = new List<(int Pid, string Name, DateTime Start)>();
        for (var (p, guard) = (pid, 0); guard < 4 && ParentOf(p) is { } parent; p = parent, guard++)
        {
            var name = NameOf(parent);
            if (name is not ("cswap" or "cmd" or "powershell" or "pwsh")) break;
            // Only the console the tray opened (`pwsh -Command & '<engine>' 'run' …`, or the cmd fallback): a shell the user started holds their own script or state.
            if (name != "cswap" && !TrayConsole(CommandLineOf(parent))) break;
            // A real parent started before its child: a pid the system has since given to a newer process is not one.
            if (StartOf(p) is not { } child || StartOf(parent) is not { } start || start > child) break;
            chain.Add((parent, name, start));
        }
        using (var claude = Process.GetProcessById(pid))
        {
            claude.Kill();
            try { await claude.WaitForExitAsync(new CancellationTokenSource(10_000).Token); }
            catch (OperationCanceledException) { } // slow to go: a window without workflows is still reopened
            if (!claude.HasExited) return false; // its shells stay: the window is still there
            if (record != null)
                try { File.Delete(record); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        _ = CloseShellsAsync(chain).ContinueWith(t => Program.Log(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        return true;
    }

    /// Whether a shell's command line is one `Engine.LaunchConsole` started: `pwsh … -Command & '<…\cswap[.exe]>' 'run' …`
    /// (or powershell; conhost adds a title first), or the cmd fallback, `cmd /k <…\cswap[.exe]> run …` (Windows Terminal)
    /// and `cmd /k "title … && <…\cswap[.exe]> run …"` (conhost).
    internal static bool TrayConsole(string? commandLine) =>
        commandLine != null && Regex.IsMatch(commandLine,
            @"^""?[^""]*\b(cmd(\.exe)?""?\s+/k|(pwsh|powershell)(\.exe)?""?\s.*-Command)\s.*\bcswap(\.exe)?['""]?\s+'?run\b", RegexOptions.IgnoreCase);

    /// A process's command line, null when it cannot be read.
    static string? CommandLineOf(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                using var query = Process.Start(new ProcessStartInfo("/bin/ps", ["-p", pid.ToString(), "-o", "command="]) { UseShellExecute = false, RedirectStandardOutput = true });
                if (query == null) return null;
                var command = query.StandardOutput.ReadToEnd().Trim();
                query.WaitForExit(3000);
                return command.Length > 0 ? command : null;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            NtQueryInformationProcess(p.Handle, 60, 0, 0, out var size); // ProcessCommandLineInformation: a UNICODE_STRING, then its text
            if (size <= 0) return null;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (NtQueryInformationProcess(p.Handle, 60, buf, size, out _) != 0) return null;
                return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, IntPtr.Size), Marshal.ReadInt16(buf) / 2);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    static DateTime? StartOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    /// Exit code 0, unlike Process.Kill's: Windows Terminal closes a tab whose shell exits 0, and keeps one that died with -1 open as a dead tab.
    /// The handle is opened right after the start-time check, so a recycled pid is all but impossible to hit.
    internal static void EndShell(Process proc)
    {
        if (!OperatingSystem.IsWindows()) { proc.Kill(); return; }
        if (!TerminateProcess(proc.SafeHandle.DangerousGetHandle(), 0)) throw new InvalidOperationException("TerminateProcess failed");
    }

    static async Task CloseShellsAsync(List<(int Pid, string Name, DateTime Start)> chain)
    {
        foreach (var (p, name, start) in chain)
        {
            try
            {
                using var proc = Process.GetProcessById(p);
                if (proc.StartTime != start) continue; // the pid went to another process meanwhile: never kill that
                if (name == "cswap") { try { await proc.WaitForExitAsync(new CancellationTokenSource(30_000).Token); } catch (OperationCanceledException) { } }
                else EndShell(proc);
            }
            catch (ArgumentException) { } // already gone
            catch (InvalidOperationException) { }
        }
    }

    /// The close and the launch of a move, after its last look. A window running workflows (`closeFirst`) is closed first, as its runs must
    /// stop before another window resumes them, then copied (what it wrote on the way out) and launched: the launch's answer. Any other window
    /// gets its new one first and is closed only once that is up and `recheck` still holds: true, or null when the old one is kept.
    /// A workflow window whose claude has not exited (`close` false) is neither copied nor launched: null, as its runs may still be going.
    public static async Task<bool?> SwapAsync(bool closeFirst, Func<Task> copy, Func<Task<bool>> launch, Func<bool> recheck, Func<Task<bool>> close)
    {
        if (closeFirst && !await close()) return null;
        try { await copy(); } catch (Exception e) { Program.Log(e); }
        if (closeFirst) return await launch();
        if (!await launch() || !recheck()) return null;
        await close();
        return true;
    }

    /// Whether a planned move may still close its window: the target not refused since the plan, no switch running, and the
    /// default login where the plan saw it (a launch on a login that moved would leave two live copies of it).
    internal static bool SafeToClose(int target, IReadOnlyDictionary<int, DateTimeOffset> refusedUntil, bool switching, int? loginAtPlan, int? loginNow, DateTimeOffset now) =>
        !(refusedUntil.GetValueOrDefault(target) > now) && !switching && loginAtPlan == loginNow;

    /// Accounts a window was just reopened on (a `session#account` stamp under 30 s old; its run marker appears a few seconds later): a switch must not pick them.
    internal static IEnumerable<int> Launching(IReadOnlyDictionary<string, DateTime> resumed, DateTime nowUtc) =>
        resumed.Where(m => nowUtc - m.Value < TimeSpan.FromSeconds(30) && int.TryParse(m.Key[(m.Key.LastIndexOf('#') + 1)..], out _))
            .Select(m => int.Parse(m.Key[(m.Key.LastIndexOf('#') + 1)..]));

    /// Whether the transcript has a reply of Claude's own (not an error) written after `since`: the session carried on after that error.
    internal static bool RanSince(string file, DateTimeOffset since)
    {
        try
        {
            foreach (var line in Tail(file))
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (Str(r, "type") == "assistant" && !(r.TryGetProperty("isApiErrorMessage", out var err) && err.ValueKind == JsonValueKind.True)
                        && r.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String && ts.TryGetDateTimeOffset(out var t) && t > since) return true;
                }
                catch (JsonException) { } // a line cut by the tail window, or one being written
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return false;
    }

    /// Whether `key` has been seen for at least `grace`: stamps it on first sight only.
    internal static bool BusyPast(Dictionary<string, DateTime> since, string key, DateTime now, TimeSpan grace)
    {
        if (!since.TryGetValue(key, out var first)) since[key] = first = now;
        return now - first >= grace;
    }

    /// `kill`: the answer decides a close (`Unchanged`), so on macOS and Linux only a process proven to be the one that wrote
    /// the record counts; without it, unknown means alive, so a live window's profile is never freed. Windows has one rule.
    internal static bool IsLiveClaude(int pid, string? procStart, bool kill = false)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (!OperatingSystem.IsWindows())
                return PosixLive(procStart, () => p.StartTime.ToUniversalTime(), OperatingSystem.IsLinux(), () =>
                {
                    var stat = File.ReadAllText($"/proc/{pid}/stat");
                    var fields = stat[(stat.LastIndexOf(')') + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    return fields.Length > 19 ? fields[19] : null;
                }, () => CommandLineOf(pid), kill);
            // The record's start stamp names the process that wrote it, so a recycled pid never matches. The image name
            // is only a fallback for unstamped records: Claude Code's updater renames a running claude.exe to
            // claude.exe.old.<stamp>.<pid>, which would hide every window older than the update.
            if (long.TryParse(procStart, out var ft))
                return Math.Abs(p.StartTime.ToUniversalTime().ToFileTimeUtc() - ft) < TimeSpan.TicksPerSecond * 2;
            return NameOf(p.Id) == "claude";
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// `IsLiveClaude` on macOS and Linux, given the live process's start (UTC), its /proc start ticks and its command line.
    /// Same stamp conventions as the Python engine's process_detection.pid_matches_record: Linux clock ticks; macOS ps lstart
    /// under C locale and UTC. `kill`: the start must match the record within 2 s, as on Windows; unknown is not a match.
    internal static bool PosixLive(string? procStart, Func<DateTime> startUtc, bool linux, Func<string?> procTicks, Func<string?> commandLine, bool kill)
    {
        if (linux && long.TryParse(procStart, out _)) return procTicks() == procStart;
        var recorded = default(DateTime);
        var stamped = !string.IsNullOrWhiteSpace(procStart) && DateTime.TryParseExact(Regex.Replace(procStart, @"\s+", " "), "ddd MMM d HH:mm:ss yyyy",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out recorded);
        if (kill) return stamped && Math.Abs((startUtc() - recorded).Ticks) < TimeSpan.TicksPerSecond * 2;
        if (!stamped) return true; // unknown must never free a live profile
        return startUtc() <= recorded.AddSeconds(2) || commandLine()?.Contains("claude", StringComparison.OrdinalIgnoreCase) != false;
    }

    internal static string? TranscriptOf(string dir, string sessionId, string cwd)
    {
        // Where Claude Code files it: the cwd with every other character as "-". The search covers long (hashed)
        // paths, and a session that outlived a `cd`.
        var direct = Path.Combine(dir, "projects", Regex.Replace(cwd, "[^A-Za-z0-9]", "-"), sessionId + ".jsonl");
        if (File.Exists(direct)) return direct;
        try { return Directory.EnumerateFiles(Path.Combine(dir, "projects"), sessionId + ".jsonl", SearchOption.AllDirectories).FirstOrDefault(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// The last lines of a transcript; they can be megabytes long, so only the end is read.
    static IEnumerable<string> Tail(string path, int bytes = 256 * 1024)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, f.Length - bytes);
        f.Seek(start, SeekOrigin.Begin);
        var buf = new byte[f.Length - start];
        f.ReadExactly(buf);
        var lines = Encoding.UTF8.GetString(buf).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return start > 0 ? lines.Skip(1) : lines;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    static string? NameOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName.Split('.')[0].ToLowerInvariant(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
    }

    /// The processes running an agents terminal's `patch-goblins agents-mcp` bridge: each `patch-goblins` process's parent and
    /// grandparent (a shell between them). Never closed or moved, as the bridge and its flags would not come back.
    internal static HashSet<int> BridgeHosts()
    {
#if !WINDOWS
        var processes = AgentProcess.ProcessTable();
        var unixHosts = new HashSet<int>();
        foreach (var process in processes.Where(p => p.Pid != Environment.ProcessId))
        {
            var name = NameOf(process.Pid);
            if (name is not ("patch-goblins-backend" or "patch-goblins")) continue;
            if (CommandLineOf(process.Pid)?.Contains("agents-mcp", StringComparison.Ordinal) != true) continue;
            unixHosts.Add(process.Parent);
            if (processes.FirstOrDefault(p => p.Pid == process.Parent) is var parent && parent.Parent > 0) unixHosts.Add(parent.Parent);
        }
        return unixHosts;
#else
        var all = new List<(int Pid, int Parent, string Exe)>();
        var snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == -1) return [];
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (var ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                all.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile));
        }
        finally { CloseHandle(snap); }
        var parent = all.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().Parent);
        var hosts = new HashSet<int>();
        foreach (var b in all.Where(p => p.Exe.Equals("patch-goblins.exe", StringComparison.OrdinalIgnoreCase) && p.Pid != Environment.ProcessId))
        {
            hosts.Add(b.Parent);
            if (parent.TryGetValue(b.Parent, out var up)) hosts.Add(up);
        }
        return hosts;
#endif
    }

    static int? ParentOf(int pid)
    {
#if !WINDOWS
        return AgentProcess.ProcessTable().FirstOrDefault(p => p.Pid == pid) is var process && process.Parent > 0 ? process.Parent : null;
#else
        var snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == -1) return null;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (var ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                if (e.th32ProcessID == pid) return (int)e.th32ParentProcessID;
            return null;
        }
        finally { CloseHandle(snap); }
#endif
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] static extern bool Process32First(nint snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] static extern bool Process32Next(nint snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(nint h, int infoClass, nint info, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(nint h, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize, dwTime; }

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    static extern double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);
}
