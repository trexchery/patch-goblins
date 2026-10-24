using System.Diagnostics;
using System.Text;

namespace PatchGoblins;

/// A fresh usage-limit error as Claude Code wrote it, in any transcript of a config dir: the main loop's, a
/// subagent's, a workflow agent's.
public sealed record LimitHit(string File, string ConfigDir, Limit Limit);

/// Sees usage-limit errors the moment they are written: a watcher on each config dir's `projects` (recursive, so
/// `<session>/subagents/**` too), with `Poll` of the live windows' files as the net under it (a watcher can overflow
/// or die). A file is read from where it was last read, so a hit costs a few bytes of I/O, not a rescan.
public sealed class LimitWatch(Action<LimitHit> onHit, Action<string> log, Func<DateTimeOffset>? clock = null) : IDisposable
{
    /// An older error is history (a resumed or copied transcript), not news.
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);
    /// First sight of a file: only its end, where a new error is.
    const int FirstLook = 64 * 1024;
    readonly Dictionary<string, long> _read = [];
    readonly Dictionary<string, FileSystemWatcher> _watchers = [];
    readonly HashSet<long> _seen = []; // error timestamps: a transcript the tray copies carries the same error again
    readonly object _lock = new();
    bool _disposed; // under _lock: a Sync or Read still running at exit adds no watcher and posts no hit

    public bool Watching(string configDir) { lock (_lock) return _watchers.ContainsKey(configDir); }

    /// Starts a watcher on every config dir that has none: at the start, after one died, or once a profile appears.
    public void Sync(IEnumerable<string> configDirs)
    {
        foreach (var dir in configDirs)
        {
            var projects = Path.Combine(dir, "projects");
            if (Watching(dir) || !Directory.Exists(projects)) continue;
            try
            {
                var w = new FileSystemWatcher(projects, "*.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    InternalBufferSize = 64 * 1024,
                };
                w.Changed += (_, e) => Read(e.FullPath, dir);
                w.Created += (_, e) => Read(e.FullPath, dir);
                w.Renamed += (_, e) => Read(e.FullPath, dir);
                w.Error += (_, e) => Fail(dir, e.GetException());
                w.EnableRaisingEvents = true;
                lock (_lock)
                {
                    if (_disposed) { w.Dispose(); return; }
                    _watchers[dir] = w;
                }
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { log($"limit watch on {dir}: {e.Message}; the poll covers it"); }
        }
    }

    /// A buffer overflow (events lost) or a dead watcher: drop it. The poll reads whatever was missed from where each
    /// file was last read, and the next `Sync` starts a fresh watcher.
    internal void Fail(string dir, Exception e)
    {
        log($"limit watch on {dir}: {e.Message}; the poll covers it");
        FileSystemWatcher? w;
        lock (_lock) _watchers.Remove(dir, out w);
        if (w != null) Task.Run(w.Dispose);
    }

    /// Reads what was added to each file since it was last read; the net under the watchers.
    public void Poll(IEnumerable<(string File, string Dir)> files)
    {
        foreach (var (file, dir) in files) Read(file, dir);
    }

    void Read(string path, string dir)
    {
        var hits = new List<LimitHit>();
        try
        {
            lock (_lock)
            {
                if (_disposed) return;
                using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var len = f.Length;
                var known = _read.TryGetValue(path, out var at) && at <= len; // shorter than last time: rewritten, look again
                var start = known ? at : Math.Max(0, len - FirstLook);
                if (start >= len) return;
                f.Seek(start, SeekOrigin.Begin);
                var buf = new byte[len - start];
                f.ReadExactly(buf);
                var end = Array.LastIndexOf(buf, (byte)'\n');
                if (end < 0)
                {
                    // Cut mid-line at the start: nothing to wait for. A line still being written: read on once it ends.
                    if (!known && start > 0) _read[path] = len;
                    return;
                }
                _read[path] = start + end + 1;
                var now = (clock ?? (() => DateTimeOffset.UtcNow))();
                var lines = Encoding.UTF8.GetString(buf, 0, end + 1).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // Typed `apiError` only: a server throttle is not a limit. A first look starts mid-line: its first line is cut.
                foreach (var line in !known && start > 0 ? lines.Skip(1) : lines)
                    if (line.Contains("usage_limit_reached") && ResumeStopped.LimitOf([line]) is { ResetsAt: { } reset } l
                        && l.At != default && now - l.At <= Fresh && reset > now && Remember(l.At))
                        hits.Add(new LimitHit(path, dir, l));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // locked a moment: the poll retries
        lock (_lock) if (_disposed) return;
        foreach (var h in hits) onHit(h);
    }

    /// The tray copies a transcript into another config dir: the error it ends with is the old one again, not a new refusal
    /// of that dir's account. Remembered before the copy, only that error is dropped; a new refusal there is still news.
    public bool Remember(DateTimeOffset at)
    {
        lock (_lock)
        {
            // Only errors older than the freshness window go: they can never be a hit again, so a burst's copies stay known (ponytail: a burst of 1000 inside two minutes grows it, fine)
            if (_seen.Count > 1000) _seen.RemoveWhere(t => t < ((clock ?? (() => DateTimeOffset.UtcNow))() - Fresh).UtcTicks);
            return _seen.Add(at.UtcTicks);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
        }
    }
}

/// What one limit event came to: null from `OnLimitAsync` when it was already handled. `Switched`: the default login
/// moved; `NoRoom`: no account to move it to (`Earliest`: when one frees up); `Held`: room only on accounts a `cswap run`
/// profile holds (the move pass frees one, or says what holds it); `Error`: every try failed.
public sealed record LimitOutcome(SwitchResult? Switched = null, bool NoRoom = false, DateTimeOffset? Earliest = null, string? Error = null, bool Held = false);

/// What a usage-limit error does at once: dedupe it, tell the engine the account is full, and move the default login to
/// the account with room from the latest reading. No refresh, no quiet wait; a stale reading corrects itself when the
/// next refusal arrives here and the next account is tried.
public sealed class LimitSwitch(Func<Snapshot?> snapshot, Func<(int? Account, DateTime ChangedAt)> login, Func<ISet<int>> liveProfiles,
    Func<string, Task> report, Func<int, Task<SwitchResult>> switchTo, ISet<string> reported, Action<string> log, Func<DateTimeOffset>? clock = null,
    Func<double>? threshold = null, Func<double>? hysteresis = null)
{
    /// Accounts the API refused, until their reset: no reading, however fresh it looks, puts one back before then.
    public Dictionary<int, DateTimeOffset> RefusedUntil { get; } = [];
    /// Refusals of a model's own window ("Fable" on #2): they keep windows on that model off the account, and nothing else.
    public Dictionary<(int Account, string Scope), DateTimeOffset> ScopedRefusedUntil { get; } = [];
    readonly HashSet<string> _handled = [];
    readonly Dictionary<(string File, long Reset), (int Account, DateTimeOffset At, string Session)> _chargedTo = []; // default-login refusals: a stuck window replays its error, so the same transcript and reset is the same refuser, until it runs again
    /// The tray moved a session: its old refusals are not its next ones.
    public void Forget(string session) { lock (_chargedTo) foreach (var k in _chargedTo.Where(c => c.Value.Session == session).Select(c => c.Key).ToList()) _chargedTo.Remove(k); }
    DateTimeOffset _lastTry; // when every try last failed
    readonly SemaphoreSlim _one = new(1, 1); // one move at a time; separate from the engine's own gate on purpose
    /// A switch is running: a window move planned before it must not launch a profile of the account it moves to.
    public bool Switching { get; private set; }

    public async Task<LimitOutcome?> OnLimitAsync(LimitHit hit)
    {
        await _one.WaitAsync();
        try
        {
            var now = (clock ?? (() => DateTimeOffset.UtcNow))();
            var snap = snapshot();
            var l = hit.Limit;
            var reset = l.ResetsAt!.Value; // LimitWatch hands over only errors that say when
            if (snap == null) return null; // not listed yet: nothing is recorded, so the caller can look again
            var profile = ResumeStopped.ProfileAccount(hit.ConfigDir);
            var window = (hit.File, reset.ToUnixTimeSeconds());
            // A replay (the same transcript, same reset, a new timestamp, nothing run in between) keeps its first refuser; a transcript that carried on after the switch, or another window's refusal with an aligned reset, is the login's.
            (int Account, DateTimeOffset At, string Session) first = default;
            bool replay;
            lock (_chargedTo) replay = profile == null && _chargedTo.TryGetValue(window, out first) && !ResumeStopped.RanSince(hit.File, first.At);
            var refused = replay ? first.Account : ResumeStopped.Refuser(l, profile, snap.Accounts, login());
            if (!replay && profile == null && refused is { } charged)
                lock (_chargedTo) _chargedTo[window] = (charged, l.At, ResumeStopped.AgentSession(hit.ConfigDir, hit.File) ?? Path.GetFileNameWithoutExtension(hit.File));
            // A refusal seen again is not logged or reported again, but the switch still happens while the default login is on the refuser (it may have come back).
            var again = !_handled.Add($"{refused?.ToString() ?? "?"}:{reset.ToUnixTimeSeconds()}");
            if (again && (now - _lastTry < TimeSpan.FromSeconds(30) || !(refused is { } rn && (login().Account ?? snap.ActiveAccountNumber) == rn && RefusedUntil.TryGetValue(rn, out var until0) && until0 > now))) return null; // a repeat retries at most every 30 s: a burst of agents is not a burst of ungated spawns
            if (!again) log($"limit seen {Path.GetFileName(hit.File)} acct {refused?.ToString() ?? "?"} resets {reset:u}");
            if (refused is not { } n) return new LimitOutcome(); // unattributed: only the windows are looked at
            var model = ResumeStopped.ModelOfFile(hit.File);
            var scope = ResumeStopped.ScopeOf(l, snap.Accounts.FirstOrDefault(a => a.Number == n), model);
            if (scope != null)
            {
                if (!ScopedRefusedUntil.TryGetValue((n, scope), out var was) || was < reset) ScopedRefusedUntil[(n, scope)] = reset;
            }
            else if (!RefusedUntil.TryGetValue(n, out var until) || until < reset) RefusedUntil[n] = reset;
            try
            {
                var stand = new StoppedSession(0, "", "", hit.ConfigDir, n, hit.File, Limit: l, OwnRefused: true, Model: model);
                if (ResumeStopped.LimitReport([stand], snap.Accounts, now, reported) is { } doc) _ = Import(doc); // not awaited: the switch does not need it, and it can wait on the usage store's lock
            }
            catch (Exception e) { log($"import-usage failed: {e.Message}"); }
            // A model's own window ran out ("Fable"): the account still has general room for every other window, so the default
            // login stays; the window that ran on that model is moved by itself (the tray's pass, ResumeStopped.Target).
            if (scope != null) { if (!again) log($"limit is {scope}'s own on acct {n}: only windows on {scope} move"); return new LimitOutcome(); }
            // Only the default login moves here, and only while it is still the refuser: the engine's own check may have moved it already.
            if ((login().Account ?? snap.ActiveAccountNumber) != n) return new LimitOutcome();
            var line = threshold?.Invoke() ?? ResumeStopped.Full;
            var live = liveProfiles();
            var targets = ResumeStopped.SwitchTargets(snap.Accounts, n, live, RefusedUntil, now, line);
            // Held only when the move pass would free a held account; else nothing frees one, and that is said like no room.
            if (targets.Count == 0 && ResumeStopped.FreeTarget(snap.Accounts, live, RefusedUntil, now, line, hysteresis?.Invoke() ?? 0) != null) return new LimitOutcome(Held: true);
            if (targets.Count == 0) return new LimitOutcome(NoRoom: true, Earliest: ResumeStopped.EarliestReset(snap.Accounts, RefusedUntil, now));
            var clockMs = Stopwatch.StartNew();
            Switching = true;
            foreach (var m in targets.Take(3)) // ponytail: three tries, then the engine's own check takes over
            {
                try
                {
                    var r = await switchTo(m);
                    if (r.Switched) log($"switch {n}->{m} {clockMs.ElapsedMilliseconds} ms");
                    return new LimitOutcome(r);
                }
                catch (Exception e) { log($"switch {n}->{m} failed: {e.Message}"); } // lock timeout, profile ahead, credential error: the next account
            }
            _lastTry = now;
            return new LimitOutcome(Error: $"The switch away from #{n} failed; the engine's own check is trying.");
        }
        finally { Switching = false; _one.Release(); }
    }

    async Task Import(string doc)
    {
        try { await report(doc); }
        catch (Exception e) { log($"import-usage failed: {e.Message}"); }
    }
}
