using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatchGoblins;

/// Where a piece of work stands. Queued → Working → Checking → Open (pull request awaiting review)
/// → Done. NeedsOwner parks it with a question while other work goes on; feedback on an open pull
/// request queues it again.
public enum ItemState { Queued, Working, Checking, Open, NeedsOwner, Done }

/// One unit of work: a GitHub issue, or a request the owner made in the agents terminal.
public sealed class WorkItem
{
    public string Id { get; set; } = "";
    public int? Issue { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int Rank { get; set; }
    public ItemState State { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    /// The owner-facing line: a question, why it stopped, or what was done.
    public string? Note { get; set; }
    /// The last run's own account of its change: the pull request's description.
    public string? Summary { get; set; }
    /// When the question was asked (answers on the issue after this resolve it).
    public DateTimeOffset? AskedAt { get; set; }
    /// The owner's answer or instruction, handed to the next run once.
    public string? Guidance { get; set; }
    /// What the next run must fix (verification output, review findings, PR checks and comments).
    public string? Followup { get; set; }
    public string? Branch { get; set; }
    public string? Worktree { get; set; }
    public int? Pr { get; set; }
    public string? PrUrl { get; set; }
    /// Its pull request was merged (a closed one was not).
    public bool Merged { get; set; }
    /// The goal it serves: "{repo.Key}/{goal.Id}" (always the key, even in its own repository).
    public string? Goal { get; set; }
    /// Items ("{repo.Key}/{item.Id}") that must land first: merged, finished without a pull request, or gone.
    public List<string> After { get; set; } = [];
    /// The rank it had before a plan raised it; null when the plan did not (or the owner ranked it since).
    public int? OwnRank { get; set; }
    /// PR feedback after this moment is new.
    public DateTimeOffset? PrSeen { get; set; }
    /// When the pull request was opened (or first found open), for the morning summary.
    public DateTimeOffset? PrOpened { get; set; }
    public int Failures { get; set; }
    public int Followups { get; set; }
    /// The next run reviews the change instead of working on it.
    public bool ReviewNext { get; set; }
    public int Reviews { get; set; }
    /// The commit the last review approved; a review stands only while HEAD is still this commit.
    public string? ReviewedHead { get; set; }
    /// Findings the last allowed review still had, carried into the pull request for the owner.
    public string? ReviewNotes { get; set; }
    /// Why it waits for the owner before a push ("review", "sensitive", "push"; "workflow" from older builds); an answer without text approves it.
    public string? HoldFor { get; set; }
    /// Changed files the policy calls sensitive (Policy.SensitiveIn): their review is adversarial, their push the owner's.
    public List<string>? Sensitive { get; set; }
    /// What verified the pushed commit: one line per check, with its outcome and commit; the pull request's CI after it.
    public List<string>? Verification { get; set; }
    /// The owner cleared the next push.
    public bool PushApproved { get; set; }
    /// The implementing conversation, resumed when its account is available again.
    public Session? Session { get; set; }
    /// The latest process (work or review).
    public Run? Run { get; set; }
    /// How its runs go: the Director's validated routing, escalated by genuine failures. Null until routed (or started).
    public Route? Route { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public string Label => Issue is { } n ? $"#{n} {Title}" : $"{Id} {Title}";
    /// Started and not yet handed to review or parked: the repository's one active item.
    [JsonIgnore] public bool InFlight => State is ItemState.Working or ItemState.Checking || (State == ItemState.Queued && Worktree != null && Pr == null);
}

public sealed record Session(string Id, string Email, string Org)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// A note in the agents' shared memory. Owner notes are the owner's own words or decisions and bind every
/// agent; agent notes are what runs learned: hints to verify.
public sealed record Note(string Text, string Source, DateTimeOffset At, bool Owner = false)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// A worker process: enough to re-attach after a restart.
public sealed class Run
{
    public int Attempt { get; set; }
    /// "work", "review" or "plan".
    public string Kind { get; set; } = "work";
    public string Job { get; set; } = "";
    public int Pid { get; set; }
    public long Created { get; set; }
    public string SessionId { get; set; } = "";
    public string Email { get; set; } = "";
    public string Org { get; set; } = "";
    /// The worktree's HEAD when the run started.
    public string Head { get; set; } = "";
    public DateTimeOffset Started { get; set; }
    /// The process may still be running (cleared once its outcome is handled).
    public bool Live { get; set; }
    /// What it was started with (null: before routing existed).
    public RunSettings? Settings { get; set; }
    /// Its account's usage at the start, to compare with the end's.
    public QuotaSnap? QuotaStart { get; set; }
    /// It continued an earlier conversation (its token counts include that history).
    public bool Resumed { get; set; }
    /// Measured once it ended: how it went, and how long it took.
    public string? Outcome { get; set; }
    public DateTimeOffset? Ended { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public enum GoalState { Active, Achieved, NeedsOwner, Dropped }

/// An objective the owner set in the agents terminal. The Director plans work toward it, replans as that
/// work lands, and calls it achieved only on evidence.
public sealed class Goal
{
    /// g1, g2 … per repository.
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Objective { get; set; } = "";
    /// An issue of the owner's adopted as this goal.
    public int? Issue { get; set; }
    /// Owner-confirmed commands that must pass on the base branch.
    public List<string> Checks { get; set; } = [];
    public GoalState State { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    /// The question, why it stopped, or the evidence.
    public string? Note { get; set; }
    /// The owner's answer, for the next plan once.
    public string? Guidance { get; set; }
    public bool Replan { get; set; } = true;
    public DateTimeOffset? LastPlan { get; set; }
    public int Plans { get; set; }
    /// Plans in a row that changed nothing.
    public int Idle { get; set; }
    public int Failures { get; set; }
    /// The goal items' states as the last plan saw them: a change means plan again.
    public string? Progress { get; set; }
    /// The Director says it is achieved: its evidence, to be checked.
    public string? Claim { get; set; }
    /// Why the last completion check failed, for the next plan.
    public string? Refuted { get; set; }
    /// What proved it achieved.
    public string? Evidence { get; set; }
    /// When it was achieved (the morning summary counts these).
    public DateTimeOffset? AchievedAt { get; set; }
    /// Newest last, at most 12 lines.
    public List<string> Log { get; set; } = [];
    /// The planner run.
    public Run? Run { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// A repository the owner authorized for continuous operation.
public sealed class RepoAgents
{
    public string Path { get; set; } = "";
    public string Base { get; set; } = "main";
    /// Command that must pass in the worktree before a branch is pushed.
    public string? Verify { get; set; }
    /// The policy's path-selected checks as the owner confirmed them; a changed policy applies once confirmed again.
    public List<PolicyCheck> Checks { get; set; } = [];
    public bool Authorized { get; set; }
    public DateTimeOffset? AuthorizedAt { get; set; }
    public bool Paused { get; set; }
    /// Why the repository as a whole is held up (unreadable issues, a failing verification baseline).
    public string? Note { get; set; }
    public DateTimeOffset? LastScan { get; set; }
    public List<int> Skipped { get; set; } = [];
    public List<WorkItem> Items { get; set; } = [];
    public int NextLocal { get; set; } = 1;
    public List<Goal> Goals { get; set; } = [];
    public int NextGoal { get; set; } = 1;
    /// The Director's pass over queued work that serves no goal: ordering and routing only, never new work. A goal's
    /// planner runs it (its run, failures, back-off), but it is no goal the owner sees.
    public Goal? Triage { get; set; }
    /// What the owner and runs noted about this repository.
    public List<Note> Memory { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public string Leaf => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;

    /// Short stable key for folder and job names.
    [JsonIgnore] public string Key => AgentStore.KeyFor(Path);

    [JsonIgnore] public bool Running => Authorized && !Paused;

    /// Every director run holder: the goals, and the triage.
    [JsonIgnore] public IEnumerable<Goal> Directors => Triage is { } t ? Goals.Append(t) : Goals;
}

public sealed class AgentsState
{
    public List<RepoAgents> Repos { get; set; } = [];
    /// Notes that hold in every repository.
    public List<Note> Memory { get; set; } = [];
    /// Fields an older or newer build wrote, here and on every level below: saved back as they were,
    /// so neither build erases the other's.
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// state.json plus each run's files, under the tray's private folder. The tray is the only writer
/// (single instance); every change is a whole-file atomic replace.
public sealed class AgentStore(string root)
{
    public string Root { get; } = root;
    public static string DefaultRoot => System.IO.Path.Combine(DockSettings.Dir, "agents");

    string StatePath => System.IO.Path.Combine(Root, "state.json");

    static readonly JsonSerializerOptions Options = new(Json.Options)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AgentsState Load()
    {
        try { return Upgrade(JsonSerializer.Deserialize<AgentsState>(File.ReadAllText(StatePath), Options) ?? new()); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection instead of silently overwriting it.
            try { File.Copy(StatePath, StatePath + ".corrupt", overwrite: true); } catch (IOException) { }
            return new();
        }
    }

    /// State written before `Merged` existed: a finished item whose note says its pull request merged was merged.
    internal static AgentsState Upgrade(AgentsState s)
    {
        foreach (var i in s.Repos.SelectMany(r => r.Items).Where(i => i is { State: ItemState.Done, Merged: false, Pr: { } p } && i.Note == $"pull request #{p} merged")) i.Merged = true;
        return s;
    }

    public void Save(AgentsState s) => AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(s, Options));

    /// A repository was left running, or a worker may still be: the tray starts agents, so open terminals link up
    /// and runs without one stop.
    public static bool HasWork(string root) =>
        new AgentStore(root).Load().Repos.Any(r => r.Authorized || r.Items.Any(i => i.Run is { Live: true }) || r.Directors.Any(g => g.Run is { Live: true }));

    /// A worker of `root`'s state still runs: workers outlive the tray that started them.
    public static bool Running(string root) =>
        new AgentStore(root).Load().Repos.SelectMany(r => r.Items.Select(i => i.Run).Concat(r.Directors.Select(g => g.Run)))
            .Any(r => r is { Live: true, Pid: > 0 } && AgentProcess.Attach(r.Job, r.Pid, r.Created) is { } p && Close(p));

    static bool Close(AgentProcess p) { p.Dispose(); return true; }

    public string RunDir(RepoAgents repo, WorkItem item) => System.IO.Path.Combine(Root, "runs", $"{repo.Key}-{item.Id}");

    public string Worktree(RepoAgents repo, WorkItem item) => System.IO.Path.Combine(Root, "wt", $"{repo.Key}-{item.Id}");

    public string RunFile(RepoAgents repo, WorkItem item, int attempt, string kind) =>
        System.IO.Path.Combine(RunDir(repo, item), $"{kind}-{attempt}.{(kind == "out" ? "jsonl" : kind == "prompt" ? "md" : "log")}");

    public static string KeyFor(string path)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
        if (OperatingSystem.IsWindows()) full = full.ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))))[..6].ToLowerInvariant();
    }
}
