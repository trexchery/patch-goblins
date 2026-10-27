using System.Text.Json;
using System.Text.RegularExpressions;

namespace PatchGoblins;

/// The engine's thresholds plus the timings tests shorten.
public sealed class AgentsConfig
{
    /// The engine's autoswitch.threshold: accounts at or above it get no new runs. The tray sets it from its own `Threshold`
    /// (the engine's, else the tray's one fallback) before the first dispatch, so runs and window moves agree.
    public double Threshold { get; set; } = 90;
    public bool ConsumeFirst { get; set; }
    /// No output and no CPU this long while nothing is pending: stalled (a hung request or a hidden prompt). Reasoning and
    /// writing stream (partial messages, thinking tokens), so a live request is never this quiet.
    public TimeSpan Stall { get; set; } = TimeSpan.FromMinutes(10);
    /// The same while a tool call or a background task is pending, or ultracode fans out: they may wait quietly on the
    /// network. Above Claude's own 10-minute limit on a foreground command and its 10-minute idle wait for background work.
    public TimeSpan PendingStall { get; set; } = TimeSpan.FromMinutes(25);
    /// A run that gave its final answer with nothing pending but has not exited after this is closed and judged.
    public TimeSpan FinishGrace { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ScanEvery { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan PrEvery { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan VerifyTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan LaunchBackoff { get; set; } = TimeSpan.FromMinutes(2);
    /// After a start, how long open agents terminals get to link up again before other runs stop.
    public TimeSpan Relink { get; set; } = TimeSpan.FromSeconds(15);
    /// Items worked on at once per repository; 0 is automatic: one per account with quota to spare.
    public int MaxConcurrent { get; set; } = 1;
    /// Gap between starting runs, so the next account pick sees the usage the last one is already causing.
    public TimeSpan Ramp { get; set; } = TimeSpan.FromMinutes(3);
    /// Pull requests waiting for review before new work holds.
    public int MaxOpenPrs { get; set; } = Supervisor.MaxOpenPrs;
    /// Open pull requests as drafts.
    public bool DraftPrs { get; set; }
    /// The shell verification, policy and goal checks run under (AgentProcess.StartShell); tests judge as another machine would.
    public CheckShell.Kind Shell { get; set; } = CheckShell.Runner;
    /// Off: a push never waits for the owner's yes (review notes, a policy that asks before every push), except for
    /// files the repository's policy protects: a pushed workflow runs with the repository's secrets before anyone reads it.
    public bool AskApproval { get; set; } = true;
    /// A goal plans again at most this often after its last plan; the owner's answer or replan skips it.
    public TimeSpan ReplanEvery { get; set; } = TimeSpan.FromMinutes(10);
    /// A goal whose work has all landed looks again after this long.
    public TimeSpan IdleReplan { get; set; } = TimeSpan.FromHours(1);
    /// The owner lets runs turn on ultracode by themselves, where the Director recommends it or an escalation calls for it,
    /// whenever their account has room (Routing.UltracodeOn). Off by default.
    public bool Ultracode { get; set; }
    /// The Director orders and routes queued work no goal planned (Supervisor.WantsTriage). Off: it runs on default routes.
    public bool Triage { get; set; } = true;
}

/// `Transient`: nothing is wrong with the work itself (a fetch that failed, as when offline), so it is no failure to count.
public sealed class AgentsException(string message, bool transient = false) : Exception(message) { public bool Transient => transient; }

/// The deterministic part of Patch Goblins agents. Per authorized repository it works on one item at a
/// time: a headless Claude Code run in the item's own git worktree and account profile, a fresh
/// read-only review of the change, the repository's verification command, then a pushed branch and
/// a pull request, which it watches for failing checks, conflicts and review comments. A question
/// for the owner parks its item and work moves on; at most ten pull requests wait for review.
/// No model supervises any of this, and it does nothing while nothing needs doing: one timer, armed
/// only for the next real deadline. A repository works only while its agents terminal is open.
///
/// Single-threaded: every member runs on the SynchronizationContext it was created on (the tray's
/// UI thread); awaits are the only interleaving points.
public sealed partial class Supervisor : IDisposable
{
    public const int MaxOpenPrs = 10, MaxFailures = 3, MaxFollowups = 3, MaxReviews = 2;
    /// Rank of work the owner named (prioritized issues, requests): above anything found by scanning.
    const int OwnerRank = 10_000;
    /// Claude's own safety classifier approves routine actions; anything else is denied, never asked.
    public const string PermissionMode = "auto";

    readonly Engine _engine;
    readonly Cli _cli;
    readonly AgentStore _store;
    readonly SynchronizationContext _ctx;
    readonly AgentsState _state;
    /// Runs under way: an item's work or review, or a goal's planner.
    readonly Dictionary<object, Worker> _workers = [];
    readonly Dictionary<string, DateTimeOffset> _cooled = [];
    /// An item resting after a failed start, or a goal backing off after a failed plan or check.
    readonly Dictionary<object, DateTimeOffset> _notBefore = [];
    readonly Dictionary<RepoAgents, DateTimeOffset> _prPolled = [], _fetched = [];
    readonly HashSet<RepoAgents> _baseline = [];
    readonly HashSet<WorkItem> _checks = [];
    /// Open agents terminals per repository key, and the verifications under way (stopped with them).
    readonly Dictionary<string, int> _terminals = [];
    readonly Dictionary<WorkItem, (RepoAgents Repo, AgentProcess Proc)> _verifying = [];
    readonly HashSet<WorkItem> _interrupted = [];
    readonly HashSet<RepoAgents> _rebaseline = [];
    DateTimeOffset _started = DateTimeOffset.Now, _lastLaunch;
    /// Where the next launch starts looking: just past the repository served last, so one with a long
    /// queue cannot take every account.
    int _turn;
    readonly SemaphoreSlim _gitGate = new(1, 1);
    readonly System.Threading.Timer _timer;
    IReadOnlyList<Account>? _accounts;
    IReadOnlyList<ProjectMapping> _maps = [];
    IReadOnlySet<int> _live = new HashSet<int>();
    /// The only Claude account is the login in use (AccountPicker.Solo): runs share it, and wait while a terminal of yours works on it.
    bool _solo, _ownBusy;
    bool _allOut;
    DateTimeOffset? _resumeAt;
    bool _pumping, _pumpAgain, _launching, _disposed, _stopping;

    public AgentsConfig Config { get; }
    /// Why queued work is not starting (null when it is, or there is none).
    public string? Waiting { get; private set; }
    public IReadOnlyList<RepoAgents> Repos => _state.Repos;
    public int Working => _workers.Count;
    /// Something stopped that only the owner can fix. Questions and approvals are not: those are
    /// answered in the agents terminal, the only place agents work.
    public bool HasProblem => _state.Repos.Any(Problem) || Waiting is AccountPicker.OnlyYours or AccountPicker.NoneUsable;

    internal static bool Problem(RepoAgents r) =>
        r.Authorized && (r.Note != null || r.Items.Any(Problem) || r.Goals.Any(g => g is { State: GoalState.NeedsOwner, Failures: >= MaxFailures }));

    /// Parked after retries ran out or a check, push or pull request failed; not a hold, a question or a no-op.
    internal static bool Problem(WorkItem i) =>
        i is { State: ItemState.NeedsOwner, HoldFor: null, AskedAt: null } && i.Note?.StartsWith(NoChange) != true && !Held(i);

    /// Anything the tray shows may have changed.
    public event Action? Changed;
    /// The owner's own yes, asked natively (never by a model) before a repository runs unattended.
    /// Called with the repository and the verification command and base it would run with.
    public Func<RepoAgents, string?, string, Task<bool>>? Authorize;
    /// The owner's own yes, asked natively (never by a model), before a goal's acceptance checks run
    /// unattended on the base branch. Null or false: the checks are refused.
    public Func<RepoAgents, IReadOnlyList<string>, Task<bool>>? ConfirmChecks;
    public Action<string, string>? Notify;
    /// Every account is out of usage and the earliest reset has passed: the tray should read fresh usage now.
    public event Action? RefreshWanted;
    /// Some repository is working (or would, were there work): its terminal is open and it is not paused.
    public bool IsRunning => _state.Repos.Any(Active);

    sealed class Worker(RepoAgents repo, AgentProcess proc, string outPath, string account)
    {
        public RepoAgents Repo { get; } = repo;
        public AgentProcess Proc { get; } = proc;
        public string OutPath { get; } = outPath;
        public string Account { get; } = account;
        public long LastLen, LastProgress = AgentProcess.UnbiasedMilliseconds;
        public TimeSpan LastCpu;
        public string? StopReason;
        /// Ultracode: its workflow agents can wait on the network for long, quiet stretches.
        public bool Fanned;
        public StreamTail Tail { get; } = new(outPath);
        public RunActivity Activity { get; } = new();
        /// Its account's run count when it started (Measure).
        public int StartedAs;
        public Task? Finished;
    }

    public Supervisor(Engine engine, Cli cli, AgentStore store, AgentsConfig config)
    {
        _ctx = SynchronizationContext.Current ?? throw new InvalidOperationException("Supervisor needs a SynchronizationContext");
        _engine = engine;
        _cli = cli;
        _store = store;
        Config = config;
        _state = store.Load();
        _timer = new System.Threading.Timer(_ => _ctx.Post(_ => Pump(), null));
    }

    /// Re-attaches to runs that outlived the last session, finishes interrupted checks, then pumps.
    public void Start()
    {
        _started = DateTimeOffset.Now;
        foreach (var repo in _state.Repos)
        {
            foreach (var item in repo.Items)
            {
                // The data folder moved (autodock became Patch Goblins): the worktree is there now, and git learns its new place
                // before any prune would drop it (git runs one command at a time, in order).
                if (item.Worktree is { } was && !Directory.Exists(was) && _store.Worktree(repo, item) is var moved && Directory.Exists(moved))
                {
                    item.Worktree = moved;
                    Fire(() => GitAsync(repo.Path, "worktree", "repair", moved));
                }
                if (item.Run is { Live: true } run)
                {
                    var proc = run.Pid == 0 ? null : AgentProcess.Attach(run.Job, run.Pid, run.Created);
                    if (run.Pid == 0) AgentProcess.Reap(run.Job); // crashed between the write-ahead save and the start
                    if (proc != null) Watch(repo, item, proc);
                    else Fire(() => FinishRunAsync(repo, item, null));
                }
                else if (item.State == ItemState.Working) item.State = ItemState.Queued;
            }
            foreach (var goal in repo.Directors.Where(g => g.Run is { Live: true }))
            {
                var run = goal.Run!;
                var proc = run.Pid == 0 ? null : AgentProcess.Attach(run.Job, run.Pid, run.Created);
                if (run.Pid == 0) AgentProcess.Reap(run.Job); // crashed between the write-ahead save and the start
                if (proc != null) WatchPlanner(repo, goal, proc);
                else
                {
                    (run.Live, goal.Replan) = (false, true); // ended while Patch Goblins was away: plan again
                    Fire(() => RemoveProbeAsync(repo, PlanProbe(goal)));
                }
            }
        }
        Save();
        Pump();
    }

    /// The tray's latest Claude accounts (each refresh) and folder assignments.
    /// `live`: accounts a `cswap run` window holds (the tray's view), which the default login can never switch to.
    public void UpdateAccounts(IReadOnlyList<Account> claude, IReadOnlyList<ProjectMapping> maps, IReadOnlySet<int>? live = null, bool ownBusy = false)
    {
        _accounts = claude;
        (_solo, _ownBusy) = (AccountPicker.Solo(claude), ownBusy);
        FlushMeasures(force: false); // a reading taken after a run ended completes its line
        _maps = maps;
        _live = live ?? new HashSet<int>();
        foreach (var (item, w) in _workers)
        {
            if (w.StopReason != null || claude.FirstOrDefault(a => AccountPicker.Key(a) == w.Account) is not { } a) continue;
            if (a.Active && !_solo)
            {
                // The owner switched to this account meanwhile (or added a second one): two copies of one login must not run.
                Fire(() => StopWorkerAsync(item, "limit"));
            }
            else if (AccountPicker.SpendsMoney(a) && (a.Usage == null || AccountPicker.Peak(a) >= Config.Threshold))
            {
                // Past the threshold, or unknown, an account with extra usage on could start billing.
                _cooled[w.Account] = AccountPicker.NextReset(a, null, DateTimeOffset.Now, Config.Threshold) ?? DateTimeOffset.Now.AddHours(1);
                Fire(() => StopWorkerAsync(item, "limit"));
            }
        }
        Pump();
    }

    // -- the loop -----------------------------------------------------------

    /// One pass over everything that may be due. Re-entrant calls fold into one more pass.
    void Pump()
    {
        if (_disposed) return;
        if (_pumping) { _pumpAgain = true; return; }
        _pumping = true;
        Fire(async () =>
        {
            var failed = false;
            try
            {
                do
                {
                    _pumpAgain = false;
                    if (_resumeAt is { } due && DateTimeOffset.Now >= due) { _resumeAt = null; RefreshWanted?.Invoke(); }
                    CheckStalls();
                    StopUnwatched();
                    foreach (var repo in _state.Repos.Where(Active).ToList())
                    {
                        if (_rebaseline.Remove(repo)) Fire(() => VerifyBaselineAsync(repo)); // interrupted by a closed terminal
                        if (Due(repo.LastScan, ScanEvery(repo))) await ScanAsync(repo);
                        if (repo.Items.Any(Watched) && Due(_prPolled.GetValueOrDefault(repo), Config.PrEvery))
                            await WatchPullRequestsAsync(repo);
                        // Checks interrupted by a restart or a pause pick up where they were.
                        foreach (var item in repo.Items.Where(i => i.State == ItemState.Checking && !_checks.Contains(i) && _notBefore.GetValueOrDefault(i) <= DateTimeOffset.Now).ToList())
                            Fire(() => CheckAsync(repo, item));
                        // Approvals turned off: a push already waiting for a yes goes ahead, as an answer without text would
                        // send it. Protected files ("sensitive", "workflow" from older builds) still wait.
                        if (!Config.AskApproval)
                            foreach (var item in repo.Items.Where(i => i is { State: ItemState.NeedsOwner, HoldFor: "push" or "review", Worktree: not null }).ToList())
                            {
                                (item.State, item.PushApproved, item.HoldFor, item.Note) = (ItemState.Checking, true, null, null);
                                Save();
                                Fire(() => CheckAsync(repo, item));
                            }
                        foreach (var goal in repo.Goals.Where(g => g.State == GoalState.Active).ToList()) Tend(repo, goal);
                    }
                    await DispatchAsync();
                } while (_pumpAgain && !_disposed);
            }
            catch (Exception e)
            {
                Program.Log(e);
                failed = true;
            }
            finally
            {
                _pumping = false;
                Schedule(failed);
                Changed?.Invoke();
            }
        });
    }

    /// Arms the single timer for the earliest real deadline, or disarms it. After a failed pass, a retry
    /// in half a minute: a bug must not leave the work stranded.
    void Schedule(bool retry = false)
    {
        if (_disposed) return;
        TimeSpan? next = null;
        void Min(TimeSpan t) { if (next is not { } n || t < n) next = t < TimeSpan.Zero ? TimeSpan.Zero : t; }
        if (retry) Min(TimeSpan.FromSeconds(30));
        if (_workers.Count > 0) Min(TimeSpan.FromSeconds(Math.Clamp(Config.Stall.TotalSeconds / 4, 1, 60)));
        // An answered run with nothing pending is closed once it has been quiet for FinishGrace: wake then, never sooner.
        foreach (var w in _workers.Values.Where(w => w is { StopReason: null, Activity: { Answered: not null, Pending: false } }))
            Min(TimeSpan.FromMilliseconds(Math.Max(w.LastProgress + Config.FinishGrace.TotalMilliseconds - AgentProcess.UnbiasedMilliseconds, Config.FinishGrace.TotalMilliseconds / 4)));
        if (_workers.Count > 0 && DateTimeOffset.Now - _lastLaunch < Config.Ramp) Min(_lastLaunch + Config.Ramp - DateTimeOffset.Now); // the gap between starts ends
        if ((_workers.Count > 0 || _verifying.Count > 0) && DateTimeOffset.Now - _started < Config.Relink) Min(_started + Config.Relink - DateTimeOffset.Now);
        foreach (var repo in _state.Repos.Where(Active))
        {
            Min(Until(repo.LastScan, ScanEvery(repo)));
            if (repo.Items.Any(Watched)) Min(Until(_prPolled.GetValueOrDefault(repo), Config.PrEvery));
            // A goal's next look: its throttle after the last plan, or the idle look-again. Only ahead: one due but
            // unable to start (backlog, accounts, the gap between starts) is woken by what unblocks it.
            foreach (var g in repo.Goals.Where(g => g is { State: GoalState.Active, LastPlan: not null }))
            {
                var wait = g.Replan ? Until(g.LastPlan, Config.ReplanEvery)
                    : g.Claim == null && GoalItems(Key(repo, g)).All(i => i.State == ItemState.Done) ? Until(g.LastPlan, Config.IdleReplan)
                    : TimeSpan.Zero;
                if (wait > TimeSpan.Zero) Min(wait);
            }
            // Only ahead, as for goals: a triage due but unable to start is woken by what unblocks it.
            if (repo.Triage is { LastPlan: { } routed } && WantsTriage(repo, throttled: false) && Until(routed, Config.ReplanEvery) is var due && due > TimeSpan.Zero) Min(due);
        }
        foreach (var t in _notBefore.Values) Min(t - DateTimeOffset.Now);
        if (_resumeAt is { } resume) Min(resume - DateTimeOffset.Now);
        _timer.Change(next is { } d ? (long)Math.Max(d.TotalMilliseconds, 250) : Timeout.Infinite, Timeout.Infinite);
    }

    /// A problem note (gh signed out, issues unreadable) is checked again within a minute, so its dot clears soon after the fix.
    TimeSpan ScanEvery(RepoAgents repo) => repo.Note != null ? TimeSpan.FromMinutes(1) : Config.ScanEvery;

    static bool Due(DateTimeOffset? last, TimeSpan every) => last is not { } t || DateTimeOffset.Now - t >= every;
    static TimeSpan Until(DateTimeOffset? last, TimeSpan every) => last is { } t ? t + every - DateTimeOffset.Now : TimeSpan.Zero;

    /// A run is stalled when it has written nothing AND its process tree has used no CPU for the whole
    /// stall window. A silent test suite burns CPU; a hung network call or a hidden prompt does not.
    void CheckStalls()
    {
        var now = AgentProcess.UnbiasedMilliseconds;
        foreach (var (item, w) in _workers.ToList())
        {
            if (w.StopReason != null) continue;
            // What it waits on, from what it wrote since the last look (partial-message deltas are skipped unparsed).
            // At most 2 MB a look: a re-attached run's long file catches up over a few looks instead of holding up the tray.
            for (var (k, at) = (0, -1L); k < 8 && w.Tail.Offset != at; k++) { at = w.Tail.Offset; w.Activity.Add(w.Tail.Read(), DateTimeOffset.Now); }
            var len = FileLength(w.OutPath);
            var cpu = w.Proc.CpuTime;
            if (len != w.LastLen || cpu - w.LastCpu > TimeSpan.FromSeconds(2)) (w.LastLen, w.LastCpu, w.LastProgress) = (len, cpu, now);
            if (Verdict(w.Activity, w.Fanned, TimeSpan.FromMilliseconds(now - w.LastProgress), DateTimeOffset.Now, Config) is { } verdict)
                Fire(() => StopWorkerAsync(item, verdict));
        }
    }

    /// What a run's quiet means. "finished": it gave its final answer, nothing it started still runs, and it has been
    /// quiet for FinishGrace since (a turn starting again streams first), yet Claude has not exited; it is closed and
    /// judged like any finished run. "stalled": no output and no CPU for Stall, or PendingStall while a tool call or
    /// background task is pending (or ultracode fans out). Quiet alone is never taken for completion: only the answer is.
    internal static string? Verdict(RunActivity a, bool fanned, TimeSpan idle, DateTimeOffset now, AgentsConfig c) =>
        a is { Answered: { } at, Pending: false } && now - at >= c.FinishGrace && idle >= c.FinishGrace ? "finished"
        : idle > (fanned || a.Pending ? c.PendingStall : c.Stall) ? "stalled"
        : null;

    async Task DispatchAsync()
    {
        if (_launching || _stopping || _accounts == null) return;
        string? waiting = null;
        var started = false;
        foreach (var repo in _state.Repos.Skip(_turn).Concat(_state.Repos.Take(_turn)).Where(r => Active(r) && !_baseline.Contains(r)).ToList())
        {
            if (_disposed || _launching) break;
            // A goal's planner first, outside the item cap: what it plans decides the items. One per repository at a time.
            // With no goal to plan, the triage orders and routes the queued work; it is optional, so it never holds anything up.
            if (!_goalBusy.Any(b => b.Value == PlanningNow && Owns(repo, b.Key))
                && (repo.Goals.Where(g => WantsPlan(repo, g)).MinBy(g => g.LastPlan ?? DateTimeOffset.MinValue) ?? (WantsTriage(repo) ? repo.Triage : null)) is { } goal)
            {
                var triage = goal == repo.Triage;
                var settings = triage ? Routing.Triage : Routing.Plan;
                var (planner, why) = PickAccount(repo, null, settings.Model);
                if (planner == null) { if (!triage) waiting = why is AccountPicker.OnlyYours or AccountPicker.NoneUsable ? why : waiting ?? why; }
                else if (_workers.Count == 0 || DateTimeOffset.Now - _lastLaunch >= Config.Ramp)
                {
                    var last = goal.Run;
                    await LaunchPlannerAsync(repo, goal, planner, settings);
                    if (goal.Run != last) // one that never got to start (GitHub unreadable, paused meanwhile) takes no turn from the items
                    {
                        (_lastLaunch, _turn) = (DateTimeOffset.Now, _state.Repos.IndexOf(repo) + 1);
                        started = true;
                    }
                }
            }
            var cap = Config.MaxConcurrent > 0 ? Config.MaxConcurrent : Math.Max(1, _accounts.Count(a => AccountPicker.Usable(a, _solo))); // automatic: one per account, verifying included
            // One at a time, what is under way finishes first; with more, an item resting after a failed start holds none of the rest;
            // planned work waits for what it builds on.
            Func<WorkItem, bool> resting = cap > 1 ? i => _notBefore.GetValueOrDefault(i) > DateTimeOffset.Now || !Ready(i) : i => !Ready(i);
            // Counted over every working folder: the setting is the tray's, not each repository's; a paused or closed one's checks wait.
            while (!_disposed && !_launching && repo.LastScan != null && _state.Repos.Where(Active).Sum(r => r.Items.Count(i => i.State is ItemState.Working or ItemState.Checking)) < cap) // not before a scan re-applied what an unplan stripped
            {
                if (Next(repo, Config.MaxOpenPrs, resting) is not { } item) break;
                if (_notBefore.TryGetValue(item, out var t) && t > DateTimeOffset.Now) break;
                // The triage under way is routing it: it waits for that run (the stall check bounds it), never for one that cannot start.
                if (item.Route == null && repo.Triage is { } routing && _goalBusy.ContainsKey(routing)) break;
                var (account, settings, why) = PickRun(repo, item);
                // A reason only the owner can fix wins over one that passes by itself, whichever folder came first.
                if (account == null) { waiting = why is AccountPicker.OnlyYours or AccountPicker.NoneUsable ? why : waiting ?? why; break; }
                // Spaced out after the last start; judged after the pick, so every folder's reason stays current.
                if (_workers.Count > 0 && DateTimeOffset.Now - _lastLaunch < Config.Ramp) break;
                await LaunchAsync(repo, item, account, settings);
                (_lastLaunch, _turn) = (DateTimeOffset.Now, _state.Repos.IndexOf(repo) + 1);
                if (item.State == ItemState.Queued) break; // paused or backed off while preparing
                started = true;
            }
        }
        foreach (var k in _notBefore.Where(kv => kv.Value <= DateTimeOffset.Now).Select(kv => kv.Key).ToList()) _notBefore.Remove(k);
        Waiting = waiting;

        // Every account out of usage: hold (nothing starts, running work ends at its own limit and moves on),
        // say so once, and come back at the earliest reset with a fresh reading. Resumes by itself. Judged on
        // every account, not on the one a waiting folder is assigned to.
        var allOut = waiting != null && AccountPicker.Pick(_accounts, Config.Threshold, new Dictionary<string, int>(), _cooled, DateTimeOffset.Now, solo: _solo)
            .Why?.StartsWith(AccountPicker.AllNearLimits) == true;
        if (allOut && !_allOut)
        {
            var back = AccountPicker.EarliestReturn(_accounts, _cooled, DateTimeOffset.Now, Config.Threshold, _solo);
            Notify?.Invoke("Agents waiting for usage", "Every Claude account is near its limit" + (back is { } t ? $"; they continue by themselves {Format.When(t, DateTimeOffset.Now)}." : "."));
        }
        else if (!allOut && _allOut && (started || _workers.Count > 0)) Notify?.Invoke("Agents resumed", "A Claude account has usage again.");
        // Account refresh and baseline checks can interleave a pass with no
        // dispatch. Keep the notification pending until work actually resumes.
        _allOut = allOut || (_allOut && !started && _workers.Count == 0);
        _resumeAt = allOut ? (AccountPicker.EarliestReturn(_accounts, _cooled, DateTimeOffset.Now, Config.Threshold, _solo) ?? DateTimeOffset.Now.AddMinutes(2)) + TimeSpan.FromSeconds(30) : null;
    }

    /// The repository's next item: the one already under way, then pull request follow-ups, then new
    /// work by rank (answered questions first), passing over items `resting` after a failed start.
    /// Anything that would open another pull request waits while the limit of pull requests is waiting
    /// for review or would be with the changes under way; follow-ups on those never wait.
    internal static WorkItem? Next(RepoAgents repo, int maxPrs = MaxOpenPrs, Func<WorkItem, bool>? resting = null)
    {
        var ordered = repo.Items.Where(i => i.State == ItemState.Queued && resting?.Invoke(i) != true)
            .OrderByDescending(i => i.Guidance != null).ThenByDescending(i => i.Rank)
            .ThenBy(i => i.Issue ?? int.MaxValue).ThenBy(i => i.Created).ToList();
        var prs = repo.Items.Count(i => i.Pr != null && i.State != ItemState.Done);
        var underway = ordered.FirstOrDefault(i => i.InFlight);
        if (underway != null && prs < maxPrs) return underway;
        if (ordered.FirstOrDefault(i => i.Pr != null) is { } followup) return followup;
        // Each change under way opens a pull request too: new work counts them.
        return underway != null || prs + repo.Items.Count(i => i.InFlight && i.Pr == null) >= maxPrs ? null : ordered.FirstOrDefault();
    }

    /// The account and settings for an item's next run. Its route (the Director's, else the default) decides the model,
    /// effort and ultracode; the account needs room in that model's own weekly window. Opus out of room everywhere: a
    /// route the Director chose runs on Sonnet at no less than high effort rather than idle; one that failures escalated
    /// to Opus waits for it. Ultracode only where Routing.UltracodeOn allows it.
    (Account?, RunSettings, string?) PickRun(RepoAgents repo, WorkItem item)
    {
        var route = item.Route ?? Routing.Default;
        var s = item.ReviewNext ? Routing.Review(route) : new RunSettings(route.Model, route.Effort, false);
        if (item is { ReviewNext: true, Sensitive: not null }) s = s with { Model = "opus", Effort = "high" }; // an adversarial review of sensitive files
        var (account, why) = PickAccount(repo, item, s.Model);
        if (account == null && s.Model == "opus" && !(route.ModelRaised && !item.ReviewNext)
            && PickAccount(repo, item, "sonnet") is ({ } fallback, _))
            (account, s) = (fallback, s with { Model = "sonnet", Effort = Array.IndexOf(Routing.Efforts, s.Effort) < 2 ? "high" : s.Effort });
        if (account != null && !item.ReviewNext)
            s = s with { Ultracode = Routing.UltracodeOn(route, Config.Ultracode, account, RunsOn(account) + (_solo ? 1 : 0), Config.Threshold) };
        return (account, s, why);
    }

    /// The folder's assigned account if it has one (work code stays on its account), otherwise the best
    /// of the rest, leaving other open folders' assigned accounts to them while another fits. Never the
    /// login in use at the keyboard: runs only happen in isolated profiles.
    (Account?, string?) PickAccount(RepoAgents repo, WorkItem? item, string? model = null)
    {
        var accounts = _accounts ?? [];
        var busy = _workers.Values.GroupBy(w => w.Account).ToDictionary(g => g.Key, g => g.Count());
        var prefer = item is { ReviewNext: false, Session: { } s } ? s.Email + "|" + s.Org : null;
        (Account?, string?) From(IReadOnlyList<Account> pool) =>
            AccountPicker.Pick(pool, Config.Threshold, busy, _cooled, DateTimeOffset.Now, 1, prefer, Config.ConsumeFirst, _live, model, _solo, _ownBusy);
        // The folder's account as the default login: work code stays on it, so the folder waits for the login to move on (no problem for the owner).
        if (ProjectMap.AccountFor(repo.Path, accounts, _maps) is { } mapped)
            return mapped.Active && !_solo ? (null, $"#{mapped.Number}, this folder's account, is your login right now") : From([mapped]);
        // Held only for a folder that would start something, or has not read its issues yet.
        var theirs = _state.Repos.Where(r => r != repo && Active(r) && (r.LastScan == null || Next(r, Config.MaxOpenPrs, i => !Ready(i)) != null)).Select(r => ProjectMap.AccountFor(r.Path, accounts, _maps)).OfType<Account>().ToList();
        var pick = From(accounts.Where(a => !theirs.Contains(a) || AccountPicker.Key(a) == prefer).ToList()); // a session resumes where it lives
        return pick.Item1 != null || theirs.Count == 0 ? pick : From(accounts);
    }

    // -- runs ---------------------------------------------------------------

    async Task LaunchAsync(RepoAgents repo, WorkItem item, Account account, RunSettings settings)
    {
        _launching = true;
        var review = item.ReviewNext;
        item.State = ItemState.Working;
        item.Note = null;
        Save();
        try
        {
            var wt = await PrepareWorktreeAsync(repo, item);
            var head = (await GitAsync(wt, "rev-parse", "HEAD")).Out.Trim();
            // After the worktree is prepared (which may fetch the base): no run starts on a base the policy does not allow.
            var start = await StartPointAsync(repo);
            RepoPolicy policy;
            try { policy = await Policy.LoadAsync(_cli, repo.Path, start); }
            catch (PolicyException e) { throw new AgentsException("the repository's policy is unreadable: " + e.Message); }
            if (!await PolicyHoldsAsync(repo, policy, start))
            {
                item.State = ItemState.Queued;
                Save();
                return;
            }
            List<string> narrowed = [];
            string[] deny = [.. Policy.Confine(policy.Deny, wt, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), narrowed)];
            foreach (var n in narrowed) Program.Log($"{repo.Leaf} {item.Label}: {n}");
            var org = account.OrganizationUuid ?? "";
            var resume = !review && item.Session is { } s && s.Email == account.Email && s.Org == org;
            string prompt;
            if (review) prompt = ReviewPrompt(repo, item);
            else if (resume) prompt = ResumePrompt(repo, item);
            else
            {
                string? notes = null;
                if (item.Session != null) // an earlier conversation worked here: hand over its notes
                    try { notes = (await _engine.HandoffAsync("claude", wt)).Path; }
                    catch (Exception e) when (e is EngineException or System.Text.Json.JsonException) { }
                prompt = WorkPrompt(repo, item, notes, item.Issue is { } n ? await IssueDiscussionAsync(repo, n) : null);
            }
            if (!Active(repo) || _stopping || item.State != ItemState.Working || !repo.Items.Contains(item) || Cooled(account))
            {
                // Paused, stopped or skipped while this was being prepared, or its account yielded to the owner meanwhile.
                if (item.State == ItemState.Working && repo.Items.Contains(item)) item.State = ItemState.Queued;
                else if (item.Worktree != null) await FinishAsync(repo, item, item.Note ?? "skipped by the owner");
                Save();
                return;
            }
            var attempt = (item.Run?.Attempt ?? 0) + 1;
            var session = resume ? item.Session!.Id : Guid.NewGuid().ToString();
            Directory.CreateDirectory(_store.RunDir(repo, item));
            var promptPath = _store.RunFile(repo, item, attempt, "prompt");
            File.WriteAllText(promptPath, prompt);
            item.Run = new Run
            {
                Attempt = attempt,
                Kind = review ? "review" : "work",
                Job = $@"Local\patch-goblins-agent-{repo.Key}-{item.Id}-{attempt}-{Guid.NewGuid().ToString("N")[..6]}",
                SessionId = session,
                Email = account.Email,
                Org = org,
                Head = head,
                Started = DateTimeOffset.Now,
                Live = true,
                Settings = settings,
                QuotaStart = Routing.Snap(account, settings.Model),
                Resumed = resume,
            };
            if (!review) item.Session = new Session(session, account.Email, org);
            item.Route ??= Routing.Default; // started unrouted: escalation needs a base
            Save(); // write-ahead: the job is known before its process exists
            var outPath = _store.RunFile(repo, item, attempt, "out");
            var proc = AgentProcess.Start(_engine.Exe, WorkerArgs(account.Number, session, resume, review, wt, settings, own: _solo && account.Active, deny, [.. policy.AddDirs]), wt, item.Run.Job,
                promptPath, outPath, _store.RunFile(repo, item, attempt, "err"), WorkerEnvironment(Path.Combine(_store.Root, "no-gh"), settings));
            (item.Run.Pid, item.Run.Created) = (proc.Pid, proc.CreatedTicks);
            Save();
            Watch(repo, item, proc);
            // One launch at a time: the engine sets up account profiles under its store lock.
            await WaitForStartAsync(outPath, proc, TimeSpan.FromSeconds(45));
        }
        catch (Exception e) when (e is AgentsException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (item.Run is { Live: true, Pid: 0 } run) { AgentProcess.Reap(run.Job); run.Live = false; }
            if (Dropped(repo, item))
            {
                // Skipped while it was being prepared (which is likely why it failed): clean up, nothing more.
                if (item.Worktree != null) await FinishAsync(repo, item, item.Note ?? "skipped by the owner");
            }
            else
            {
                _notBefore[item] = DateTimeOffset.Now + Config.LaunchBackoff;
                Charge(repo, item, Engine.Redact(e.Message));
            }
            Save();
        }
        finally
        {
            _launching = false;
            Pump();
        }
    }

    /// `cswap run N --require-session`: the account's isolated profile or nothing, never the default login. `own`: the one
    /// account there is, and it is the default login (AccountPicker.Solo): the engine then launches plain claude on it, never a second copy of its login.
    /// Runs cannot push or call gh, nor use what the repository's policy denies (Policy.Confine); reviewers cannot edit files
    /// either. Their file tools reach their worktree and the folders the owner approved (`dirs`, Policy.AddDirs), nothing
    /// else (Policy.Guard). Every run states its model, effort and ultracode.
    internal static string[] WorkerArgs(int account, string session, bool resume, bool review, string worktree, RunSettings? settings = null, bool own = false, string[]? deny = null, string[]? dirs = null) =>
    [
        "run", account.ToString(), .. own ? (string[])[] : ["--require-session"], "--",
        "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages", // a request that streams is never taken for a stall
        resume ? "--resume" : "--session-id", session,
        "--permission-mode", PermissionMode,
        "--strict-mcp-config",
        .. (dirs ?? []).SelectMany(d => new[] { "--add-dir", d }),
        "--disallowedTools", "Bash(git push *)", "Bash(gh *)", .. review ? (string[])["Edit", "Write", "NotebookEdit"] : [], .. deny ?? [],
        .. settings != null ? Routing.Args(settings, Guard(worktree, dirs ?? [])) : ["--settings", JsonSerializer.Serialize(new { hooks = Guard(worktree, dirs ?? []) })],
    ];

    /// The PreToolUse hook that runs Policy.Guard on every file tool call, with the folders the run may reach: its
    /// worktree, where Claude Code keeps its long tool outputs for that folder, and the owner's approved ones.
    static object Guard(string worktree, string[] dirs) => Guard(worktree, dirs, !OperatingSystem.IsWindows());

    /// `posix`: macOS and Linux. The hook runs under sh there, so each argument is single-quoted and kept literally. Claude
    /// Code keeps a folder's long tool outputs in `<tmp>/claude/<the folder's path, every other character a dash>`, where
    /// `<tmp>` is /tmp (macOS: also /private/tmp, the same place) unless the temp folder differs.
    internal static object Guard(string worktree, string[] dirs, bool posix)
    {
        var kept = Regex.Replace(posix ? Policy.Full(worktree, "/", true) : Path.GetFullPath(worktree), "[^A-Za-z0-9]", "-");
        string[] temps = posix ? [.. new[] { "/tmp", "/private/tmp", Path.GetTempPath().TrimEnd('/') }.Distinct().Select(t => $"{t}/claude/{kept}")] : [Path.Combine(Path.GetTempPath(), "claude", kept)];
        string[] roots = [worktree, .. temps, .. dirs];
        string[] call = [Environment.ProcessPath!, "guard", .. roots];
        var command = string.Join(' ', call.Select(a => posix ? "'" + a.Replace("'", @"'\''") + "'" : $"\"{a.Replace('\\', '/')}\""));
        return new { PreToolUse = Policy.GuardedTools.Select(t => new { matcher = t, hooks = new[] { new { type = "command", command } } }) };
    }

    /// The tray's environment, minus anything that would point Claude elsewhere, plus a guard between
    /// runs and GitHub: no git remote, no gh login, no git credential helper; Patch Goblins pushes and opens
    /// pull requests itself, after verifying and review. Runs act as the owner's Windows user, so this
    /// stops mistakes and casual attempts, not a determined program; Claude's own auto-mode classifier,
    /// the owner-only issue and comment rules, and holding workflow changes for the owner do the rest.
    /// Nothing that remaps a model alias or sets thinking comes along. A run's subagents and workflow agents run on its
    /// own model (a repository's settings cannot widen the allowlist for them); no advisor consults another model behind
    /// its back; without ultracode, workflows are off outright.
    internal static Dictionary<string, string?> WorkerEnvironment(string noGh, RunSettings? run = null)
    {
        Directory.CreateDirectory(noGh);
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONUTF8"] = "1",
            ["PYTHONUNBUFFERED"] = "1",
            ["NO_COLOR"] = "1",
            ["NoDefaultCurrentDirectoryInExePath"] = "1", // never run a `claude` planted in the worktree
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_ALLOW_PROTOCOL"] = "file",
            ["GH_TOKEN"] = "patch-goblins-agent-has-no-github-access",
            ["GITHUB_TOKEN"] = "patch-goblins-agent-has-no-github-access",
            ["GH_PROMPT_DISABLED"] = "1",
            ["GH_CONFIG_DIR"] = noGh, // an empty gh profile: not signed in
            ["GIT_CONFIG_COUNT"] = "1", // no credential helper hands git the owner's GitHub login
            ["GIT_CONFIG_KEY_0"] = "credential.helper",
            ["GIT_CONFIG_VALUE_0"] = "",
            ["CLAUDE_CONFIG_DIR"] = null,
            ["CLAUDECODE"] = null,
            ["ANTHROPIC_MODEL"] = null,
            ["ANTHROPIC_SMALL_FAST_MODEL"] = null,
            ["MAX_THINKING_TOKENS"] = null,
        };
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && (k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ANTHROPIC_DEFAULT_", StringComparison.OrdinalIgnoreCase))) env[k] = null;
        env["CLAUDE_CODE_DISABLE_ADVISOR_TOOL"] = "1";
        if (run?.Ultracode != true) env["CLAUDE_CODE_DISABLE_WORKFLOWS"] = "1";
        if (run != null) (env["CLAUDE_CODE_SUBAGENT_MODEL"], env["CLAUDE_CODE_SUBAGENT_MODEL_FORCE"]) = (run.Model, "1");
        return env;
    }

    static async Task WaitForStartAsync(string outPath, AgentProcess proc, TimeSpan timeout)
    {
        var tail = new StreamTail(outPath);
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !proc.HasExited)
        {
            if (tail.Read().OfType<InitEvent>().Any()) return;
            await Task.Delay(500);
        }
    }

    void Watch(RepoAgents repo, WorkItem item, AgentProcess proc) =>
        Watch(repo, item, item.Run!, _store.RunFile(repo, item, item.Run!.Attempt, "out"), proc, w => FinishRunAsync(repo, item, w));

    /// Tracks a run (an item's, or a goal's planner) until its process exits, then hands it to `finish`.
    void Watch(RepoAgents repo, object key, Run run, string outPath, AgentProcess proc, Func<Worker, Task> finish)
    {
        var w = new Worker(repo, proc, outPath, run.Email + "|" + run.Org) { LastCpu = proc.CpuTime, Fanned = run.Settings?.Ultracode == true };
        w.StartedAs = _starts[w.Account] = _starts.GetValueOrDefault(w.Account) + 1; // a reading taken after this counts this run too
        w.LastLen = FileLength(outPath);
        _workers[key] = w;
        w.Finished = Fire(async () =>
        {
            await proc.WaitForExitAsync();
            await finish(w);
        });
    }

    async Task StopWorkerAsync(object key, string reason)
    {
        if (!_workers.TryGetValue(key, out var w)) return;
        // The owner acting on a goal outranks a pause or a limit that came first: a plan read in after it must not be used.
        if (w.StopReason != null) { if (reason == "owner") w.StopReason = reason; return; }
        w.StopReason = reason;
        await w.Proc.StopAsync(Config.StopGrace);
    }

    /// Judges a finished run from its own output (the process is gone, or was never re-attached).
    async Task FinishRunAsync(RepoAgents repo, WorkItem item, Worker? w)
    {
        if (_disposed) return; // the next start re-attaches or judges it
        var reason = w?.StopReason is "finished" ? null : w?.StopReason; // closed after its final answer: judged as finished
        if (w != null)
        {
            await w.Proc.ReapAsync(); // still listed meanwhile: its account stays busy until the run is judged
            _workers.Remove(item);
            w.Proc.Dispose();
            if (_disposed) return;
        }
        if (item.Run is not { } run) return;
        run.Live = false;
        if (reason == "skipped" || item.State == ItemState.Done || !repo.Items.Contains(item))
        {
            Save(); // the owner dropped it meanwhile
            return;
        }
        var review = run.Kind == "review";
        if (review && item.Worktree is { } wt)
        {
            await RestoreAfterReviewAsync(wt, run.Head);
            if (Dropped(repo, item)) { Save(); return; }
        }
        var events = StreamTail.ReadAll(_store.RunFile(repo, item, run.Attempt, "out"));
        var started = events.OfType<InitEvent>().Any();
        var result = FinalResult(events);
        var text = result?.Text ?? "";
        var err = TailOf(_store.RunFile(repo, item, run.Attempt, "err"), 2000);
        // Found ended at start with no result: something outside stopped it (a restart, a sign-out,
        // a shutdown) while Patch Goblins was not watching. Not the work's failure.
        if (w == null && result == null) reason ??= "restart";
        item.State = ItemState.Queued;
        if (started && !review) item.Guidance = null; // delivered
        if (!review)
            foreach (var l in LearnedLine.Matches(text).Take(3))
                Memory.Add(repo.Memory, l.Groups[1].Value, item.Label, false, DateTimeOffset.Now);
        text = StripLearned(text); // what a note mentions is not the run's verdict or summary

        string outcome;
        if (reason == "stalled") { outcome = "stalled"; Charge(repo, item, "it stopped making progress"); } // a hung call or prompt, not the work: no escalation
        else if (reason == "limit") { outcome = "limit"; item.Note = "its account became unavailable; continuing on another"; }
        else if (reason != null) { outcome = reason; item.Note = null; } // paused, stopped, skipped or rebooted: nothing failed
        else if (review && Marker(text, "APPROVED") != null)
        {
            outcome = "approved";
            (item.ReviewNext, item.ReviewedHead, item.ReviewNotes) = (false, run.Head, null);
            item.State = ItemState.Checking;
        }
        else if (review && Marker(text, "CHANGES") is { } findings)
        {
            outcome = "changes";
            item.ReviewNext = false;
            item.Reviews++;
            if (item.Reviews >= MaxReviews)
            {
                // Two passes: the rest is for the owner to weigh in the pull request.
                (item.ReviewedHead, item.ReviewNotes) = (run.Head, findings);
                item.State = ItemState.Checking;
            }
            else
            {
                item.Followup = "A fresh review of your change found these problems. Fix them:\n" + findings;
                Escalate(repo, item, "review");
            }
        }
        else if (!review && Marker(text, "BLOCKED") is { } question) { outcome = "blocked"; await AskOwnerAsync(repo, item, question); }
        else if (!review && Marker(text, "DONE") is { } summary) { outcome = "done"; EnterChecking(item, summary); }
        else if (LimitHit(text) || LimitHit(err))
        {
            // Not the work's fault: rest the account until it resets; the item moves on with a hand-off.
            outcome = "limit";
            CoolAccount(run.Email + "|" + run.Org);
        }
        else if (NoConversation(text) || NoConversation(err)) { outcome = "no conversation"; item.Session = null; } // the transcript is elsewhere: hand off
        else if (!started && err.Contains("active default login", StringComparison.OrdinalIgnoreCase))
        {
            outcome = "account in use";
            _cooled[run.Email + "|" + run.Org] = DateTimeOffset.Now.AddMinutes(5); // the owner switched to it: another account, free
        }
        else if (!started) { outcome = "did not start"; _notBefore[item] = DateTimeOffset.Now + Config.LaunchBackoff; Charge(repo, item, "Claude did not start: " + OneLine(err)); }
        else if (!review && result is { IsError: false }) { outcome = "done"; EnterChecking(item, LastLine(text)); }
        else { outcome = "error"; Charge(repo, item, result == null ? "the run ended without a result" : OneLine(text)); } // an API or tool error: not the work's to escalate
        Measure(repo, item.Label, run, outcome, events, run.Settings?.Model, w);
        Save();
        if (item.State == ItemState.Checking) await CheckAsync(repo, item);
        Pump();
    }

    /// A genuine failure of the work ("review": a review found problems; "verify": the verification command or the pull
    /// request's checks failed). An item's first verify failure may be a flake: only later ones escalate. Infrastructure
    /// (limits, stalls, starts, crashes) never comes here. The worktree, branch and conversation stay: the next run resumes.
    void Escalate(RepoAgents repo, WorkItem item, string kind)
    {
        var route = item.Route ??= Routing.Default;
        if (kind == "verify" && !route.VerifyFailed) { route.VerifyFailed = true; return; }
        if (Routing.Escalate(route, kind, Config.Ultracode) is not { } up) return; // all at the top: the failure count decides
        item.Route = up;
        if (GoalOf(item.Goal) is { } g) AddLog(g, DateTimeOffset.Now, $"{item.Label}: {up.Why}, now {up.Line}");
        else if (repo.Triage is { } t) AddLog(t, DateTimeOffset.Now, $"{item.Label}: {up.Why}, now {up.Line}");
    }

    /// The run's answer. A run can end with several results (one saying its background work still runs, an empty one
    /// last), so: the last that carries a HANDOFF marker, else the last with text, else the last.
    internal static ResultEvent? FinalResult(List<StreamEvent> events)
    {
        var results = events.OfType<ResultEvent>().ToList();
        return results.LastOrDefault(r => RunActivity.FinalMarker.IsMatch(r.Text)) ?? results.LastOrDefault(r => r.Text.Trim().Length > 0) ?? results.LastOrDefault();
    }

    /// Finished runs waiting for their account's next usage reading, and when each account last started a run.
    /// `Starts`: the account's run count at the end, -1 when another run shared the account meanwhile, -2 when no one watched it end.
    readonly List<(Dictionary<string, object?> Line, string Account, string? Model, DateTimeOffset Ended, int Starts)> _unmeasured = [];
    /// Runs started (or re-attached) per account: a count that moves during or after a run means its readings are shared.
    readonly Dictionary<string, int> _starts = [];

    /// One finished run's measurements, appended to metrics.jsonl (local, never sent anywhere): what it ran with, what
    /// Claude says it ran on, how long, its tokens, and its account's usage before and after. Quota is the usage
    /// readings' difference, never the price-table cost Claude also reports. The line waits for the tray's next reading
    /// of the account (FlushMeasures): the one at hand is older than the end.
    void Measure(RepoAgents repo, string what, Run run, string outcome, List<StreamEvent> events, string? model, Worker? w)
    {
        (run.Outcome, run.Ended) = (outcome, DateTimeOffset.Now);
        var result = events.OfType<ResultEvent>().LastOrDefault(r => r.Tokens != null) ?? events.OfType<ResultEvent>().LastOrDefault(); // modelUsage is the session's running total
        var line = new Dictionary<string, object?>
        {
            ["at"] = run.Ended, ["repo"] = repo.Leaf, ["item"] = what, ["kind"] = run.Kind, ["attempt"] = run.Attempt,
            ["model"] = run.Settings?.Model, ["effort"] = run.Settings?.Effort, ["ultracode"] = run.Settings?.Ultracode,
            ["ran_on"] = events.OfType<InitEvent>().LastOrDefault()?.Model,
            ["seconds"] = (int)(run.Ended.Value - run.Started).TotalSeconds,
            ["claude_ms"] = events.OfType<ResultEvent>().Sum(r => r.DurationMs ?? 0), ["turns"] = result?.Turns, ["tokens"] = result?.Tokens,
            ["resumed"] = run.Resumed, // a resumed conversation's tokens are its whole history's
            ["quota_start"] = run.QuotaStart,
            ["quota_end"] = null,
            ["outcome"] = outcome,
        };
        var account = run.Email + "|" + run.Org;
        var starts = _starts.GetValueOrDefault(account);
        _unmeasured.Add((line, account, model, run.Ended.Value,
            w == null ? -2 : starts != w.StartedAs || _workers.Values.Any(o => o != w && o.Account == account) ? -1 : starts));
    }

    /// Writes the lines whose end reading has come: a live reading of the account taken after the run ended. It stays
    /// empty, with why, when another run started there first, a usage window reset during the run, or no fresh reading
    /// came within half an hour or before Patch Goblins stopped (`force`): a stale or shared reading would mislead.
    void FlushMeasures(bool force)
    {
        foreach (var m in _unmeasured.ToList())
        {
            var end = Routing.Snap(_accounts?.FirstOrDefault(a => AccountPicker.Key(a) == m.Account), m.Model);
            var fresh = end is { At: { } at } && at > m.Ended;
            if (!fresh && !force && DateTimeOffset.Now - m.Ended < TimeSpan.FromMinutes(30)) continue;
            var shared = m.Starts == -1 || m.Starts >= 0 && _starts.GetValueOrDefault(m.Account) != m.Starts;
            var reset = (m.Line["quota_start"] as QuotaSnap)?.ResetsAt is { } r && end?.At >= r;
            m.Line["quota_end"] = fresh && !shared && !reset && m.Starts != -2 ? end : null;
            if (m.Line["quota_end"] == null)
                m.Line["quota_note"] = m.Starts == -2 ? "it ended while Patch Goblins was not running" : shared ? "another run shared the account" : !fresh ? "no fresh reading after the run" : "a usage window reset during the run";
            try { File.AppendAllText(Path.Combine(_store.Root, "metrics.jsonl"), JsonSerializer.Serialize(m.Line, Json.Options) + "\n"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Program.Log(e); }
            _unmeasured.Remove(m);
        }
    }

    /// Not the run's fault: rest its account until it resets (an hour when unknown).
    void CoolAccount(string key)
    {
        var acct = _accounts?.FirstOrDefault(a => AccountPicker.Key(a) == key);
        _cooled[key] = (acct is null ? null : AccountPicker.NextReset(acct, null, DateTimeOffset.Now, Config.Threshold)) ?? DateTimeOffset.Now.AddHours(1);
    }

    static void EnterChecking(WorkItem item, string summary)
    {
        item.State = ItemState.Checking;
        item.Note = item.Summary = summary;
    }

    /// Skipped or finished meanwhile: nothing may bring it back.
    static bool Dropped(RepoAgents repo, WorkItem item) => item.State == ItemState.Done || !repo.Items.Contains(item);

    /// A failed attempt. Under the limit the item resumes; at it, the owner hears about it once.
    void Charge(RepoAgents repo, WorkItem item, string why)
    {
        if (Dropped(repo, item)) return;
        item.Failures++;
        if (item.Failures < MaxFailures)
        {
            item.State = ItemState.Queued;
            item.Note = why;
            return;
        }
        item.State = ItemState.NeedsOwner;
        item.Note = $"stopped after {item.Failures} attempts: {why}";
        Notify?.Invoke("Agents need you", $"{repo.Leaf} · {item.Label}: {item.Note}");
    }

    // -- owner controls -----------------------------------------------------

    public async Task PauseAsync(RepoAgents? only = null)
    {
        foreach (var repo in _state.Repos.Where(r => only == null || r == only)) repo.Paused = true;
        Save();
        await StopWorkersAsync(only, "paused");
        Pump();
    }

    public void Resume(RepoAgents? only = null)
    {
        foreach (var repo in _state.Repos.Where(r => only == null || r == only)) { repo.Paused = false; repo.Note = null; }
        Save();
        Pump();
    }

    /// Ends continuous operation; open pull requests stay open and nothing is deleted.
    public async Task StopAsync(RepoAgents repo)
    {
        repo.Authorized = false;
        repo.Paused = false;
        Save();
        await StopWorkersAsync(repo, "stopped");
        Pump();
    }

    /// Runs under way on `account` (stopping ones included: their wrappers still hold its profile).
    public int RunsOn(Account account) => _workers.Values.Count(w => w.Account == AccountPicker.Key(account));

    /// The owner's login needs `account`: its runs stop as at a usage limit (each item carries on later on another account, in
    /// its own worktree and conversation, nothing counted against it) and the account rests `rest`, so nothing starts on it
    /// meanwhile. Returns once their claudes are gone; each `cswap run` wrapper then exits and seals the profile on its own.
    public async Task<int> YieldAsync(Account account, TimeSpan rest)
    {
        Rest(account, rest);
        var key = AccountPicker.Key(account);
        var runs = _workers.Where(kv => kv.Value.Account == key && kv.Value.StopReason == null).Select(kv => kv.Key).ToList();
        await Task.WhenAll(runs.Select(k => StopWorkerAsync(k, "limit")));
        Pump();
        return runs.Count;
    }

    /// Nothing new starts on `account` for `rest` (runs already there go on).
    public void Rest(Account account, TimeSpan rest)
    {
        var key = AccountPicker.Key(account);
        var until = DateTimeOffset.Now + rest;
        if (_cooled.GetValueOrDefault(key) < until) _cooled[key] = until;
    }

    /// Resting since it was picked: a run that starts on it now would hold the account a yield just freed.
    bool Cooled(Account account) => _cooled.GetValueOrDefault(AccountPicker.Key(account)) > DateTimeOffset.Now;

    /// When `account` stops resting (a yield, a limit it hit, the owner switching to it), or null when it is not resting: the roster's job line.
    public DateTimeOffset? CooledUntil(Account account) =>
        _cooled.TryGetValue(AccountPicker.Key(account), out var until) && until > DateTimeOffset.Now ? until : null;

    Task StopWorkersAsync(RepoAgents? only, string reason) =>
        Task.WhenAll(_workers.Where(kv => only == null || kv.Value.Repo == only).Select(kv => kv.Key).ToList().Select(k => StopWorkerAsync(k, reason)));

    /// Stops every run now (Patch Goblins is exiting and the owner asked for that), and waits until their
    /// outcomes are recorded so the next start resumes them instead of counting failures.
    public async Task StopAllAsync()
    {
        _stopping = true; // nothing new starts while Patch Goblins exits
        var finishing = _workers.Values.Select(w => w.Finished).OfType<Task>().ToList();
        await Task.WhenAny(Task.WhenAll(StopWorkersAsync(null, "paused"), Task.WhenAll(finishing)), Task.Delay(Config.StopGrace + TimeSpan.FromSeconds(15)));
    }

    // -- the agents terminal ------------------------------------------------

    /// Authorized, not paused, and its agents terminal open: agents never work in the background.
    bool Active(RepoAgents repo) => repo.Running && TerminalOpen(repo);

    public bool TerminalOpen(RepoAgents repo) => _terminals.GetValueOrDefault(repo.Key) > 0;

    /// An agents terminal opened for this folder, or linked up again after a restart.
    /// Settings changed: look for work again.
    public void Poke() => Pump();

    internal bool Pumping => _pumping;

    /// Why nothing starts, for a test that timed out waiting: the flags a launch needs clear, and what each goal waits on.
    internal string Diagnose()
    {
        var now = DateTimeOffset.Now;
        string Goals(RepoAgents r) => string.Join("; ", r.Goals.Select(g => $"{g.Id} replan={g.Replan} claim={g.Claim != null} wantsPlan={WantsPlan(r, g)} dueIn={Until(g.LastPlan, Config.ReplanEvery).TotalSeconds:0.0}s"));
        return $"pumping={_pumping} again={_pumpAgain} launching={_launching} stopping={_stopping} workers={_workers.Count} verifying={_verifying.Count} gitGate={_gitGate.CurrentCount} "
            + $"busy=[{string.Join(", ", _goalBusy.Select(b => $"{b.Key.Id}:{b.Value}"))}] notBefore=[{string.Join(", ", _notBefore.Select(n => $"{(n.Value - now).TotalSeconds:0.0}s"))}] "
            + string.Join(" | ", _state.Repos.Select(r => $"{r.Leaf} active={Active(r)} {Goals(r)}"));
    }

    public void Attach(string path)
    {
        var key = AgentStore.KeyFor(path);
        _terminals[key] = _terminals.GetValueOrDefault(key) + 1;
        Pump();
    }

    /// One closed. With none left, the repository's work stops as on a pause and continues on the next open.
    public void Detach(string path)
    {
        var key = AgentStore.KeyFor(path);
        if (_terminals.GetValueOrDefault(key) > 1) _terminals[key]--;
        else _terminals.Remove(key);
        Pump();
    }

    /// Runs and verifications of repositories without an open terminal stop: no failure, resumed later.
    /// Right after a start, open terminals first get a moment to link up again.
    void StopUnwatched()
    {
        if (DateTimeOffset.Now - _started < Config.Relink) return;
        foreach (var repo in _state.Repos.Where(r => !Active(r)))
        {
            foreach (var key in _workers.Where(kv => kv.Value.Repo == repo).Select(kv => kv.Key).ToList()) Fire(() => StopWorkerAsync(key, "paused"));
            foreach (var (item, (_, proc)) in _verifying.Where(v => v.Value.Repo == repo).ToList())
            {
                _verifying.Remove(item);
                _interrupted.Add(item);
                Fire(() => proc.StopAsync(TimeSpan.Zero));
            }
        }
    }

    public RepoAgents? RepoFor(string path)
    {
        var key = AgentStore.KeyFor(path);
        return _state.Repos.FirstOrDefault(r => r.Key == key);
    }

    // -- helpers ------------------------------------------------------------

    void Save()
    {
        try { _store.Save(_state); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Program.Log(e); }
    }

    /// Fire-and-forget on the supervisor's context; a bug logs instead of taking the tray down.
    Task Fire(Func<Task> work)
    {
        return Run();
        async Task Run()
        {
            try { await work(); }
            catch (Exception e) { Program.Log(e); }
        }
    }

    static readonly Regex LearnedLine = new(@"^[ \t]*HANDOFF:[ \t]*LEARNED\b[ \t:]*(\S.*)$\r?\n?", RegexOptions.Multiline);

    internal static string StripLearned(string text) => LearnedLine.Replace(text, "");

    /// What follows the run's last `HANDOFF: <kind>` line (to the end of its output).
    internal static string? Marker(string text, string kind)
    {
        var m = Regex.Matches(text, @"HANDOFF:\s*" + kind + @"\b[ \t:]*").LastOrDefault();
        if (m == null) return null;
        var rest = text[(m.Index + m.Length)..];
        // Another marker line (a LEARNED note before the final line) is not part of this one's text.
        if (Regex.Match(rest, @"^[ \t]*HANDOFF:", RegexOptions.Multiline) is { Success: true } next) rest = rest[..next.Index];
        rest = rest.Trim();
        return rest.Length > 0 ? Engine.Redact(rest) : kind.ToLowerInvariant();
    }

    /// Claude's own wording for a spent subscription window or a rate limit.
    internal static bool LimitHit(string text) =>
        text.Length < 600 && Regex.IsMatch(text, @"hit your (session|weekly|monthly( spend)?|usage) limit|usage limit reached|rate limit|\(429\)|Credit balance is too low", RegexOptions.IgnoreCase);

    static bool NoConversation(string text) => text.Contains("No conversation found", StringComparison.OrdinalIgnoreCase);

    static string OneLine(string s)
    {
        s = Engine.Redact(s).ReplaceLineEndings(" ").Trim();
        return s.Length > 300 ? s[..300] + "…" : s.Length == 0 ? "no details" : s;
    }

    static string LastLine(string s) =>
        OneLine(s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "");

    static long FileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
    }

    internal static string TailOf(string path, int chars)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Position = Math.Max(0, fs.Length - chars * 2);
            var s = new StreamReader(fs).ReadToEnd();
            return Engine.Redact(s.Length > chars ? s[^chars..] : s);
        }
        catch (IOException) { return ""; }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        FlushMeasures(force: true);
        // Runs keep going; the next start re-attaches them, and stops them unless their terminal is open.
        foreach (var w in _workers.Values) w.Proc.Dispose();
        _workers.Clear();
    }
}
