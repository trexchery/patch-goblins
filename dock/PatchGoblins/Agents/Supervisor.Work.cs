using System.Text.Json;
using System.Text.RegularExpressions;

namespace PatchGoblins;

// Git and GitHub: worktrees, the review and verification gates, pushing, pull requests, issues.
public sealed partial class Supervisor
{
    async Task<CliResult> GitAsync(string cwd, params string[] args)
    {
        // One git command at a time: worktree, fetch and ref updates share the repository's locks.
        await _gitGate.WaitAsync();
        // The supervisor's git runs with the owner's credentials: never with hooks a worker could have
        // written into the repository.
        try { return await _cli.GitAsync(cwd, ["-c", "core.hooksPath=" + NoHooks, "-c", "core.fsmonitor=false", .. args]); }
        finally { _gitGate.Release(); }
    }

    string NoHooks
    {
        get
        {
            var dir = Path.Combine(_store.Root, "no-hooks");
            Directory.CreateDirectory(dir);
            return dir.Replace('\\', '/');
        }
    }

    async Task<bool> HasOriginAsync(RepoAgents repo) => (await GitAsync(repo.Path, "remote", "get-url", "origin")).Ok;

    /// A fetch that failed for want of a network; a deleted base or revoked credentials are the owner's to fix.
    internal static readonly Regex Offline = new(@"resolve host|timed out|timeout|refused|unreachable|connection reset|failed to connect|error connecting|no such host|no route", RegexOptions.IgnoreCase);

    async Task<string> StartPointAsync(RepoAgents repo, bool strict = false)
    {
        if (!await HasOriginAsync(repo)) return repo.Base;
        // Strict always fetches itself: a fetch already under way may fail and still count as recent.
        if (strict || Due(_fetched.GetValueOrDefault(repo), TimeSpan.FromMinutes(2)))
        {
            var fetch = await GitAsync(repo.Path, "fetch", "--quiet", "origin", repo.Base);
            if (strict && !fetch.Ok) throw new AgentsException($"could not fetch origin/{repo.Base}: {fetch.Why}", transient: Offline.IsMatch(fetch.Err + fetch.Out)); // a stale base proves nothing
            _fetched[repo] = DateTimeOffset.Now;
        }
        return "origin/" + repo.Base;
    }

    /// The item's own worktree on its own branch, created on first use and kept until the work is done.
    async Task<string> PrepareWorktreeAsync(RepoAgents repo, WorkItem item)
    {
        if (!Directory.Exists(repo.Path)) throw new AgentsException($"{repo.Path} no longer exists");
        var start = await StartPointAsync(repo);
        var wt = item.Worktree ?? _store.Worktree(repo, item);
        // Git forgot the worktree (`git worktree prune` elsewhere): a run there sees no repository and can only ask the
        // owner, who cannot fix it. Its files move aside (uncommitted work survives) and the branch is checked out afresh.
        if (File.Exists(Path.Combine(wt, ".git")) && !(await GitAsync(wt, "rev-parse", "--absolute-git-dir")).Ok)
            Directory.Move(wt, $"{wt}.orphaned-{DateTime.Now:yyyyMMdd-HHmmss}");
        if (!File.Exists(Path.Combine(wt, ".git")))
        {
            await GitAsync(repo.Path, "worktree", "prune");
            item.Branch ??= $"patch-goblins/{item.Id}-{Slug(item.Title)}";
            var exists = (await GitAsync(repo.Path, "rev-parse", "--verify", "--quiet", "refs/heads/" + item.Branch)).Ok;
            var r = exists
                ? await GitAsync(repo.Path, "worktree", "add", wt, item.Branch)
                : await GitAsync(repo.Path, "worktree", "add", "-b", item.Branch, wt, start);
            if (!r.Ok) throw new AgentsException("could not create a worktree: " + r.Why);
            item.Worktree = wt;
            Save();
        }
        else
        {
            // A run killed mid-commit leaves the index locked; nothing else uses this worktree now.
            var gitDir = (await GitAsync(wt, "rev-parse", "--absolute-git-dir")).Out.Trim();
            if (gitDir.Length > 0) File.Delete(Path.Combine(gitDir, "index.lock"));
        }
        if (item.Pr != null && start.StartsWith("origin/"))
        {
            // Pick up anything pushed to the pull request since (the owner's own commits included),
            // and the base, which a conflict fix merges.
            await GitAsync(repo.Path, "fetch", "--quiet", "origin", item.Branch!, repo.Base);
            await GitAsync(wt, "merge", "--ff-only", "--quiet", "origin/" + item.Branch);
        }
        return wt;
    }

    /// A reviewer must leave the change exactly as it found it.
    async Task RestoreAfterReviewAsync(string wt, string head)
    {
        if (head.Length == 0 || !Directory.Exists(wt)) return;
        var now = (await GitAsync(wt, "rev-parse", "HEAD")).Out.Trim();
        if (now != head || (await GitAsync(wt, "status", "--porcelain", "--untracked-files=no")).Out.Trim().Length > 0)
            await GitAsync(wt, "reset", "--hard", "--quiet", head);
    }

    internal static string Slug(string title)
    {
        var s = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "work" : s.Length > 40 ? s[..40].TrimEnd('-') : s;
    }

    /// After a run says it is done: commit what it left modified, verify, have it reviewed, then push
    /// and open or update the pull request. Every step is idempotent, so a restart in the middle simply
    /// runs it again.
    async Task CheckAsync(RepoAgents repo, WorkItem item)
    {
        // Paused or stopped: the check waits (in Checking) and runs again on resume.
        if (_disposed || item.State != ItemState.Checking || !Active(repo) || !_checks.Add(item)) return;
        var summary = item.Summary ?? item.Title;
        try
        {
            var wt = item.Worktree is { } w && Directory.Exists(w) ? w : throw new AgentsException("its worktree is gone");
            if ((await GitAsync(wt, "status", "--porcelain", "--untracked-files=no")).Out.Trim().Length > 0)
            {
                await GitAsync(wt, "add", "--update");
                var c = await GitAsync(wt, "commit", "--quiet", "-m", item.Label);
                if (!c.Ok) throw new AgentsException("could not commit: " + c.Why);
            }
            var start = await StartPointAsync(repo, strict: true); // the base and its policy as they are now
            if ((await GitAsync(wt, "rev-list", "--count", start + "..HEAD")).Out.Trim() == "0")
            {
                Park(repo, item, NoChange + ": " + summary);
                return;
            }
            if (!Active(repo)) return; // closed meanwhile: checked again on the next open
            if (await PolicyAtAsync(repo, item, start) == null) return;
            var files = await ChangedFilesAsync(wt, start);
            if (Policy.SkipsCi((await GitAsync(wt, "log", "--format=%B", start + "..HEAD")).Out))
            {
                item.Followup = $"A commit message on this branch tells GitHub to skip CI ([skip ci], [ci skip], [no ci], [skip actions] or skip-checks: true), so its checks would prove nothing. Rewrite the branch's commit messages without it (for example `git reset --soft {start}` and commit again).";
                Charge(repo, item, "a commit skips CI");
                return;
            }
            var head = (await GitAsync(wt, "rev-parse", "HEAD")).Out.Trim();
            var (selected, elsewhere) = Policy.Select(repo.Checks, files, CheckShell.Os);
            var commands = (repo.Verify is { Length: > 0 } v ? [v] : new List<string>()).Concat(selected.Select(c => c.Run)).Distinct().ToList();
            var evidence = new List<string>();
            foreach (var command in commands)
            {
                var verified = await VerifyAsync(repo, item, wt, command);
                if (Dropped(repo, item)) return; // skipped during verification
                if (_interrupted.Remove(item)) return; // its terminal closed: verified again on the next open
                if (verified is { } failure)
                {
                    item.Followup = $"Patch Goblins ran `{command}` in this worktree and it failed:\n{failure}";
                    Escalate(repo, item, "verify");
                    Charge(repo, item, $"`{command}` failed");
                    return;
                }
                evidence.Add($"passed: `{command}` at {head[..Math.Min(7, head.Length)]}");
            }
            evidence.AddRange(elsewhere.Select(c => $"not run here: `{c.Run}` needs {c.Os}"));
            if (commands.Count == 0) evidence.Add($"unverified: no verification command covers these changes; only CI can show they work at {head[..Math.Min(7, head.Length)]}");
            item.Verification = evidence;
            // Verification takes time: the base, its pin and its checks are judged again, and its policy decides what follows.
            if (await PolicyAtAsync(repo, item, await StartPointAsync(repo, strict: true)) is not { } policy) return;
            var touchy = Policy.SensitiveIn(policy, files)
                .Concat((await ChangedFilesAsync(wt, start, "-G", Policy.SecretPattern)).Select(f => f + " (a token- or key-shaped string)")).ToList();
            if (touchy.Except(item.Sensitive ?? []).Any()) item.ReviewedHead = null; // newly sensitive: the review so far was not adversarial for it
            item.Sensitive = touchy.Count > 0 ? touchy : null;
            // The review and the owner's yes are for one commit: any other head is reviewed again, and a new review needs a new yes.
            if (item.ReviewedHead != head)
            {
                // A fresh, read-only session reviews the change before anyone else sees it.
                item.PushApproved = false;
                item.ReviewNext = true;
                item.State = ItemState.Queued;
                return;
            }
            if (!item.PushApproved)
            {
                if (item.ReviewNotes is { } notes && Config.AskApproval)
                {
                    // One yes covers both, so the owner sees both.
                    Hold(repo, item, "review", "two review passes still found problems; answer to push it as is, or say what to change:\n" + notes
                        + (item.Sensitive is { } also ? $"\nIt also touches what the repository's policy keeps for you: {string.Join(", ", also.Take(10))}" : ""));
                    return;
                }
                // A push runs the branch's own workflows with the repository's secrets, before anyone reviews it; the policy's
                // other sensitive paths change what runs on this machine, what agents are told, or hold secrets.
                if (item.Sensitive is { } sensitive)
                {
                    Hold(repo, item, "sensitive", $"the change touches {string.Join(", ", sensitive.Take(5))}{(sensitive.Count > 5 ? $" and {sensitive.Count - 5} more" : "")}, which the repository's policy keeps for you (workflows run with the repository's secrets once pushed); answer to push it, or say what to change");
                    return;
                }
                if (policy.Push == "ask" && Config.AskApproval)
                {
                    Hold(repo, item, "push", "this repository's policy asks before every push (a push starts its remote CI); answer to push it, or say what to change");
                    return;
                }
            }
            if (!start.StartsWith("origin/"))
            {
                item.State = ItemState.Done;
                item.Note = $"ready on branch {item.Branch} (no remote to push to)";
                return;
            }
            if (!Active(repo) || Dropped(repo, item)) return; // paused, stopped, closed or skipped during verification
            // Exactly the commit verified, reviewed and approved, whatever the branch points at now.
            var push = await GitAsync(wt, "push", "--quiet", "origin", $"{head}:refs/heads/{item.Branch}");
            if (!push.Ok)
            {
                if (push.Err.Contains("non-fast-forward") || push.Err.Contains("fetch first"))
                {
                    item.Followup = $"The branch on GitHub has commits this worktree lacks. Patch Goblins fetched them: merge origin/{item.Branch} and resolve any conflicts.";
                    item.State = ItemState.Queued;
                    return;
                }
                throw new AgentsException("could not push: " + push.Why, transient: Offline.IsMatch(push.Err + push.Out));
            }
            await GitAsync(wt, "branch", "--quiet", "--set-upstream-to=origin/" + item.Branch); // as `push -u` would: the doctor sees it pushed
            // Pushing is idempotent: a pause now leaves it in Checking, and resuming opens the pull request.
            if (!Active(repo) || Dropped(repo, item)) return;
            if (item.Pr == null) await OpenPullRequestAsync(repo, item, wt, summary);
            else await RecordEvidenceAsync(repo, item);
            item.State = ItemState.Open;
            item.Followup = null;
            item.Reviews = 0;
            item.Failures = 0;
            (item.PushApproved, item.ReviewNotes, item.HoldFor) = (false, null, null);
            item.Note = summary;
        }
        catch (Exception e)
        {
            // Offline: the check runs again after a pause (the pump waits for it). Anything else is parked: a check that throws must not be retried in a loop.
            if (e is AgentsException { Transient: true })
            {
                _notBefore[item] = DateTimeOffset.Now + Config.LaunchBackoff;
                item.Note = Engine.Redact(e.Message);
            }
            else
            {
                if (e is not AgentsException) Program.Log(e);
                Park(repo, item, e is AgentsException ? e.Message : "the check could not run: " + e.Message);
            }
        }
        finally
        {
            _checks.Remove(item);
            Save();
            Changed?.Invoke();
            Pump();
        }
    }

    /// The base's policy at `start`, never the worktree's (a change cannot relax the rules it is judged by); null when it
    /// stops this item (unreadable: parked) or this repository (PolicyHoldsAsync: paused).
    async Task<RepoPolicy?> PolicyAtAsync(RepoAgents repo, WorkItem item, string start)
    {
        RepoPolicy policy;
        try { policy = await Policy.LoadAsync(_cli, repo.Path, start); }
        catch (PolicyException e) { Park(repo, item, "the repository's policy is unreadable, so nothing is pushed: " + e.Message); return null; }
        return await PolicyHoldsAsync(repo, policy, start) ? policy : null;
    }

    /// Files the branch's own commits since `start` change (`filter`: only those whose changes match, as `-G regex`), in any
    /// commit, merges included, not only the last tree: a secret added and removed again is still in what a push uploads,
    /// and the base's own changes are not the branch's. Each path as it is (a rename is its old and its new path, no name
    /// comes back quoted), every file read as text (an attribute cannot hide one). A list that cannot be read stops the check.
    async Task<List<string>> ChangedFilesAsync(string wt, string start, params string[] filter)
    {
        var r = await GitAsync(wt, ["log", "--format=", "--name-only", "--no-renames", "--diff-merges=first-parent", "--text", "--no-textconv", "--no-ext-diff", "-z", .. filter, start + "..HEAD"]);
        return r.Ok ? [.. r.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : throw new AgentsException("could not list the changed files: " + r.Why);
    }

    /// Runs the repository's verification command in the worktree; null when it passes, else its tail.
    async Task<string?> VerifyAsync(RepoAgents repo, WorkItem item, string wt, string verify)
    {
        var dir = _store.RunDir(repo, item);
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, $"verify-{item.Run?.Attempt ?? 0}.log");
        var empty = Path.Combine(dir, "empty");
        File.WriteAllText(empty, "");
        File.WriteAllText(log, "");
        using var proc = AgentProcess.StartShell(verify, wt, $@"Local\patch-goblins-verify-{repo.Key}-{item.Id}-{Guid.NewGuid().ToString("N")[..6]}", empty, log,
            new Dictionary<string, string?>(WorkerEnvironment(Path.Combine(_store.Root, "no-gh"))) { ["CI"] = "1" });
        _verifying[item] = (repo, proc);
        try { await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(Config.VerifyTimeout)); }
        finally { _verifying.Remove(item); }
        if (!proc.HasExited)
        {
            await proc.StopAsync(TimeSpan.Zero);
            return $"(timed out after {Config.VerifyTimeout.TotalMinutes:0} minutes)\n" + TailOf(log, 3000);
        }
        await proc.ReapAsync();
        return proc.ExitCode == 0 ? null : TailOf(log, 3000);
    }

    async Task OpenPullRequestAsync(RepoAgents repo, WorkItem item, string wt, string summary)
    {
        var existing = await _cli.GhAsync(wt, "pr", "list", "--head", item.Branch!, "--state", "open", "--json", "number,url");
        if (existing.Ok && ParsePrList(existing.Out) is { } found)
        {
            (item.Pr, item.PrUrl) = found;
            item.PrSeen ??= DateTimeOffset.Now;
            item.PrOpened ??= DateTimeOffset.Now;
            return;
        }
        var body = Path.Combine(_store.RunDir(repo, item), "pr-body.md");
        File.WriteAllText(body, PullRequestBody(repo, item, summary));
        string[] create = ["pr", "create", "--base", repo.Base, "--head", item.Branch!, "--title", item.Title, "--body-file", body];
        var r = await _cli.GhAsync(wt, Config.DraftPrs ? [.. create, "--draft"] : create);
        var draft = Config.DraftPrs && r.Ok;
        // GitHub Free has no drafts on private repositories: a ready pull request rather than none.
        if (Config.DraftPrs && !r.Ok && (r.Err + r.Out).Contains("draft", StringComparison.OrdinalIgnoreCase)) r = await _cli.GhAsync(wt, create);
        if (!r.Ok || Regex.Match(r.Out, @"https://\S+/pull/(\d+)") is not { Success: true } m)
            throw new AgentsException("could not open a pull request: " + r.Why, transient: Offline.IsMatch(r.Err + r.Out));
        item.Pr = int.Parse(m.Groups[1].Value);
        item.PrUrl = m.Value;
        item.PrSeen = item.PrOpened = DateTimeOffset.Now;
        Notify?.Invoke(draft ? "Draft pull request opened" : "Pull request ready", $"{repo.Leaf} #{item.Pr} · {item.Title}");
    }

    /// A follow-up pushed to an open pull request: what verified this commit goes on the record there too.
    async Task RecordEvidenceAsync(RepoAgents repo, WorkItem item)
    {
        var body = Path.Combine(_store.RunDir(repo, item), "evidence.md");
        File.WriteAllText(body, "Patch Goblins pushed a follow-up.\n\n" + EvidenceSection(item));
        var r = await _cli.GhAsync(repo.Path, "pr", "comment", item.Pr!.Value.ToString(), "--body-file", body);
        if (!r.Ok) Program.Log($"gh pr comment {item.Pr}: exit {r.Code}: {r.Why}"); // the push stands; the record waits for the next one
    }

    internal static string EvidenceSection(WorkItem item) =>
        "Verification:\n" + string.Join("\n", (item.Verification ?? ["unverified: no record"]).Select(e => "- " + e)) + "\n";

    internal static (int, string)? ParsePrList(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var pr in doc.RootElement.EnumerateArray())
                return (pr.GetProperty("number").GetInt32(), pr.GetProperty("url").GetString() ?? "");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
        return null;
    }

    /// Parks an item the owner has to clear before Patch Goblins pushes it.
    void Hold(RepoAgents repo, WorkItem item, string kind, string why)
    {
        Park(repo, item, why);
        item.HoldFor = kind;
    }

    /// A finished run that changed nothing: the owner's call (close the issue or retry), not a failure.
    const string NoChange = "finished without changing anything";

    void Park(RepoAgents repo, WorkItem item, string why)
    {
        if (Dropped(repo, item)) return;
        item.State = ItemState.NeedsOwner;
        item.Note = Engine.Redact(why);
        Notify?.Invoke("Agents need you", $"{repo.Leaf} · {item.Label}: {item.Note}");
    }

    const string QuestionMark = "<!-- patch-goblins-agents -->", OldQuestionMark = "<!-- autodock-agents -->", HandoffQuestionMark = "<!-- handoff-agents -->"; // the two before it: asked before the renames

    /// A decision only the owner can make: park the item, ask where the owner already works (the issue),
    /// and move on. An answer there, or in the agents terminal, brings it back.
    async Task AskOwnerAsync(RepoAgents repo, WorkItem item, string question)
    {
        item.State = ItemState.NeedsOwner;
        item.Note = question;
        item.AskedAt = DateTimeOffset.Now;
        Notify?.Invoke("Agents need you", $"{repo.Leaf} · {item.Label}: {question}");
        if (item.Issue is not { } n) return;
        var body = Path.Combine(_store.RunDir(repo, item), "question.md");
        File.WriteAllText(body,
            "Patch Goblins agents set this issue aside for a decision:\n\n" +
            string.Join("\n", question.Split('\n').Select(l => "> " + l)) +
            "\n\nReply here and the work picks up from where it stopped.\n" + QuestionMark + "\n");
        await _cli.GhAsync(repo.Path, "issue", "comment", n.ToString(), "--body-file", body);
    }

    /// The issue's discussion (the owner's documented decisions live there), oldest first.
    async Task<string?> IssueDiscussionAsync(RepoAgents repo, int issue)
    {
        var r = await _cli.GhAsync(repo.Path, "issue", "view", issue.ToString(), "--json", "comments");
        if (!r.Ok) return null;
        var lines = IssueInfo.Comments(r.Out, DateTimeOffset.MinValue, _me).TakeLast(15).ToList();
        return lines.Count == 0 ? null : Clip(string.Join("\n\n", lines), 8000);
    }

    /// Pull requests nobody is working on right now, whatever the item waits for.
    static bool Watched(WorkItem i) => i.Pr != null && i.State is ItemState.Open or ItemState.NeedsOwner or ItemState.Queued;

    /// Owner-visible results: merged or closed pull requests finish their items (parked ones too);
    /// failing checks, conflicts and new review comments send an open one back for another pass.
    async Task WatchPullRequestsAsync(RepoAgents repo)
    {
        _prPolled[repo] = DateTimeOffset.Now;
        foreach (var item in repo.Items.Where(Watched).ToList())
        {
            var seen = DateTimeOffset.Now;
            var r = await _cli.GhAsync(repo.Path, "pr", "view", item.Pr!.Value.ToString(), "--json", "state,mergeable,statusCheckRollup,comments,reviews,headRefOid");
            if (!r.Ok || PullRequestState.Parse(r.Out, item.PrSeen ?? DateTimeOffset.MinValue, _me) is not { } pr) continue;
            item.Verification = Evidence(item.Verification, pr);
            if (pr.State is "MERGED" or "CLOSED")
            {
                var merged = pr.State == "MERGED";
                item.Merged = merged;
                if (!merged && GoalOf(item.Goal) is { } g) g.Claim = null; // the claim may have counted on this change
                // An issue the Director ordered after this one would wait for it forever: it asks the owner (a goal's steps ask through Tend).
                if (!merged)
                    foreach (var d in repo.Items.Where(d => d is { Goal: null, Issue: not null, State: ItemState.Queued, Worktree: null, Pr: null } && d.After.Contains(Key(repo, item))))
                        (d.State, d.Note, d.After) = (ItemState.NeedsOwner, $"{SkipHold}{item.Label}, whose pull request was closed without merging; answer to run it anyway", [.. d.After.Where(k => k != Key(repo, item))]);
                Memory.Add(repo.Memory, merged
                    ? $"Merged #{item.Pr} ({item.Label}){(item.Summary is { } s ? ": " + s : "")}"
                    : $"Closed without merging: #{item.Pr} ({item.Label}). The owner did not want this change as made{(item.Summary is { } c ? ": " + c : "")}",
                    "pull request", false, DateTimeOffset.Now);
                await FinishAsync(repo, item, pr.State == "MERGED" ? $"pull request #{item.Pr} merged" : $"pull request #{item.Pr} closed without merging");
                continue;
            }
            if (item.State != ItemState.Open || pr.ChecksPending) continue; // act on a complete picture, once
            var asks = new List<string>();
            if (pr.FailedChecks.Count > 0)
            {
                var logs = "";
                if (pr.FailedRunId is { } run && await _cli.GhAsync(repo.Path, "run", "view", run, "--log-failed") is var l)
                {
                    if (l.Ok) logs = "\n" + Engine.Redact(Tail(l.Out, 4000));
                    else Program.Log($"gh run view {run} --log-failed: exit {l.Code}: {l.Why}"); // the follow-up goes without the log
                }
                asks.Add($"These checks failed: {string.Join(", ", pr.FailedChecks)}.{logs}");
            }
            if (pr.Conflicting) asks.Add($"The pull request conflicts with {repo.Base}. Patch Goblins fetched it: merge origin/{repo.Base} into this branch and resolve the conflicts.");
            // Newest first: GitHub lists the oldest page first. shortcut: the newest 100 per poll, paginate if a review ever leaves more between polls.
            var inline = await _cli.GhAsync(repo.Path, "api", "-X", "GET", $"repos/{{owner}}/{{repo}}/pulls/{item.Pr}/comments", "-f", "sort=created", "-f", "direction=desc", "-f", "per_page=100");
            if (!inline.Ok) continue; // PrSeen must not pass comments nobody read: the next poll tries again
            var feedback = pr.Feedback.Concat(PullRequestState.InlineComments(inline.Out, item.PrSeen ?? DateTimeOffset.MinValue, _me).Reverse()).ToList();
            if (feedback.Count > 0) asks.Add("Review feedback to address:\n" + Engine.Redact(string.Join("\n\n", feedback)));
            if (asks.Count == 0) continue;
            item.PrSeen = seen;
            item.Followups++;
            if (item.Followups > MaxFollowups)
            {
                Park(repo, item, $"pull request #{item.Pr} still needs work after {MaxFollowups} passes");
                continue;
            }
            item.Followup = string.Join("\n\n", asks);
            item.State = ItemState.Queued;
            if (pr.FailedChecks.Count > 0) Escalate(repo, item, "verify"); // the owner's comments are requests, not failures
        }
        Save();
    }

    /// Done for good: the worktree and local branch go; the pull request and remote branch stay.
    async Task FinishAsync(RepoAgents repo, WorkItem item, string how)
    {
        item.State = ItemState.Done;
        item.Note = how;
        item.Followup = null;
        // A verification still running there would keep the worktree from going, and run on for nothing.
        if (_verifying.Remove(item, out var v)) await v.Proc.StopAsync(TimeSpan.FromSeconds(5));
        if (item.Worktree is { } wt)
        {
            await GitAsync(repo.Path, "worktree", "remove", "--force", wt);
            item.Worktree = null;
        }
        if (item.Branch is { } b) await GitAsync(repo.Path, "branch", "-D", b);
        try { Directory.Delete(_store.RunDir(repo, item), recursive: true); }
        catch (IOException) { }
        Save();
    }

    static string Tail(string s, int chars) => s.Length > chars ? s[^chars..] : s;

    /// The local record, then what GitHub says of the pull request's head: its CI, and whether that head is still the commit
    /// Patch Goblins verified (someone may have pushed since).
    internal static List<string> Evidence(List<string>? local, PullRequestState pr)
    {
        var list = (local ?? []).Where(e => !e.StartsWith("CI ") && !e.StartsWith("stale: ")).ToList();
        var mine = list.Select(e => Regex.Match(e, @"^(passed|unverified): .* at ([0-9a-f]{7,40})$")).LastOrDefault(m => m.Success)?.Groups[2].Value;
        var stale = pr.Head is { Length: >= 7 } head && mine != null && !head.StartsWith(mine);
        if (stale) list.Add($"stale: the pull request's head is now {pr.Head![..7]}, which Patch Goblins did not verify");
        list.Add(stale ? "CI on that unverified head: " + pr.Ci[3..] : pr.Ci); // green on someone else's commit is no evidence for this one
        return list;
    }

    // -- issue discovery ----------------------------------------------------

    string? _me;
    /// The owner's open issues as the last scan read them.
    readonly Dictionary<RepoAgents, List<IssueInfo>> _openIssues = [];

    /// Whether agents may take the issue: actionable, no open pull request has it, not skipped or routed elsewhere, not a goal.
    static bool WantedBy(RepoAgents repo, IssueInfo i, string? me, HashSet<int> open, HashSet<int> covered, HashSet<int> skip, HashSet<int>? only) =>
        i.Actionable(me, open) && !covered.Contains(i.Number) && !repo.Skipped.Contains(i.Number)
        && !skip.Contains(i.Number) && (only == null || only.Contains(i.Number))
        && repo.Goals.All(g => g.Issue != i.Number || g.State == GoalState.Dropped); // a goal is planned, not worked

    /// Picks up new actionable issues, drops queued ones that closed, and brings back items whose
    /// question was answered on the issue.
    async Task ScanAsync(RepoAgents repo)
    {
        repo.LastScan = DateTimeOffset.Now;
        // Offline is no problem to fix: the next scan reads them, and the dot stays dark meanwhile.
        if (_me == null && await _cli.GhAsync(repo.Path, "api", "user", "--jq", ".login") is var who)
        {
            if (who.Ok) _me = who.Out.Trim();
            else if (!Offline.IsMatch(who.Err + who.Out))
                repo.Note = "could not read issues: " + (Regex.IsMatch(who.Err + who.Out, "gh auth login|not logged", RegexOptions.IgnoreCase) ? "gh is not signed in (run `gh auth login`)" : who.Why);
        }
        if (_me is not { } me) { Save(); return; }
        var r = await _cli.GhAsync(repo.Path, "issue", "list", "--state", "open", "--limit", "100", "--json", "number,title,body,labels,assignees,author,blockedBy");
        if (!r.Ok && !Offline.IsMatch(r.Err + r.Out)) r = await _cli.GhAsync(repo.Path, "issue", "list", "--state", "open", "--limit", "100", "--json", "number,title,body,labels,assignees,author");
        if (!r.Ok) { if (!Offline.IsMatch(r.Err + r.Out)) repo.Note = "could not read issues: " + r.Why; Save(); return; }
        if (repo.Note?.StartsWith("could not") == true) repo.Note = null; // issues read, or a one-off baseline hiccup
        if (IssueInfo.ParseList(r.Out) is not { } issues) { repo.Note = "could not read issues: gh printed something unexpected"; Save(); return; }
        // What the Director sees of what waits on what. Only the owner's: anyone can open an issue on a public repository.
        _openIssues[repo] = issues.Where(i => i.Author.Equals(me, StringComparison.OrdinalIgnoreCase) || i.Assignees.Contains(me, StringComparer.OrdinalIgnoreCase)).ToList();
        var open = issues.Select(i => i.Number).ToHashSet();
        // Issues an open pull request already resolves are someone's work in progress.
        var covered = new HashSet<int>();
        if (await _cli.GhAsync(repo.Path, "pr", "list", "--state", "open", "--limit", "100", "--json", "number,closingIssuesReferences") is { Ok: true } prs)
            covered = IssueInfo.ClosedByOpenPullRequests(prs.Out);
        // The owner's own routing (a "Patch Goblins" section in an issue they wrote) narrows it further.
        var (skip, only) = IssueInfo.Routing(issues, me);
        bool Wanted(IssueInfo i) => WantedBy(repo, i, _me, open, covered, skip, only);
        var byNumber = issues.ToDictionary(i => i.Number);
        // The Director's unstarted picks (raised from below the owner's rank) are judged as a plan judges them: unwanted now,
        // they leave their goal and the line below takes them; what was planned on them asks the owner or leaves too.
        // A blocker that is an item, in any state, is left to `after`: Ready counts a merged one as landed, Tend flags one closed unmerged.
        var sequenced = open.Except(repo.Items.Select(i => i.Issue).OfType<int>()).ToHashSet();
        foreach (var i in repo.Items.Where(i => i is { State: ItemState.Queued, Worktree: null, Pr: null, Guidance: null, Goal: not null, OwnRank: < OwnerRank, Issue: { } n }
                                                && byNumber.TryGetValue(n, out var picked) && !WantedBy(repo, picked, _me, sequenced, covered, skip, only)).ToList())
        {
            if (GoalOf(i.Goal) is { } g) { AddLog(g, DateTimeOffset.Now, $"{i.Label} left: agents may no longer take it"); g.Claim = null; }
            Unplan(repo, i.Goal!, [i]);
            HoldDependents(_state.Repos, repo, i, "which agents may no longer take");
        }
        // Unstarted work found by scanning that is no longer wanted (now blocked, a tracker, routed elsewhere,
        // or someone else's pull request covers it) leaves the queue; work the owner named stays.
        var unwanted = repo.Items.Where(i => i is { State: ItemState.Queued, Worktree: null, Pr: null, Guidance: null } && i.Rank < OwnerRank && i.Goal == null
                                             && i.Issue is { } n && byNumber.TryGetValue(n, out var issue) && !Wanted(issue)).ToList();
        foreach (var i in unwanted)
        {
            repo.Items.Remove(i);
            HoldDependents(_state.Repos, repo, i, "which agents may no longer take"); // a missing predecessor reads as landed
        }

        // Closed issues: unstarted and parked work goes, and so does work under way that has no pull
        // request yet. A full page may not list every open issue, so only a complete list counts.
        bool Gone(WorkItem i) => i.Issue is { } n && !open.Contains(n) && i.State != ItemState.Checking
                                 && (i.State == ItemState.Done ? i.Worktree == null : i.Pr == null)
                                 && !(i.State == ItemState.Done && i.Goal != null); // a goal's finished work is its evidence
        if (issues.Count < 100)
        {
            foreach (var gone in repo.Items.Where(Gone).ToList())
            {
                if (!Gone(gone)) continue; // its run ended while an earlier one was stopping, and its check took over
                var landed = gone is { State: ItemState.Done } && (gone.Merged || gone.Pr == null); // closing the issue of work that merged changes nothing for what waits on it
                if (gone.State != ItemState.Done)
                {
                    await StopWorkerAsync(gone, "skipped");
                    await FinishAsync(repo, gone, "its issue was closed");
                }
                if (!landed && GoalOf(gone.Goal) is { } g) g.Claim = null; // ended unmerged: the claim may have counted on it
                repo.Items.Remove(gone);
                if (!landed) HoldDependents(_state.Repos, repo, gone, "whose issue was closed"); // a missing predecessor reads as landed
            }
            foreach (var g in repo.Goals.Where(g => g is { State: GoalState.Active or GoalState.NeedsOwner, Issue: { } n } && !open.Contains(n)).ToList())
                await EndGoalAsync(repo, g, GoalState.Dropped, $"its issue #{g.Issue} was closed");
        }
        foreach (var issue in issues)
        {
            var dependents = issues.Count(o => o.BlockedBy(open).Contains(issue.Number));
            if (repo.Items.FirstOrDefault(i => i.Issue == issue.Number) is { } known)
            {
                if (known.State == ItemState.Queued && known.Worktree == null)
                    (known.Title, known.Body) = (issue.Title, issue.Body);
                continue;
            }
            if (!Wanted(issue)) continue;
            // Work that unblocks other issues goes first.
            repo.Items.Add(new WorkItem { Id = issue.Number.ToString(), Issue = issue.Number, Title = issue.Title, Body = issue.Body, Rank = issue.Rank + 50 * dependents });
        }

        foreach (var item in repo.Items.Where(i => i is { State: ItemState.NeedsOwner, AskedAt: not null, Issue: not null }).ToList())
        {
            var v = await _cli.GhAsync(repo.Path, "issue", "view", item.Issue!.Value.ToString(), "--json", "comments");
            if (!v.Ok) continue;
            if (item.State != ItemState.NeedsOwner || item.AskedAt is not { } asked || Dropped(repo, item)) continue; // changed during the await
            var answers = IssueInfo.Comments(v.Out, asked, _me).Where(c => !c.Contains(QuestionMark) && !c.Contains(OldQuestionMark) && !c.Contains(HandoffQuestionMark)).ToList();
            if (answers.Count == 0) continue;
            item.Guidance = Engine.Redact(string.Join("\n\n", answers));
            Memory.Add(repo.Memory, $"#{item.Issue}: {item.Guidance}", "issue", true, DateTimeOffset.Now);
            (item.State, item.AskedAt, item.Failures, item.Note) = (ItemState.Queued, null, 0, null);
        }
        Save();
    }

    /// The owner named an issue the filters skipped: take it as is.
    async Task<WorkItem?> AddIssueAsync(RepoAgents repo, int number)
    {
        var r = await _cli.GhAsync(repo.Path, "issue", "view", number.ToString(), "--json", "number,title,body,labels,assignees,state");
        if (!r.Ok || IssueInfo.ParseList("[" + r.Out + "]") is not [var issue]) return null;
        if (repo.Items.FirstOrDefault(i => i.Issue == number) is { } scanned) return scanned; // a scan added it meanwhile
        var item = new WorkItem { Id = number.ToString(), Issue = number, Title = issue.Title, Body = issue.Body };
        repo.Items.Add(item);
        return item;
    }

    /// The owner's GitHub login, read once (a restart need not wait for the next scan).
    async Task<string?> MeAsync(RepoAgents repo) =>
        _me ??= (await _cli.GhAsync(repo.Path, "api", "user", "--jq", ".login")) is { Ok: true } me ? me.Out.Trim() : null;

    /// A clean detached checkout of the base as it is now (fetched), for a plan or a completion check, whatever a crash left there.
    async Task<string> ProbeWorktreeAsync(RepoAgents repo, WorkItem probe, bool strict = false)
    {
        if (!Directory.Exists(repo.Path)) throw new AgentsException($"{repo.Path} no longer exists");
        _fetched.Remove(repo);
        var start = await StartPointAsync(repo, strict);
        // Plans and completion checks run on the base too: only while its policy allows it (callers stop once agents pause).
        try { await PolicyHoldsAsync(repo, await Policy.LoadAsync(_cli, repo.Path, start), start); }
        catch (PolicyException e) { throw new AgentsException("the repository's policy is unreadable: " + e.Message); }
        var wt = _store.Worktree(repo, probe);
        await GitAsync(repo.Path, "worktree", "remove", "--force", wt);
        try { if (Directory.Exists(wt)) Directory.Delete(wt, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        await GitAsync(repo.Path, "worktree", "prune");
        var add = await GitAsync(repo.Path, "worktree", "add", "--detach", "--force", wt, start);
        return add.Ok ? wt : throw new AgentsException($"could not check out {start}: {add.Why}");
    }

    Task RemoveProbeAsync(RepoAgents repo, WorkItem probe) => GitAsync(repo.Path, "worktree", "remove", "--force", _store.Worktree(repo, probe));

    /// Before the first run of a newly started repository: the gate must pass on the base branch, or
    /// every item would fail it for reasons that are not its own.
    async Task VerifyBaselineAsync(RepoAgents repo)
    {
        if (repo.Verify is not { Length: > 0 } verify) return;
        if (!Active(repo)) { _rebaseline.Add(repo); return; } // its terminal closed first: on the next open
        _baseline.Add(repo);
        var probe = new WorkItem { Id = "baseline", Title = "baseline" };
        var wt = _store.Worktree(repo, probe);
        var again = false; // its terminal closed: on the next open, once this one has cleaned up after itself
        try
        {
            var start = await StartPointAsync(repo);
            try { if (!await PolicyHoldsAsync(repo, await Policy.LoadAsync(_cli, repo.Path, start), start)) return; } // paused: nothing runs on that base
            catch (PolicyException e) { repo.Note = "the verification command was not checked: the repository's policy is unreadable: " + e.Message; return; }
            await GitAsync(repo.Path, "worktree", "prune");
            var add = await GitAsync(repo.Path, "worktree", "add", "--detach", "--force", wt, start);
            if (!add.Ok) { repo.Note = "could not check the verification command: " + add.Why; return; }
            if (!Active(repo)) { again = true; return; } // closed meanwhile
            var result = await VerifyAsync(repo, probe, wt, verify);
            if (_interrupted.Remove(probe)) again = true;
            else if (result is { } failure)
            {
                repo.Paused = true;
                repo.Note = $"`{verify}` fails on {repo.Base} before any change, so agents are paused. Fix it or change the command, then resume.\n{failure}";
                Notify?.Invoke("Agents paused", $"{repo.Leaf}: `{verify}` fails on {repo.Base}");
            }
        }
        finally
        {
            await GitAsync(repo.Path, "worktree", "remove", "--force", wt);
            try { Directory.Delete(_store.RunDir(repo, probe), recursive: true); } catch (IOException) { }
            _baseline.Remove(repo);
            if (again) _rebaseline.Add(repo);
            Save();
            Pump();
        }
    }
}

/// The parts of `gh issue list --json …` the supervisor decides on.
public sealed record IssueInfo(int Number, string Title, string Body, List<string> Labels, List<string> Assignees, List<int> Blockers, string Author = "")
{
    // Labels that say "not for an agent yet". Substring match, case-insensitive.
    static readonly string[] Hold = ["question", "discussion", "wontfix", "won't fix", "invalid", "duplicate", "blocked", "on hold", "needs decision", "needs design", "needs-design", "epic"];

    // Trackers organise work; they are not work: by title, by their own words, or a checklist of issues.
    static readonly Regex TrackerTitle = new(@"\b(roadmap|tracker|tracking issue|epic|umbrella|meta issue)\b|^\s*\[?\s*phase\s*\d+\s*(/\s*\d+)?\s*\]", RegexOptions.IgnoreCase);
    static readonly Regex TrackerBody = new(@"\b(tracking only|planning and tracking|tracking issue|this is (the|a|an) [\w\s-]{0,40}tracker)\b", RegexOptions.IgnoreCase);
    static readonly Regex Checklist = new(@"^\s*[-*]\s*\[[ xX]\].*#\d+", RegexOptions.Multiline);
    // A decision to make, not code to write.
    static readonly Regex DecisionTitle = new(@"^\s*(decide|decision|choose)\b", RegexOptions.IgnoreCase);
    // "## Dependencies" (or Depends on, Prerequisites, Blocked by; a heading or a bold label) up to the next heading.
    static readonly Regex DependencySection = new(@"^(?:#{1,6}\s*|\*\*)(?:dependencies|depends on|prerequisites|blocked by)\b.*(?:\n(?!#{1,6}\s).*)*", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    // What follows these words on a dependency line is context, not a prerequisite: "#2 architecture; coordinate #7".
    static readonly Regex NotPrerequisite = new(@"\b(coordinat\w*|related|see also)\b", RegexOptions.IgnoreCase);
    // An issue reference or range ("#12", "#22–#27"), not a pull request's ("PR #12").
    static readonly Regex IssueRef = new(@"(?<!\b(?:PRs?|pull requests?)\s?)(?<![\w&])#(\d+)\b(?:\s*[–—-]\s*#?(\d+)\b)?", RegexOptions.IgnoreCase);
    // The owner's routing for Patch Goblins: a section whose heading names Patch Goblins (or autodock, its old name), with Skip: and Eligible: lines.
    static readonly Regex RoutingSection = new(@"^#{1,6}[^\n]*(?:patch.?goblins|handoff|autodock).*(?:\n(?!#{1,6}\s).*)*", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    static readonly Regex RoutingLine = new(@"^[\s>*_-]*(skip|eligible)\b[^:\n]*:", RegexOptions.IgnoreCase);

    public bool Tracker => TrackerTitle.IsMatch(Title) || TrackerBody.IsMatch(Body) || Checklist.Matches(Body).Count >= 3;
    public bool Decision => DecisionTitle.IsMatch(Title);

    /// Open issues this one waits for: GitHub's own dependencies, "blocked by / depends on #N", and the
    /// issues a Dependencies section names.
    public List<int> BlockedBy(IReadOnlySet<int> open) =>
        Blockers.Concat(Regex.Matches(Body, @"(?:blocked by|depends on)\s+#(\d+)", RegexOptions.IgnoreCase).Select(m => int.Parse(m.Groups[1].Value)))
            .Concat(DependencySection.Matches(Body).SelectMany(s => s.Value.Split('\n'))
                .SelectMany(l => Refs(NotPrerequisite.Match(l) is { Success: true } m ? l[..m.Index] : l)))
            .Where(n => n != Number && open.Contains(n)).Distinct().ToList();

    static IEnumerable<int> Refs(string text)
    {
        foreach (Match m in IssueRef.Matches(text))
        {
            var from = int.Parse(m.Groups[1].Value);
            var to = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var t) && t > from && t - from <= 100 ? t : from;
            for (var n = from; n <= to; n++) yield return n;
        }
    }

    /// The owner's routing notes for Patch Goblins, from issues the owner wrote: issues to skip, and, when an
    /// eligible list exists, the only issues agents take up on their own.
    public static (HashSet<int> Skip, HashSet<int>? Only) Routing(IEnumerable<IssueInfo> issues, string me)
    {
        var skip = new HashSet<int>();
        HashSet<int>? only = null;
        foreach (var issue in issues.Where(i => i.Author.Equals(me, StringComparison.OrdinalIgnoreCase)))
            foreach (Match section in RoutingSection.Matches(issue.Body))
                foreach (var line in section.Value.Split('\n'))
                    if (RoutingLine.Match(line) is { Success: true } m)
                    {
                        var refs = Refs(line[m.Length..]);
                        if (m.Groups[1].Value.Equals("skip", StringComparison.OrdinalIgnoreCase)) skip.UnionWith(refs);
                        else (only ??= []).UnionWith(refs);
                    }
        return (skip, only);
    }

    /// The owner's own (written by or assigned to them: anyone can open an issue on a public repository,
    /// and its text steers an agent), not someone else's, not held by a label, not a tracker or a decision,
    /// not waiting on another open issue. Issues the owner names in the agents terminal are taken regardless.
    public bool Actionable(string? me, IReadOnlySet<int> open)
    {
        if (me == null) return false;
        var mine = Assignees.Contains(me, StringComparer.OrdinalIgnoreCase);
        if (Assignees.Count > 0 && !mine) return false;
        if (!mine && !Author.Equals(me, StringComparison.OrdinalIgnoreCase)) return false;
        if (Labels.Any(l => Hold.Any(h => l.Contains(h, StringComparison.OrdinalIgnoreCase)))) return false;
        if (Tracker || Decision) return false;
        return BlockedBy(open).Count == 0;
    }

    /// Urgency from labels; the owner's own ordering outranks all of it.
    public int Rank =>
        Labels.Select(l => l.ToLowerInvariant()).Sum(l =>
            l.Contains("critical") || l.Contains("urgent") || l.Contains("p0") ? 300
            : l.Contains("high") || l.Contains("p1") ? 200
            : l.Contains("bug") ? 100
            : 0);

    /// Null when the output is not the expected list: never mistake garbage for "no open issues".
    public static List<IssueInfo>? ParseList(string json)
    {
        var list = new List<IssueInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var i in doc.RootElement.EnumerateArray())
            {
                if (i.TryGetProperty("state", out var st) && st.GetString() is { } s && s != "OPEN") continue;
                var blockers = i.TryGetProperty("blockedBy", out var b) && b.ValueKind == JsonValueKind.Object && b.TryGetProperty("nodes", out var nodes)
                    ? nodes.EnumerateArray().Where(n => n.TryGetProperty("number", out _)).Select(n => n.GetProperty("number").GetInt32()).ToList()
                    : [];
                list.Add(new(
                    i.GetProperty("number").GetInt32(),
                    i.GetProperty("title").GetString() ?? "",
                    i.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                    Names(i, "labels", "name"),
                    Names(i, "assignees", "login"),
                    blockers,
                    i.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object && au.TryGetProperty("login", out var al) ? al.GetString() ?? "" : ""));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
        return list;
    }

    /// Issues that open pull requests (`gh pr list --json closingIssuesReferences`) already resolve.
    public static HashSet<int> ClosedByOpenPullRequests(string json)
    {
        var set = new HashSet<int>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var pr in doc.RootElement.EnumerateArray())
                if (pr.TryGetProperty("closingIssuesReferences", out var refs) && refs.ValueKind == JsonValueKind.Array)
                    foreach (var r in refs.EnumerateArray())
                        if (r.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number) set.Add(n.GetInt32());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        return set;
    }

    /// The owner's and collaborators' comments of `gh issue view --json comments` newer than `since`,
    /// as "login: body". Anyone else's are not instructions.
    public static List<string> Comments(string json, DateTimeOffset since, string? me)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array) return list;
            foreach (var c in comments.EnumerateArray())
            {
                var who = c.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
                var body = c.TryGetProperty("body", out var b) ? b.GetString()?.Trim() ?? "" : "";
                var role = c.TryGetProperty("authorAssociation", out var r) ? r.GetString() : null;
                if (!PullRequestState.Trusted(who, role, me) || body.Length == 0) continue;
                if (!(c.TryGetProperty("createdAt", out var t) && DateTimeOffset.TryParse(t.GetString(), out var at) && at > since)) continue;
                list.Add($"{who}: {body}");
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        return list;
    }

    static List<string> Names(JsonElement e, string array, string field) =>
        e.TryGetProperty(array, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.TryGetProperty(field, out var v) ? v.GetString() ?? "" : "").Where(x => x.Length > 0).ToList()
            : [];
}

/// What `gh pr view --json state,mergeable,statusCheckRollup,comments,reviews` says needs doing.
public sealed record PullRequestState(string State, bool Conflicting, bool ChecksPending, List<string> FailedChecks, string? FailedRunId, List<string> Feedback,
    string Ci = "CI none: no checks ran", string? Head = null)
{
    public static PullRequestState? Parse(string json, DateTimeOffset since, string? me)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var pending = false;
            var failed = new List<string>();
            var passed = 0;
            var skipped = new List<string>();
            string? runId = null;
            if (root.TryGetProperty("statusCheckRollup", out var checks) && checks.ValueKind == JsonValueKind.Array)
                foreach (var c in checks.EnumerateArray())
                {
                    var name = Str(c, "name") ?? Str(c, "context") ?? "check";
                    var status = Str(c, "status");
                    var outcome = Str(c, "conclusion") ?? Str(c, "state");
                    if (status is { } s && s != "COMPLETED" || outcome is "PENDING" or "EXPECTED") { pending = true; continue; }
                    if (outcome is "SUCCESS") passed++;
                    else if (outcome is "FAILURE" or "ERROR" or "TIMED_OUT" or "CANCELLED" or "STARTUP_FAILURE" or "ACTION_REQUIRED")
                    {
                        failed.Add(name);
                        if (runId == null && Regex.Match(Str(c, "detailsUrl") ?? "", @"/actions/runs/(\d+)") is { Success: true } m) runId = m.Groups[1].Value;
                    }
                    else skipped.Add(name); // skipped, neutral, stale or anything else: it ran nothing that proves the change
                }
            var feedback = new List<string>();
            foreach (var (array, time) in new[] { ("comments", "createdAt"), ("reviews", "submittedAt") })
                if (root.TryGetProperty(array, out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var c in items.EnumerateArray())
                    {
                        var who = c.TryGetProperty("author", out var a) ? Str(a, "login") ?? "" : "";
                        var body = Str(c, "body") ?? "";
                        if (!Trusted(who, Str(c, "authorAssociation"), me) || !(DateTimeOffset.TryParse(Str(c, time), out var at) && at > since)) continue;
                        if (body.Trim().Length > 0) feedback.Add($"{who}: {body.Trim()}");
                        else if (Str(c, "state") == "CHANGES_REQUESTED") feedback.Add($"{who} requested changes.");
                    }
            // Only checks on this head count, and a skipped or neutral one proves nothing: green beside it is only partial.
            var ci = failed.Count > 0 ? $"CI failing: {string.Join(", ", failed)}" : pending ? "CI pending"
                : passed > 0 && skipped.Count == 0 ? $"CI passing: {passed} check{(passed == 1 ? "" : "s")}"
                : passed > 0 ? $"CI partial: {passed} passed, {skipped.Count} did not run ({string.Join(", ", skipped.Take(5))}); what did not run proves nothing"
                : skipped.Count > 0 ? "CI skipped: no evidence" : "CI none: no checks ran";
            return new(Str(root, "state") ?? "OPEN", Str(root, "mergeable") == "CONFLICTING", pending, failed, runId, feedback, ci, Str(root, "headRefOid"));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    /// Inline review comments (`gh api repos/…/pulls/N/comments`) newer than `since`.
    public static IEnumerable<string> InlineComments(string json, DateTimeOffset since, string? me)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var who = c.TryGetProperty("user", out var u) ? Str(u, "login") ?? "" : "";
                if (!Trusted(who, Str(c, "author_association"), me) || !(DateTimeOffset.TryParse(Str(c, "created_at"), out var at) && at > since)) continue;
                var line = c.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                list.Add($"{who} on {Str(c, "path")}:{line}: {Str(c, "body")?.Trim()}");
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        return list;
    }

    /// Whose words count as the owner's: the owner, and people GitHub lists as owner, member or collaborator.
    internal static bool Trusted(string login, string? association, string? me) =>
        login.Length > 0 && !login.EndsWith("[bot]") &&
        (me != null && login.Equals(me, StringComparison.OrdinalIgnoreCase) || association is "OWNER" or "MEMBER" or "COLLABORATOR");

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
