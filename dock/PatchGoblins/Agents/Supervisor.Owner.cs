using System.Text;
using System.Text.Json;

namespace PatchGoblins;

// What the agents terminal asks for (through the Patch Goblins MCP tools), the answers, and the prompts.
public sealed partial class Supervisor
{
    /// One tool call from the agents terminal, for the repository the terminal was opened in.
    public async Task<string> HandleAsync(string tool, JsonElement args, string repoPath)
    {
        var repo = RepoFor(repoPath);
        string Str(string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        switch (tool)
        {
            case "agents_status":
                return Status(repo, repoPath, args.ValueKind == JsonValueKind.Object && args.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.True);

            case "agents_control":
                var action = Str("action");
                if (action == "start" || (action == "resume" && repo is not { Authorized: true }))
                    return await StartRepoAsync(repoPath, Str("verify"), Str("base"));
                if (repo == null) return "Agents were never started for this repository.";
                switch (action)
                {
                    case "pause":
                        var n = _workers.Keys.OfType<WorkItem>().Count(repo.Items.Contains);
                        await PauseAsync(repo);
                        return n > 0 ? "Paused. The item in progress stopped where it was and continues from there on resume." : "Paused.";
                    case "resume":
                        Resume(repo);
                        return "Resumed.";
                    case "stop":
                        await StopAsync(repo);
                        return "Stopped continuous work here. Open pull requests stay open; nothing was merged or deleted.";
                }
                return "Unknown action; use start, pause, resume or stop.";

            case "agents_queue":
                if (repo == null) return "Agents were never started for this repository.";
                return await QueueCommandAsync(repo, Str("action"), args, Str("item"), Str("text"), Str("title"));

            case "agents_memory":
                var shared = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("global", out var g) && g.ValueKind == JsonValueKind.True;
                var notes = shared ? _state.Memory : repo?.Memory;
                var what = Str("text");
                switch (Str("action"))
                {
                    case "list" or "":
                        return Memory.List(_state.Memory, repo?.Memory ?? []);
                    case "add" or "forget" when notes == null:
                        return "Agents were never started for this repository; use global to note something for every repository.";
                    case "add":
                        if (what.Length == 0) return "Say what to remember.";
                        Memory.Add(notes!, what, "owner", true, DateTimeOffset.Now);
                        Save();
                        return shared ? "Noted for every repository." : "Noted for this repository.";
                    case "forget":
                        // List prints shared notes as g1, g2 …
                        what = what.Trim();
                        if (System.Text.RegularExpressions.Regex.IsMatch(what, @"^g\d+\.?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) (notes, what) = (_state.Memory, what[1..].TrimEnd('.'));
                        var gone = Memory.Forget(notes!, what);
                        Save();
                        return gone == 0 ? "No such note; the numbers are those agents_memory list prints." : $"Forgot {gone} note{(gone == 1 ? "" : "s")}.";
                }
                return "Unknown action; use list, add or forget.";

            case "agents_details":
                if (repo == null) return "Agents were never started for this repository.";
                return Find(repo, Str("item")) is { } item ? Details(repo, item) : NoItem;

            case "agents_goal":
                if (repo == null) return "Agents were never started for this repository; start them first (agents_control start).";
                return await GoalCommandAsync(repo, Str("action"), args, Str("goal"), Str("title"), Str("text"));

            case "agents_doctor":
                var report = await Doctor.AssessAsync(_cli, repoPath, Str("base") is { Length: > 0 } b ? b : repo?.Base, _me, _store.Root, repo?.Verify);
                return Engine.Redact(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("bootstrap", out var bs) && bs.ValueKind == JsonValueKind.True
                    ? report.Text() + "\n\n" + Doctor.Bootstrap(report)
                    : report.Text());
        }
        return "Unknown tool.";
    }

    async Task<string> StartRepoAsync(string path, string verify, string baseBranch)
    {
        if (!Directory.Exists(Path.Combine(path, ".git")) && !File.Exists(Path.Combine(path, ".git")))
            return $"{path} is not the root of a git repository.";
        var repo = RepoFor(path);
        var fresh = repo == null;
        repo ??= new RepoAgents { Path = Path.GetFullPath(path).TrimEnd('\\', '/') };
        var newVerify = verify.Length > 0 ? verify : repo.Verify;
        RepoPolicy? local;
        try { local = Policy.ReadLocal(Policy.LocalPath(path)); }
        catch (PolicyException e) { return "Nothing started: " + e.Message; }
        var newBase = baseBranch.Length > 0 ? baseBranch : local?.BaseRef.Branch is { Length: > 0 } lb ? lb : fresh || !repo.Authorized ? await DefaultBranchAsync(repo.Path) : repo.Base;
        // The base as it is now: its policy and pin are judged on a fresh fetch.
        var fetch = await HasOriginAsync(repo) ? await GitAsync(repo.Path, "fetch", "--quiet", "origin", newBase) : null;
        var start = (await GitAsync(repo.Path, "rev-parse", "--verify", "--quiet", "origin/" + newBase)).Ok ? "origin/" + newBase : newBase;
        RepoPolicy policy;
        try { policy = await Policy.LoadAsync(_cli, repo.Path, start); }
        catch (PolicyException e) { return "Nothing started: " + e.Message; }
        if (policy.BaseRef.Pin is { } pin && fetch is { Ok: false })
            return $"Nothing started: could not fetch origin/{newBase} to check it is still the pinned {pin}: {fetch.Why}";
        if (await BaseRefusedAsync(repo.Path, policy, newBase, start) is { } refused)
            return $"Nothing started: {refused}. " + (policy.BaseRef.Branch == newBase
                ? "Update the pin once the owner has reviewed the new commits (agents_doctor lists them)."
                : $"Start with base {policy.BaseRef.Branch}, or change `base` in the policy.");
        // The commands Patch Goblins will run unattended, and where pushes go, are the owner's to see and confirm,
        // first time and whenever they change.
        var shown = string.Join("\n", new[] { newVerify, Policy.Describe(policy.Checks) }.Where(s => s is { Length: > 0 }));
        if (!repo.Authorized || newVerify != repo.Verify || newBase != repo.Base || Policy.Describe(policy.Checks) != Policy.Describe(repo.Checks))
        {
            // What is new must run under this machine's shell; what the owner confirmed before is left as it was.
            var incoming = (newVerify != repo.Verify && newVerify is { Length: > 0 } nv ? [nv] : new List<string>())
                .Concat(policy.Checks.Where(c => c.Os == null || c.Os.Equals(CheckShell.Os, StringComparison.OrdinalIgnoreCase))
                    .Where(c => !repo.Checks.Any(o => o.Run == c.Run && o.Os == c.Os)).Select(c => c.Run));
            if (CheckShell.Refusal(incoming, Config.Shell, "The command") is { } wrong)
                return $"Nothing started: {wrong} A policy check written for another system takes an \"os\" (linux, macos or windows).";
            if (Authorize == null || !await Authorize(repo, shown.Length > 0 ? shown : null, newBase)) return "The owner did not confirm in the Patch Goblins dialog, so nothing changed.";
            repo.AuthorizedAt = DateTimeOffset.Now;
        }
        repo.Checks = policy.Checks;
        if (fresh) _state.Repos.Add(repo);
        repo.Authorized = true;
        repo.Paused = false;
        repo.Note = null;
        (repo.Verify, repo.Base) = (newVerify, newBase);
        repo.LastScan = null;
        Save();
        Fire(() => VerifyBaselineAsync(repo));
        Pump();
        return $"Started. Agents now work through the actionable open issues of {repo.Leaf}, one at a time against {repo.Base}" +
               (repo.Verify is { } v ? $", gated by `{v}`" : repo.Checks.Count == 0 ? " (no verification command, so pull requests rely on CI)" : "") +
               (repo.Checks.Count > 0 ? $", with {repo.Checks.Count} path-selected check{(repo.Checks.Count == 1 ? "" : "s")} from the repository's policy" : "") +
               $", with at most {Config.MaxOpenPrs} pull requests waiting for review. They work while this terminal is open: closing it pauses them, and reopening it continues.";
    }

    /// Why the policy refuses `branch`, at `start`, as the agents' base: it names another branch, or pins this one to a
    /// commit `start` is not. Null while it allows it.
    async Task<string?> BaseRefusedAsync(string path, RepoPolicy policy, string branch, string start)
    {
        if (policy.BaseRef is not ({ Length: > 0 } named, var pin)) return null;
        if (named != branch) return $"the policy names {named} as the base, not {branch}";
        if (pin == null) return null;
        var tip = (await GitAsync(path, "rev-parse", "--verify", "--quiet", start + "^{commit}")).Out.Trim();
        return tip.Length == 0 ? $"{start} does not exist"
            : tip.StartsWith(pin, StringComparison.OrdinalIgnoreCase) ? null
            : $"{start} is at {tip[..7]}, not the pinned {pin}";
    }

    /// While agents work: the base's policy at `start` still allows their base and its pin, and its checks are the ones the
    /// owner confirmed. Otherwise agents here pause (runs under way finish) and nothing starts, is verified or pushed until
    /// the owner starts them again, which shows the policy as it is now.
    async Task<bool> PolicyHoldsAsync(RepoAgents repo, RepoPolicy policy, string start)
    {
        var why = await BaseRefusedAsync(repo.Path, policy, repo.Base, start)
            ?? (Policy.Describe(policy.Checks) != Policy.Describe(repo.Checks) ? "the policy's checks changed since you confirmed them" : null);
        if (why == null) return true;
        if (repo.Paused) return false; // already paused: one note, one notification
        repo.Paused = true;
        repo.Note = $"Agents paused: {why}. Start them again (agents_control start) to confirm the policy as it is now.";
        Notify?.Invoke("Agents paused", $"{repo.Leaf}: {why}");
        Save();
        return false;
    }

    async Task<string> DefaultBranchAsync(string path)
    {
        var head = await GitAsync(path, "symbolic-ref", "--short", "refs/remotes/origin/HEAD");
        if (head.Ok && head.Out.Trim() is { Length: > 7 } h && h.StartsWith("origin/")) return h[7..];
        var current = await GitAsync(path, "rev-parse", "--abbrev-ref", "HEAD");
        return current.Ok && current.Out.Trim() is { Length: > 0 } c && c != "HEAD" ? c : "main";
    }

    async Task<string> QueueCommandAsync(RepoAgents repo, string action, JsonElement args, string which, string text, string title)
    {
        switch (action)
        {
            case "prioritize":
                var numbers = args.TryGetProperty("issues", out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList() : [];
                if (numbers.Count == 0) return "Name the issues in the order they should run, e.g. [12, 7].";
                var top = Math.Max(repo.Items.Select(i => i.Rank).DefaultIfEmpty(0).Max(), OwnerRank);
                var missing = new List<int>();
                for (var k = 0; k < numbers.Count; k++)
                {
                    repo.Skipped.Remove(numbers[k]);
                    var item = repo.Items.FirstOrDefault(i => i.Issue == numbers[k]) ?? await AddIssueAsync(repo, numbers[k]);
                    if (item == null) { missing.Add(numbers[k]); continue; }
                    (item.Rank, item.OwnRank) = (top + (numbers.Count - k) * 10, null);
                }
                Save();
                Pump();
                return "Order updated." + (missing.Count > 0 ? $" Not found or not open: {string.Join(", ", missing.Select(m => "#" + m))}." : "");

            case "skip":
                if (Find(repo, which) is not { } skip) return NoItem;
                if (skip.Issue is { } si && !repo.Skipped.Contains(si)) repo.Skipped.Add(si);
                await StopWorkerAsync(skip, "skipped");
                await FinishAsync(repo, skip, "skipped by the owner");
                if (GoalOf(skip.Goal) is { } sg)
                {
                    AddLog(sg, DateTimeOffset.Now, $"the owner skipped {skip.Label}");
                    sg.Claim = null; // the claim may have counted on it
                    // A goal waiting on this step got its answer: plan again without it, now (the owner acted). A question
                    // or problem about anything else stays for the owner.
                    if (sg.State == GoalState.NeedsOwner && sg.Note?.StartsWith(skip.Label) == true) (sg.State, sg.Note, sg.Replan, sg.LastPlan) = (GoalState.Active, null, true, null);
                    if (_workers.ContainsKey(sg)) Fire(() => StopWorkerAsync(sg, "owner")); // its plan may recreate the step: discarded, and Replan stays set
                }
                if (skip.Pr == null) repo.Items.Remove(skip);
                HoldDependents(_state.Repos, repo, skip);
                Save();
                Pump();
                return $"{skip.Label} is skipped" + (skip.Pr is { } p ? $"; pull request #{p} is left as it is." : ".");

            case "restore":
                if (!int.TryParse(which.TrimStart('#'), out var rn) || !repo.Skipped.Remove(rn)) return "That issue was not skipped.";
                repo.LastScan = null;
                Save();
                Pump();
                return $"#{rn} can be picked up again.";

            case "add":
                if (title.Length == 0 && text.Length == 0) return "Describe the work (title and details).";
                var request = new WorkItem
                {
                    Id = "w" + repo.NextLocal++,
                    Title = title.Length > 0 ? title : text.Split('\n')[0],
                    Body = text,
                    Rank = Math.Max(repo.Items.Select(i => i.Rank).DefaultIfEmpty(0).Max(), OwnerRank) + 10,
                };
                repo.Items.Add(request);
                Save();
                Pump();
                return $"Queued as {request.Id}" + (repo.Running ? "." : "; it starts once agents are running here.");

            case "answer" or "retry":
                if (Find(repo, which) is not { } it) return NoItem;
                if (it.State is not (ItemState.NeedsOwner or ItemState.Done)) return $"{it.Label} is {Describe(it)}; nothing to answer.";
                if (it.HoldFor != null && text.Length == 0 && it.Worktree != null)
                {
                    // The owner approves the push as it is.
                    (it.State, it.PushApproved, it.HoldFor, it.Note) = (ItemState.Checking, true, null, null);
                    Save();
                    Fire(() => CheckAsync(repo, it));
                    return $"{it.Label}: pushing it as it is.";
                }
                if (text.Length > 0)
                {
                    it.Guidance = text;
                    Memory.Add(repo.Memory, $"{it.Label}: {text}", "answer", true, DateTimeOffset.Now);
                }
                if (Held(it)) it.OwnRank = null; // the owner chose to run it: the scan's re-judge leaves it, as for a prioritized one
                (it.State, it.Failures, it.Note, it.AskedAt, it.HoldFor, it.Reviews) = (ItemState.Queued, 0, null, null, null, 0);
                if (it.Followups > MaxFollowups) it.Followups = 0;
                Save();
                Pump();
                return $"{it.Label} is queued again" + (text.Length > 0 ? " with your answer." : ".");
        }
        return "Unknown action; use prioritize, skip, restore, add, answer or retry.";
    }

    /// What was planned on a skipped item leaves the goal: removed, it counts as landed and they would start on its absence;
    /// kept with its pull request unmerged, they would wait for it forever. An adopted issue is the owner's: it waits for them,
    /// and so does an issue the Director ordered after it.
    internal static void HoldDependents(IReadOnlyList<RepoAgents> repos, RepoAgents repo, WorkItem skip, string why = "which you skipped")
    {
        for (var q = new Queue<(RepoAgents, WorkItem)>([(repo, skip)]); q.TryDequeue(out var next);)
        {
            var (sr, s) = next;
            var sk = Key(sr, s);
            foreach (var r in repos)
                foreach (var d in r.Items.Where(d => d.After.Contains(sk) && (d.Goal != null || d is { Issue: not null, State: ItemState.Queued, Worktree: null, Pr: null })).ToList())
                {
                    var dg = repos.SelectMany(x => x.Goals.Where(g => Key(x, g) == d.Goal)).FirstOrDefault(); // before Unplan clears d.Goal
                    if (d is { Issue: not null, State: ItemState.Queued, Worktree: null, Pr: null })
                    {
                        (d.State, d.Note) = (ItemState.NeedsOwner, $"{SkipHold}{skip.Label}, {why}; answer to run it anyway");
                        d.After.Remove(sk); // answered, it must not wait for what the owner skipped
                        if (dg != null) dg.Replan = true;
                    }
                    else if (Held(d)) d.After.Remove(sk); // already the owner's question: skipping another step does not answer it
                    else if (Unplan(r, d.Goal!, [d]) > 0)
                    {
                        if (dg != null) dg.Claim = null; // it left without landing: the claim may have counted on it
                        q.Enqueue((r, d));
                    }
                }
        }
    }

    /// An adopted issue held because a step it was planned after was skipped: a question for the owner, no problem.
    const string SkipHold = "it was planned after ";

    static bool Held(WorkItem i) => i is { State: ItemState.NeedsOwner, Worktree: null, Pr: null } && i.Note?.StartsWith(SkipHold) == true;

    const string NoItem = "No such item; name it as #12 (issue) or w3 (request).";

    static WorkItem? Find(RepoAgents repo, string which)
    {
        which = which.Trim().TrimStart('#');
        return repo.Items.FirstOrDefault(i => i.Id.Equals(which, StringComparison.OrdinalIgnoreCase));
    }

    // -- answers ------------------------------------------------------------

    /// What happened since `since`, for the morning notification; null when nothing did.
    public string? Summary(DateTimeOffset since)
    {
        var items = Repos.SelectMany(r => r.Items).ToList();
        var opened = items.Where(i => i.Pr != null && i.PrOpened >= since).ToList();
        var asks = items.Count(i => i.State == ItemState.NeedsOwner && !Problem(i));
        var stuck = items.Count(Problem);
        var achieved = Repos.SelectMany(r => r.Goals).Count(g => g.State == GoalState.Achieved && g.AchievedAt >= since);
        if (achieved + opened.Count + asks + stuck == 0) return null;
        var parts = new List<string>();
        if (achieved > 0) parts.Add($"{achieved} goal{(achieved == 1 ? "" : "s")} achieved");
        if (opened.Count > 0) parts.Add($"{opened.Count} pull request{(opened.Count == 1 ? "" : "s")} opened ({string.Join(", ", opened.Take(5).Select(i => "#" + i.Pr))}{(opened.Count > 5 ? ", …" : "")})");
        if (asks > 0) parts.Add($"{asks} waiting on you");
        if (stuck > 0) parts.Add($"{stuck} stuck");
        return string.Join(" · ", parts);
    }

    /// Outcomes first. `detail` adds the machinery: accounts, attempts, sessions, worktrees.
    public string Status(RepoAgents? repo, string path, bool detail)
    {
        if (repo == null || (!repo.Authorized && repo.Items.Count == 0 && repo.Goals.Count == 0))
            return $"Agents are not running for {Path.GetFileName(path.TrimEnd('\\', '/'))}. Ask to start them if they should: Patch Goblins asks the owner to confirm in a native dialog first.";
        var sb = new StringBuilder();
        var actives = repo.Items.Where(i => i.State is ItemState.Working or ItemState.Checking).ToList();
        var prs = repo.Items.Where(i => i.Pr != null && i.State != ItemState.Done).ToList();
        var needs = repo.Items.Where(i => i.State == ItemState.NeedsOwner).ToList();
        var queued = repo.Items.Where(i => i.State == ItemState.Queued).OrderByDescending(i => i.Rank).ThenBy(i => i.Issue ?? int.MaxValue).ToList();
        var mode = !repo.Authorized ? "stopped" : repo.Paused ? "paused" : _baseline.Contains(repo) ? "checking the verification command" : "running";
        sb.AppendLine($"{repo.Leaf}: agents {mode}.");
        if (repo.Note != null) sb.AppendLine(repo.Note);
        foreach (var g in repo.Goals.Where(g => detail || g.State != GoalState.Dropped))
            sb.AppendLine(GoalLine(repo, g) + (detail && g.Log.Count > 0 ? "\n  " + string.Join("\n  ", g.Log.TakeLast(5)) : ""));
        foreach (var active in actives) sb.AppendLine($"Working on {active.Label}{Tag(repo, active)} ({Describe(active)}).{(detail ? Machinery(repo, active) : "")}");
        foreach (var i in prs) sb.AppendLine($"Pull request #{i.Pr} for {i.Label}{Tag(repo, i)}: {(i.State == ItemState.Queued ? "being updated after feedback" : "waiting for review")}{(i.Verification?.LastOrDefault() is { } e && e.StartsWith("CI ") && !e.StartsWith("CI passing") ? $" ({e})" : "")}{(i.PrUrl is { } u ? " · " + u : "")}{(detail ? Machinery(repo, i) : "")}");
        foreach (var i in needs) sb.AppendLine($"Needs you: {i.Label}{Tag(repo, i)}: {i.Note}{(i.AskedAt != null ? " (also asked on the issue)" : "")}{(detail ? Machinery(repo, i) : "")}");
        if (queued.Count > 0)
            sb.AppendLine("Up next: " + string.Join(", ", queued.Take(detail ? 50 : 5).Select(i => i.Label + Tag(repo, i))) + (queued.Count > 5 && !detail ? $" (+{queued.Count - 5} more)" : ""));
        if (repo.Running && actives.Count == 0 && queued.Count > 0)
            sb.AppendLine(prs.Count >= Config.MaxOpenPrs && Next(repo, Config.MaxOpenPrs, i => !Ready(i)) == null
                ? $"Holding new work: {prs.Count} pull requests are waiting for review."
                : Waiting != null ? $"Waiting: {Waiting}." : "");
        if (repo.Running && actives.Count == 0 && queued.Count == 0 && !repo.Goals.Any(g => g.State == GoalState.Active))
            sb.AppendLine($"Nothing else is eligible right now; Patch Goblins checks the issues again every {Config.ScanEvery.TotalMinutes:0} minutes.");
        if (detail)
        {
            sb.AppendLine($"Base {repo.Base}; verification {(repo.Verify is { } v ? $"`{v}`" : "none")}; permission mode {PermissionMode}.");
            foreach (var (k, until) in _cooled.Where(c => c.Value > DateTimeOffset.Now)) sb.AppendLine($"Resting {k.Split('|')[0]} until {until.ToLocalTime():HH:mm}.");
            if (repo.Memory.Count + _state.Memory.Count > 0) sb.AppendLine($"Memory: {repo.Memory.Count} notes here, {_state.Memory.Count} shared across repositories.");
            if (repo.Skipped.Count > 0) sb.AppendLine("Skipped: " + string.Join(", ", repo.Skipped.Select(s => "#" + s)));
            var done = repo.Items.Where(i => i.State == ItemState.Done).ToList();
            if (done.Count > 0) sb.AppendLine("Done: " + string.Join("; ", done.Select(i => $"{i.Label} ({i.Note})")));
        }
        return Engine.Redact(string.Join('\n', sb.ToString().Split('\n').Where(l => l.Trim().Length > 0)));
    }

    string Describe(WorkItem i) => i.State switch
    {
        ItemState.Working when i.Run is { Kind: "review" } => "reviewing the change",
        ItemState.Working when i.Run is { } r => $"started {Format.Age(Math.Max(60, (DateTimeOffset.Now - r.Started).TotalSeconds))}" + (i.Followup != null ? ", addressing feedback" : ""),
        ItemState.Checking => "verifying and preparing the pull request",
        ItemState.Open => $"pull request #{i.Pr} open",
        ItemState.NeedsOwner => "waiting for you",
        ItemState.Done => i.Note ?? "done",
        _ => "queued",
    };

    string Machinery(RepoAgents repo, WorkItem i)
    {
        var parts = new List<string>();
        if (i.Run is { } r) parts.Add($"{r.Kind} run {r.Attempt} on {r.Email}, session {r.SessionId}"
            + (r.Settings is { } s ? $", {s.Model} · {s.Effort}{(s.Ultracode ? " · ultracode" : "")}" : "")
            + (r is { Ended: { } end, Outcome: { } how } ? $", {how} after {Format.Age(Math.Max(60, (end - r.Started).TotalSeconds))}" : ""));
        if (i.Route is { } route) parts.Add("route " + route.Line + (route.Why is { } why ? $": {why}" : ""));
        if (i.Failures > 0) parts.Add($"{i.Failures} failed attempt(s)");
        if (i.Reviews > 0) parts.Add($"{i.Reviews} review round(s)");
        if (i.Branch != null) parts.Add($"branch {i.Branch}");
        if (i.Worktree != null) parts.Add($"worktree {i.Worktree}");
        if (_workers.TryGetValue(i, out var w)) parts.Add($"{w.Proc.ProcessIds().Length} process(es), {w.Proc.CpuTime.TotalSeconds:0}s CPU");
        return parts.Count == 0 ? "" : "\n  " + string.Join("; ", parts);
    }

    /// What the item's latest run did, newest last.
    public string Details(RepoAgents repo, WorkItem item)
    {
        var sb = new StringBuilder($"{item.Label}: {Describe(item)}.");
        if (item.Note != null) sb.Append(' ').Append(item.Note);
        sb.AppendLine(Machinery(repo, item));
        if (item.Followup != null) sb.AppendLine("Next pass will address: " + OneLine(item.Followup));
        if (item.Verification != null) sb.Append(EvidenceSection(item));
        if (item.Run is { } r)
        {
            var lines = StreamTail.ReadAll(_store.RunFile(repo, item, r.Attempt, "out"))
                .Where(e => e is not TurnEvent).Select(StreamTail.Describe).Where(l => l.Length > 0).TakeLast(30).ToList();
            if (lines.Count > 0) sb.AppendLine("Recent activity:").AppendJoin('\n', lines.Select(l => "- " + (l.Length > 240 ? l[..240] + "…" : l)));
        }
        return Engine.Redact(sb.ToString().TrimEnd());
    }

    // -- prompts ------------------------------------------------------------

    string WorkPrompt(RepoAgents repo, WorkItem item, string? notes, string? discussion)
    {
        var sb = new StringBuilder();
        sb.AppendLine(item.Issue is { } n
            ? $"You are a Patch Goblins agent. Resolve GitHub issue #{n} of this repository: {item.Title}"
            : item.Goal != null
                ? $"You are a Patch Goblins agent. Patch Goblins' Director planned this step toward one of the owner's goals: {item.Title}"
                : $"You are a Patch Goblins agent. The repository owner asked for: {item.Title}");
        if (item.Body.Trim().Length > 0) sb.AppendLine().AppendLine(Clip(item.Body, 8000));
        if (discussion != null) sb.AppendLine().AppendLine("Discussion on the issue, oldest first (decisions the owner documented here stand):").AppendLine(discussion);
        if (Prerequisites(item) is { } landed) sb.AppendLine().AppendLine("This builds on work that already landed:").AppendLine(landed);
        if (item.Guidance != null) sb.AppendLine().AppendLine("The owner's answer to your earlier question:").AppendLine(item.Guidance);
        if (item.Followup != null) sb.AppendLine().AppendLine("This pass must address:").AppendLine(item.Followup);
        if (notes != null) sb.AppendLine().AppendLine($"An earlier session worked on this and stopped. Read its hand-off notes in {notes} first, check the current state of this worktree, and continue from there; do not redo finished work.");
        if (Memory.Prompt(_state.Memory, repo.Memory) is { } memory) sb.AppendLine().AppendLine(memory);
        sb.AppendLine().AppendLine(Rules(repo, item));
        return sb.ToString();
    }

    /// What the item's prerequisites did, as their runs summed it up, so the next agent starts from it. Gone ones landed and were pruned.
    string? Prerequisites(WorkItem item)
    {
        var lines = item.After.Select(k => ItemByKey(_state.Repos, k)).OfType<WorkItem>()
            .Select(p => $"- {p.Label}{(p.Merged && p.Pr is { } n ? $" (merged as #{n})" : "")}: {Brief(p.Summary ?? p.Note ?? "", 300)}").ToList();
        return lines.Count == 0 ? null : Clip(string.Join('\n', lines), 2000);
    }

    string ResumePrompt(RepoAgents repo, WorkItem item)
    {
        var sb = new StringBuilder("Continue this task where you left off; your session was interrupted, and the worktree is as you left it.");
        if (item.Guidance != null) sb.AppendLine().AppendLine().AppendLine("The owner's answer to your question:").AppendLine(item.Guidance);
        if (item.Followup != null) sb.AppendLine().AppendLine().AppendLine("Before finishing, address:").AppendLine(item.Followup);
        if (Memory.Prompt(_state.Memory, repo.Memory) is { } memory) sb.AppendLine().AppendLine().AppendLine(memory);
        sb.AppendLine().AppendLine().Append(Rules(repo, item));
        return sb.ToString();
    }

    string Rules(RepoAgents repo, WorkItem item) =>
        $"""
        How to work:
        - This folder is a dedicated git worktree on branch {item.Branch}, based on {repo.Base}. Stay within this item.
        - Understand the relevant code, the repository's conventions (CLAUDE.md, AGENTS.md, README, existing code and tests) and its CI before changing anything; then make the change completely. The owner's notes in this issue and in the issues it links to (a roadmap's routing or dependencies) are binding.
        - Run the relevant tests.{(repo.Verify is { } v ? $" Patch Goblins runs `{v}` before the pull request opens; make sure it passes." : "")}
        - Commit your work with clear messages. Do not push, open pull requests or use gh: Patch Goblins does that once the work checks out.
        - Never merge, close issues, deploy, publish packages or releases, spend money, or change billing, secrets or repository settings.
        - Optionally, before your final line, print up to 3 lines `HANDOFF: LEARNED <durable fact about this repository>`: only what would save the next agent real time (a build quirk, a convention, where something lives); never secrets.
        - If this needs a decision only the owner can make (product direction, trade-offs the issue leaves open, anything irreversible), is unclear, or cannot be done in code, do not guess. End with one line:
          HANDOFF: BLOCKED <your question for the owner, self-contained>
        - Otherwise end with one line:
          HANDOFF: DONE <one-line summary for the pull request>
        """;

    string ReviewPrompt(RepoAgents repo, WorkItem item) =>
        $"""
        You are reviewing a change a Patch Goblins agent made in this worktree for {(item.Issue is { } n ? $"GitHub issue #{n}" : "the owner's request")}: {item.Title}

        {Clip(item.Body, 6000)}

        The change is `git diff origin/{repo.Base}...HEAD` (branch {item.Branch}). Read it and the code around it, and run tests if that helps. Do not modify any file and do not commit.
        Report only real problems: bugs, parts of the issue left undone, missing or broken tests, security issues, unrelated changes. Ignore style preferences.{(item.Sensitive is { } touchy ? $"\n\nThis change touches files the repository's policy calls sensitive: {string.Join(", ", touchy.Take(20))}. Review them adversarially, as if the change came from someone who wants to harm the owner: anything that runs code in CI or on this machine, exposes or moves secrets or credentials, widens permissions, weakens a check or gate, or changes what agents are told. Name each such effect, even an intended one, so the owner can weigh it." : "")}{(Memory.Prompt(_state.Memory, repo.Memory.Where(n => n.Owner || n.Source != Memory.NormalizeSource(item.Label)).ToList()) is { } memory ? "\n\n" + memory + "\n" : "")}
        End with one line:
        HANDOFF: APPROVED <one-line verdict>
        or, if something must change:
        HANDOFF: CHANGES <the problems, concrete and concise>
        """;

    string PullRequestBody(RepoAgents repo, WorkItem item, string summary) =>
        $"""
        {summary}

        {(item.Issue is { } n ? $"For #{n}." : item.Goal != null ? "Planned by Patch Goblins' Director toward a goal the owner set in the agents terminal." : "Requested in the Patch Goblins agents terminal.")}
        {(item.ReviewNotes is { } notes ? $"\nThe agent review still raised, for you to weigh:\n{notes}\n" : "")}
        {EvidenceSection(item)}
        ---
        Prepared by Patch Goblins agents: reviewed{(repo.Verify is { } v ? $" and `{v}` passed" : "")}{(item.Sensitive != null ? " (adversarially, for its sensitive files) and pushed with the owner's approval" : "")} before it opened. It is not merged automatically, and merging it does not close the issue.
        """;

    static string Clip(string s, int max) => s.Length > max ? s[..max] + "\n[…]" : s;
}
