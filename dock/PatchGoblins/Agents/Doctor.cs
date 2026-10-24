using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PatchGoblins;

public enum Severity { Info, Limit, Block }

public sealed record Finding(Severity Level, string Area, string What, string? Fix = null);

/// What the doctor found, and what bootstrap proposes from it.
public sealed class DoctorReport
{
    public string Repo { get; init; } = "";
    public string Ref { get; set; } = "";
    public List<Finding> Findings { get; } = [];
    /// Commands the repository's pull-request CI runs, and on which operating system: the checks bootstrap proposes.
    public List<PolicyCheck> CiCommands { get; } = [];
    public bool HasAgentsMd, HasClaudeMd, HasIssueTemplate, HasPolicy;

    public string Verdict => Findings.Any(f => f.Level == Severity.Block) ? "BLOCKED" : Findings.Any(f => f.Level == Severity.Limit) ? "LIMITED" : "READY";

    public void Add(Severity level, string area, string what, string? fix = null) => Findings.Add(new(level, area, what, fix));

    public string Text()
    {
        var sb = new StringBuilder($"{Repo} at {Ref}: {Verdict}\n");
        foreach (var (level, title) in new[] { (Severity.Block, "Blocking"), (Severity.Limit, "Limiting"), (Severity.Info, "Notes") })
            if (Findings.Where(f => f.Level == level).ToList() is { Count: > 0 } list)
                sb.Append($"\n{title}:\n").AppendJoin("", list.Select(f => $"- [{f.Area}] {f.What}{(f.Fix is { } x ? $"\n  fix: {x}" : "")}\n"));
        return sb.ToString().TrimEnd();
    }
}

/// Read-only readiness check of a repository for unattended agents: `patch-goblins.exe doctor [folder]`, and the agents
/// terminal's agents_doctor. It reads git (after fetching origin) and GitHub and changes nothing. It never quotes issue
/// or pull request text, which anyone may have written: numbers and paths only.
public static class Doctor
{
    public static async Task<DoctorReport> AssessAsync(Cli cli, string path, string? baseBranch, string? me, string? agentsRoot = null, string? verify = null)
    {
        var report = new DoctorReport { Repo = Path.GetFileName(Path.GetFullPath(path).TrimEnd('\\', '/')) };
        async Task<string?> Git(params string[] a) => await cli.GitAsync(path, a) is { Ok: true } r ? r.Out.Trim() : null;
        if (await Git("rev-parse", "--show-toplevel") == null)
        {
            report.Add(Severity.Block, "repository", $"{path} is not a git repository");
            return report;
        }
        var origin = await Git("remote", "get-url", "origin");
        if (origin == null) report.Add(Severity.Limit, "repository", "no origin remote: agents can only leave work on local branches");
        else if (await Git("fetch", "--quiet", "--prune", "origin") == null) report.Add(Severity.Limit, "repository", "could not fetch origin: the findings below may be stale");

        // -- base and policy
        var local = Policy.LocalPath(path);
        RepoPolicy? pre = null;
        try { pre = Policy.ReadLocal(local); }
        catch (PolicyException e) { report.Add(Severity.Block, "policy", e.Message, "fix the file; agents refuse to work under a policy they cannot read"); }
        var remoteHead = (await Git("symbolic-ref", "--short", "refs/remotes/origin/HEAD"))?.Replace("origin/", "");
        var branch = baseBranch is { Length: > 0 } ? baseBranch : pre?.BaseRef.Branch is { Length: > 0 } b ? b : remoteHead ?? "main";
        var at = origin != null ? "origin/" + branch : branch;
        var tip = await Git("rev-parse", "--verify", "--quiet", at);
        report.Ref = tip != null ? $"{at} ({tip[..7]})" : at;
        if (tip == null)
        {
            // A pinned branch that is gone was often merged: say where its commit lives now.
            var holders = pre?.BaseRef.Pin is { } pinGone ? await Git("branch", "-r", "--contains", pinGone, "--format=%(refname:short)") : null;
            var on = holders?.Split('\n').Where(h => h is not ("origin/HEAD" or "origin")).OrderBy(h => h != "origin/" + remoteHead).Take(3).ToList() ?? [];
            report.Add(Severity.Block, "base", $"{at} does not exist" + (on.Count > 0 ? $"; the pinned {pre!.BaseRef.Pin} is now part of {string.Join(", ", on)}" : ""),
                on.Count > 0 ? $"the pin is obsolete: confirm the new base (for example {on[0]} at its current commit) and update the policy" : "name the right base branch (agents_control base, or `base` in the policy)");
            return report;
        }
        if (remoteHead != null && remoteHead != branch) report.Add(Severity.Info, "base", $"pull requests would target {branch}, not the default branch {remoteHead}");
        var policy = new RepoPolicy();
        try { policy = await Policy.LoadAsync(cli, path, at, local); report.HasPolicy = true; }
        catch (PolicyException e) { report.Add(Severity.Block, "policy", e.Message, "fix the file; agents refuse to work under a policy they cannot read"); }
        if (policy.BaseRef.Branch is { Length: > 0 } named && named != branch)
            report.Add(Severity.Block, "base", $"the policy names {named} as the base, not {branch}: agents refuse to work on {branch}", $"assess {named}, or change `base` in the policy");
        else if (policy.BaseRef is ({ } pinned, { } pin) && !tip.StartsWith(pin, StringComparison.OrdinalIgnoreCase))
        {
            var past = await Git("rev-list", "--count", $"{pin}..{tip}");
            report.Add(Severity.Block, "base", await Git("merge-base", "--is-ancestor", pin, tip) != null
                ? $"{branch} is pinned to {pin} but has moved {past} commit(s) past it, to {tip[..7]}"
                : $"{branch} is pinned to {pin}, which is not on the branch any more (rewritten or wrong pin)",
                $"review `git log {pin}..{at}`, then update the pin, or point the base back at {pin}");
        }
        if (Policy.SkipsCi(await Git("log", "-1", "--format=%B", tip) ?? ""))
            report.Add(Severity.Limit, "verification", $"the base tip {tip[..7]} skipped CI ([skip ci]): nothing proves the base itself passes", "run CI on the base, or rely on the local verification baseline");

        // -- local work: agents never touch the checkout, but it shows work that exists nowhere else
        var files = (await Git("ls-tree", "-r", "--name-only", tip) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        await LocalWorkAsync(Git, report, at);

        // -- instructions, verification, workflows, secrets
        await InstructionsAsync(Git, report, tip, files);
        var workflows = new List<(string Name, string Text)>();
        foreach (var f in files.Where(f => Regex.IsMatch(f, @"^\.github/workflows/[^/]+\.ya?ml$", RegexOptions.IgnoreCase)))
            workflows.Add((f, await Git("show", $"{tip}:{f}") ?? ""));
        foreach (var (name, text) in workflows) report.Findings.AddRange(WorkflowRisks(name, text));
        foreach (var (_, text) in workflows.Where(w => Triggers(w.Text).Contains("pull_request"))) report.CiCommands.AddRange(RunSteps(text));
        report.HasIssueTemplate = files.Any(f => f.StartsWith(".github/ISSUE_TEMPLATE/", StringComparison.OrdinalIgnoreCase));
        if (verify is not { Length: > 0 } && policy.Checks.Count == 0)
            report.Add(report.CiCommands.Count > 0 ? Severity.Limit : Severity.Block, "verification",
                report.CiCommands.Count > 0 ? "no local verification: changes are only checked by remote CI after a push (it spends Actions minutes, and a branch without CI proves nothing)"
                    : "nothing verifies a change: no verification command, no policy checks, no pull-request CI",
                "give agents a verification command, or add `checks` to the policy (bootstrap proposes them from CI)");
        foreach (var c in policy.Checks.Concat(verify is { Length: > 0 } v ? new[] { new PolicyCheck(v) } : []))
            if (SideEffect.Match(c.Run) is { Success: true } m)
                report.Add(Severity.Block, "side effects", $"the verification command `{c.Run}` mentions `{m.Value}`: verification runs unattended, many times", "verify with tests and builds only; keep deploys and publishing for people");
        report.Findings.AddRange(SecretFiles(files));
        report.Findings.AddRange(SecretHits(await Git("grep", "-I", "-n", "-E", Policy.SecretPattern, tip, "--") ?? ""));

        // -- GitHub: pull requests, issues, protection, flaky checks
        if (origin != null && cli.Gh != null && origin.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            await GitHubAsync(cli, path, report, branch, me);
        else report.Add(Severity.Limit, "github", "GitHub was not checked (no gh, or origin is not on GitHub): pull requests, issues and protection are UNVERIFIED");

        if (agentsRoot != null) report.Findings.AddRange(Outcomes(agentsRoot, report.Repo));
        return report;
    }

    static async Task LocalWorkAsync(Func<string[], Task<string?>> git, DoctorReport report, string at)
    {
        var dirty = (await git(["status", "--porcelain"]) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        if (dirty > 0) report.Add(Severity.Info, "local work", $"the checkout has {dirty} uncommitted change(s); agents leave them alone (they work in their own worktrees)");
        var unpushed = new List<string>();
        foreach (var line in (await git(["for-each-ref", "refs/heads", "--format=%(refname:short)|%(upstream:short)|%(upstream:track)"]) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(200))
        {
            var (name, upstream, track) = line.Split('|') is [var n, var u, var t] ? (n, u, t) : (line, "", "");
            if (upstream.Length > 0 && !track.Contains("gone")) { if (Regex.Match(track, @"ahead (\d+)") is { Success: true } a) unpushed.Add($"{name} (+{a.Groups[1].Value} unpushed)"); continue; }
            if (await git(["rev-list", "--count", $"{at}..{name}"]) is { } ahead && ahead != "0") unpushed.Add($"{name} ({ahead} commit(s) not on the base{(track.Contains("gone") ? ", its remote branch is gone" : ", never pushed")})");
        }
        if (unpushed.Count > 0)
            report.Add(Severity.Limit, "local work", $"{unpushed.Count} local branch(es) hold work that is on no remote: {string.Join(", ", unpushed.Take(5))}{(unpushed.Count > 5 ? ", …" : "")}",
                "push or delete them before planning, so agents do not redo or contradict that work");
        var stale = new List<string>();
        foreach (var block in (await git(["worktree", "list", "--porcelain"]) ?? "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Skip(1))
            if (block.Contains("\nprunable") || Regex.Match(block, @"^branch refs/heads/(.+)$", RegexOptions.Multiline) is { Success: true } b && await git(["merge-base", "--is-ancestor", b.Groups[1].Value.Trim(), at]) != null)
                stale.Add(block.Split('\n')[0].Replace("worktree ", ""));
        if (stale.Count > 0) report.Add(Severity.Info, "local work", $"{stale.Count} worktree(s) are merged or gone: {string.Join(", ", stale.Take(5))}", "`git worktree remove` them once nothing runs there");
    }

    static async Task InstructionsAsync(Func<string[], Task<string?>> git, DoctorReport report, string tip, List<string> files)
    {
        var set = new HashSet<string>(files.Concat(files.SelectMany(Parents)), StringComparer.OrdinalIgnoreCase);
        var docs = files.Where(f => Path.GetFileName(f) is "AGENTS.md" or "CLAUDE.md").Take(30).ToList();
        report.HasAgentsMd = docs.Contains("AGENTS.md");
        report.HasClaudeMd = docs.Contains("CLAUDE.md");
        if (!report.HasAgentsMd && !report.HasClaudeMd)
            report.Add(Severity.Limit, "instructions", "no AGENTS.md or CLAUDE.md: agents learn the repository's rules only from its code", "bootstrap proposes a short one");
        foreach (var doc in docs)
        {
            var text = await git(["show", $"{tip}:{doc}"]) ?? "";
            if (StaleRefs(doc, text, set) is { Count: > 0 } missing)
                report.Add(Severity.Limit, "instructions", $"{doc} points at {missing.Count} path(s) that do not exist on the base: {string.Join(", ", missing.Take(5))}", "update or remove them: agents follow instructions literally");
        }
        if (report is { HasAgentsMd: true, HasClaudeMd: true } && !Regex.IsMatch(await git(["show", $"{tip}:CLAUDE.md"]) ?? "", "AGENTS\\.md", RegexOptions.IgnoreCase))
            report.Add(Severity.Info, "instructions", "both AGENTS.md and CLAUDE.md exist and CLAUDE.md does not mention AGENTS.md: Claude Code reads only CLAUDE.md, other agents AGENTS.md, and the two can drift", "keep one authoritative and point the other at it");
    }

    static IEnumerable<string> Parents(string f)
    {
        for (var i = f.LastIndexOf('/'); i > 0; i = f.LastIndexOf('/', i - 1)) yield return f[..i];
    }

    /// Relative links and backticked paths in an instructions file that name nothing on the base.
    internal static List<string> StaleRefs(string doc, string text, IReadOnlySet<string> exists)
    {
        var dir = doc.Contains('/') ? doc[..doc.LastIndexOf('/')] : "";
        var refs = Regex.Matches(text, @"\]\(([^)\s#]+)(?:#[^)]*)?\)").Select(m => (m.Groups[1].Value, Link: true))
            .Concat(Regex.Matches(text, @"`([\w.-]+(?:/[\w.-]+)+/?|[\w-]+\.(?:md|json|ya?ml|toml|cs|csproj|py|ts|js|ps1|cmd|sh))`").Select(m => (m.Groups[1].Value, Link: false)));
        var missing = new List<string>();
        foreach (var (raw, link) in refs)
        {
            if (Regex.IsMatch(raw, @"^[a-z]+:|^/|^~|\.\.\.|[*<>{}$%]", RegexOptions.IgnoreCase)) continue; // URLs, absolute or home paths, patterns, placeholders
            var p = raw.TrimEnd('/');
            var candidates = new[] { Normalize(dir.Length > 0 ? dir + "/" + p : p), Normalize(p) };
            if (candidates.Any(exists.Contains)) continue;
            // A backticked `a/b` is a path here only when its first folder is (not a branch like origin/main); a bare file name may live anywhere.
            if (link || p.Contains('/') && exists.Contains(Normalize(p).Split('/')[0])) missing.Add(raw);
        }
        return missing.Distinct().ToList();
    }

    static string Normalize(string p)
    {
        var parts = new List<string>();
        foreach (var s in p.Split('/'))
            if (s == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (s is not ("" or ".")) parts.Add(s);
        return string.Join('/', parts);
    }

    // -- workflows ------------------------------------------------------------

    internal static HashSet<string> Triggers(string yaml)
    {
        var on = Regex.Match(yaml, @"^(?:on|""on""|'on'):(.*(?:\n(?:[ \t-].*|\s*))*)", RegexOptions.Multiline);
        if (!on.Success) return [];
        return Regex.Matches(on.Groups[1].Value, @"\b(pull_request_target|pull_request|push|workflow_dispatch|schedule|release|issue_comment|issues|workflow_run)\b").Select(m => m.Value).ToHashSet();
    }

    // Not a flag of the same name (`docker run --publish 5432:5432`).
    static readonly Regex SideEffect = new(@"(?<![-\w])(deploy|publish|twine upload|gh release|terraform apply|kubectl apply|wrangler|akash tx|docker push|npm publish|cargo publish)\b", RegexOptions.IgnoreCase);

    internal static List<Finding> WorkflowRisks(string name, string yaml)
    {
        yaml = Regex.Replace(yaml, @"(^|\s)#.*$", "", RegexOptions.Multiline); // what a comment mentions does not run
        var list = new List<Finding>();
        var on = Triggers(yaml);
        var pr = on.Contains("pull_request") || on.Contains("pull_request_target");
        if (on.Contains("pull_request_target"))
            list.Add(Regex.IsMatch(yaml, @"ref:\s*\$\{\{\s*github\.event\.pull_request\.head\.(sha|ref)")
                ? new(Severity.Block, "workflows", $"{name} checks out pull request code in pull_request_target, with the repository's secrets and a write token", "use pull_request (or workflow_run) for untrusted code")
                : new(Severity.Limit, "workflows", $"{name} runs on pull_request_target: privileged, with secrets, for pull requests from anyone", "prefer pull_request; never check out the pull request's code there"));
        if (!Regex.IsMatch(yaml, @"^permissions:", RegexOptions.Multiline))
            list.Add(new(Severity.Limit, "workflows", $"{name} sets no top-level permissions: its token gets the repository's default, which may be write", "add `permissions: contents: read` and raise it per job"));
        if (yaml.Contains("write-all")) list.Add(new(Severity.Limit, "workflows", $"{name} grants write-all permissions"));
        if (pr && yaml.Contains("self-hosted")) list.Add(new(Severity.Limit, "workflows", $"{name} runs pull requests on a self-hosted runner: an agent branch runs code on that machine"));
        if (Regex.IsMatch(yaml, @"\$\{\{\s*github\.(event\.(issue|pull_request|comment|review|head_commit|commits)\S*\.(title|body|message|label|name|ref)|head_ref)"))
            list.Add(new(Severity.Limit, "workflows", $"{name} expands issue or pull request text inside the workflow (script injection)", "pass it through an environment variable"));
        var secrets = Regex.Matches(yaml, @"secrets\.(\w+)").Select(m => m.Groups[1].Value).Where(s => s != "GITHUB_TOKEN").Distinct().ToList();
        var anyBranch = on.Contains("push") && !Regex.IsMatch(yaml, @"^\s+(branches|tags)\s*:", RegexOptions.Multiline);
        if ((pr || anyBranch) && secrets.Count > 0)
            list.Add(new(Severity.Limit, "workflows", $"{name} gives {string.Join(", ", secrets.Take(5))} to {(pr ? "pull requests" : "pushes to any branch")}: an agent's branch runs with them", "keep secrets in jobs that run after review (protected environments, main only)"));
        if ((pr || anyBranch) && SideEffect.Match(yaml) is { Success: true } m)
            list.Add(new(Severity.Limit, "side effects", $"{name} runs on {(pr ? "pull requests" : "pushes to any branch")} and mentions `{m.Value}`: an agent's push may reach production", "move it to a workflow on main, tags or manual dispatch"));
        var unpinned = Regex.Matches(yaml, @"uses:\s*([\w.-]+)/[\w./-]+@([\w.-]+)").Count(u => u.Groups[1].Value is not ("actions" or "github") && !Regex.IsMatch(u.Groups[2].Value, "^[0-9a-f]{40}$"));
        if (unpinned > 0) list.Add(new(Severity.Info, "workflows", $"{name} uses {unpinned} third-party action(s) by tag rather than commit"));
        return list;
    }

    /// The `run:` commands of a workflow's steps, each with the OS of its job's runner.
    internal static List<PolicyCheck> RunSteps(string yaml)
    {
        var list = new List<PolicyCheck>();
        string? os = null;
        var lines = yaml.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (Regex.Match(lines[i], @"runs-on:\s*\[?\s*['""]?(\w+)") is { Success: true } r)
                os = r.Groups[1].Value.ToLowerInvariant() switch { var s when s.StartsWith("windows") => "windows", var s when s.StartsWith("macos") => "macos", "self" => null, _ => "linux" };
            if (Regex.Match(lines[i], @"^(\s*)(?:-\s+)?run:\s*(.*)$") is not { Success: true } m) continue;
            var command = m.Groups[2].Value.Trim();
            var at = i;
            if (command is "|" or ">" or "|-" or ">-")
            {
                var indent = m.Groups[1].Value.Length;
                var body = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].Trim().Length == 0 || lines[i + 1].Length - lines[i + 1].TrimStart().Length > indent)) body.Add(lines[++i].Trim());
                var shell = lines.Skip(Math.Max(0, i - body.Count - 3)).Take(3 + body.Count).Any(l => Regex.IsMatch(l, @"shell:\s*pwsh")) ? "pwsh" : null;
                body.RemoveAll(l => l.Length == 0 || l.StartsWith('#'));
                command = shell == "pwsh" ? $"pwsh -NoProfile -Command \"{string.Join("; ", body).Replace("\"", "\\\"")}\"" : string.Join(" && ", body);
            }
            // Verification never runs a program from the current folder by bare name (NoDefaultCurrentDirectoryInExePath): CI's `build.cmd` is `.\build.cmd` here.
            if (os == "windows") command = Regex.Replace(command, @"^([\w.-]+\.(?:cmd|bat|exe|ps1))\b", @".\$1", RegexOptions.IgnoreCase);
            // A Windows job's step runs under PowerShell unless it names another shell (GitHub's default); verification runs
            // cmd.exe (CheckShell). What cmd.exe cannot run as written is suggested as CI runs it, through the step's own shell.
            if (os == "windows" && CheckShell.Problem(command, CheckShell.Kind.Cmd) != null)
            {
                var named = Regex.Match(string.Join("\n", lines.Skip(Math.Max(0, at - 3)).Take(4)), @"shell:\s*(\w+)").Groups[1].Value;
                command = named is "bash" or "sh" ? $"bash -c '{command.Replace("'", "'\\''")}'"
                    : named == "cmd" ? command
                    : $"pwsh -NoProfile -Command \"{command.Replace("\"", "\\\"")}\"";
            }
            if (command.Length > 0 && !command.Contains("${{")) list.Add(new(command, null, os));
        }
        return list.DistinctBy(c => (c.Run, c.Os)).ToList();
    }

    // -- secrets --------------------------------------------------------------

    static readonly Regex Synthetic = new(@"(test|fixture|fake|example|sample|mock|dummy|template)", RegexOptions.IgnoreCase);

    internal static List<Finding> SecretFiles(IEnumerable<string> files) =>
        files.Where(f => Policy.SecretFile.IsMatch(Path.GetFileName(f)) && !Synthetic.IsMatch(f))
            .Select(f => new Finding(Severity.Block, "secrets", $"{f} is committed and looks like a secret", "remove it from Git (and its history), rotate what it held")).ToList();

    /// `git grep -n` output (`ref:path:line:text`) to findings that name the place, never the text.
    internal static List<Finding> SecretHits(string grep)
    {
        var hits = grep.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => Regex.Match(l, @"^[^:]+:(.+?):(\d+):")).Where(m => m.Success).Select(m => (File: m.Groups[1].Value, Line: m.Groups[2].Value)).ToList();
        var real = hits.Where(h => !Synthetic.IsMatch(h.File)).ToList();
        var list = new List<Finding>();
        if (real.Count > 0) list.Add(new(Severity.Block, "secrets", $"{real.Count} token- or key-shaped string(s) are committed: {string.Join(", ", real.Take(5).Select(h => $"{h.File}:{h.Line}"))}", "rotate them if real, remove them, and add a secret scanner"));
        if (hits.Count > real.Count) list.Add(new(Severity.Info, "secrets", $"{hits.Count - real.Count} token-shaped string(s) in test or example files; confirm they are synthetic"));
        return list;
    }

    // -- GitHub ---------------------------------------------------------------

    static async Task GitHubAsync(Cli cli, string path, DoctorReport report, string branch, string? me)
    {
        me ??= await cli.GhAsync(path, "api", "user", "--jq", ".login") is { Ok: true } u ? u.Out.Trim() : null;
        if (me == null) { report.Add(Severity.Block, "github", "gh is not signed in: agents cannot read issues or open pull requests", "`gh auth login`"); return; }
        if (await cli.GhAsync(path, "pr", "list", "--state", "open", "--limit", "100", "--json", "number,baseRefName,headRefName,mergeable,updatedAt,files") is { Ok: true } prs)
            report.Findings.AddRange(PullRequests(prs.Out, branch, DateTimeOffset.Now));
        if (await cli.GhAsync(path, "issue", "list", "--state", "open", "--limit", "200", "--json", "number,title,body,labels,assignees,author,blockedBy") is { Ok: true } issues
            && IssueInfo.ParseList(issues.Out) is { } list)
            report.Findings.AddRange(Issues(list, me));
        var rules = await cli.GhAsync(path, "api", $"repos/{{owner}}/{{repo}}/rules/branches/{branch}", "--jq", "length");
        var protection = await cli.GhAsync(path, "api", $"repos/{{owner}}/{{repo}}/branches/{branch}/protection", "--silent");
        if (!protection.Ok && !(rules.Ok && rules.Out.Trim() is not ("0" or "")))
            report.Add(protection.Err.Contains("404") || protection.Err.Contains("not protected", StringComparison.OrdinalIgnoreCase) ? Severity.Limit : Severity.Info, "github",
                protection.Err.Contains("404") || protection.Err.Contains("not protected", StringComparison.OrdinalIgnoreCase)
                    ? $"{branch} has no branch protection or ruleset: agents never merge, but nothing on GitHub stops a direct push or a merge without checks"
                    : $"{branch}'s protection could not be read (UNVERIFIED)",
                "require pull requests and the CI checks on it");
        if (await cli.GhAsync(path, "run", "list", "--branch", branch, "--limit", "60", "--json", "headSha,conclusion,workflowName") is { Ok: true } runs)
            report.Findings.AddRange(Flaky(runs.Out));
    }

    internal static List<Finding> PullRequests(string json, string branch, DateTimeOffset now)
    {
        var list = new List<Finding>();
        var prs = new List<(int N, string Base, string Head, bool Conflict, DateTimeOffset Updated, HashSet<string> Files)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateArray())
                prs.Add((p.GetProperty("number").GetInt32(), p.GetProperty("baseRefName").GetString() ?? "", p.TryGetProperty("headRefName", out var h) ? h.GetString() ?? "" : "",
                    p.TryGetProperty("mergeable", out var m) && m.GetString() == "CONFLICTING",
                    p.TryGetProperty("updatedAt", out var u) && DateTimeOffset.TryParse(u.GetString(), out var t) ? t : now,
                    p.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().Select(x => x.GetProperty("path").GetString() ?? "").ToHashSet() : []));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { return [new(Severity.Info, "pull requests", "open pull requests could not be read (UNVERIFIED)")]; }
        foreach (var p in prs)
        {
            if (prs.FirstOrDefault(q => q.Head == p.Base) is { N: > 0 } under) list.Add(new(Severity.Info, "pull requests", $"#{p.N} is stacked on #{under.N}"));
            else if (p.Base != branch) list.Add(new(Severity.Limit, "pull requests", $"#{p.N} targets {p.Base}, not {branch}", "retarget it, or confirm the base agents should use"));
            if (p.Conflict) list.Add(new(Severity.Limit, "pull requests", $"#{p.N} conflicts with its base", "rebase or close it before agents change the same code"));
            if (now - p.Updated > TimeSpan.FromDays(21)) list.Add(new(Severity.Info, "pull requests", $"#{p.N} has not changed for {(int)(now - p.Updated).TotalDays} days", "finish or close it: its changes may already be obsolete"));
        }
        // One line per pull request that shares files with others; a stack (one built on the other's branch) shares them by design.
        foreach (var p in prs)
        {
            var peers = prs.Where(q => q.N != p.N && q.Base != p.Head && p.Base != q.Head && q.Files.Overlaps(p.Files)).ToList();
            if (peers.Count == 0 || peers.All(q => q.N < p.N)) continue; // reported with the lower number
            var shared = peers.SelectMany(q => q.Files.Intersect(p.Files)).Distinct().ToList();
            list.Add(new(Severity.Limit, "pull requests", $"#{p.N} changes the same files as {string.Join(", ", peers.Select(q => "#" + q.N))} ({shared.Count} file(s), e.g. {string.Join(", ", shared.Take(3))})",
                "decide which lands first and close or rebase the rest; agents would add yet another version"));
        }
        return list;
    }

    static readonly Regex Criteria = new(@"acceptance|done when|definition of done|expected|should|must|verify|test|-\s*\[ \]", RegexOptions.IgnoreCase);

    internal static List<Finding> Issues(List<IssueInfo> issues, string me)
    {
        var list = new List<Finding>();
        var open = issues.Select(i => i.Number).ToHashSet();
        var foreign = issues.Count(i => !i.Author.Equals(me, StringComparison.OrdinalIgnoreCase) && !i.Assignees.Contains(me, StringComparer.OrdinalIgnoreCase));
        if (foreign > 0) list.Add(new(Severity.Info, "issues", $"{foreign} open issue(s) are by others and not assigned to you: agents ignore them and never read their text"));
        var after = issues.ToDictionary(i => i.Number, i => i.BlockedBy(open));
        if (after.Count(a => a.Value.Count > 0) is var blocked and > 0) list.Add(new(Severity.Info, "issues", $"{blocked} issue(s) wait on other open issues and are not taken until those close"));
        if (Cycle(after) is { } cycle) list.Add(new(Severity.Limit, "issues", $"issues wait on each other in a circle: {string.Join(" → ", cycle.Select(n => "#" + n))}", "drop one of those dependencies"));
        var ready = issues.Where(i => i.Actionable(me, open)).ToList();
        var vague = ready.Where(i => !Criteria.IsMatch(i.Body)).Select(i => i.Number).ToList();
        if (vague.Count > 0) list.Add(new(Severity.Limit, "issues", $"{vague.Count} issue(s) agents would take say nothing about when they are done: {string.Join(", ", vague.Take(8).Select(n => "#" + n))}", "add acceptance criteria and the check that proves them"));
        list.Add(new(Severity.Info, "issues", $"{ready.Count} open issue(s) are ready for agents"));
        return list;
    }

    internal static List<int>? Cycle(Dictionary<int, List<int>> after)
    {
        var state = new Dictionary<int, int>(); // 1 on the path, 2 done
        var path = new List<int>();
        List<int>? Visit(int n)
        {
            if (state.GetValueOrDefault(n) == 1) return [.. path.Skip(path.IndexOf(n)), n];
            if (state.GetValueOrDefault(n) == 2) return null;
            state[n] = 1;
            path.Add(n);
            foreach (var m in after.GetValueOrDefault(n) ?? [])
                if (Visit(m) is { } c) return c;
            state[n] = 2;
            path.RemoveAt(path.Count - 1);
            return null;
        }
        return after.Keys.Order().Select(Visit).FirstOrDefault(c => c != null);
    }

    /// A workflow that both failed and passed on one commit of the base: a check that is not deterministic.
    internal static List<Finding> Flaky(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var flaky = doc.RootElement.EnumerateArray()
                .GroupBy(r => (r.GetProperty("headSha").GetString(), r.GetProperty("workflowName").GetString()))
                .Where(g => g.Any(r => r.GetProperty("conclusion").GetString() == "failure") && g.Any(r => r.GetProperty("conclusion").GetString() == "success"))
                .Select(g => g.Key.Item2).Distinct().ToList();
            return flaky.Select(w => new Finding(Severity.Limit, "verification", $"workflow {w} failed and passed on the same commit of the base: a flaky check makes red and green both meaningless", "fix or quarantine the flaky test")).ToList();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { return []; }
    }

    // -- what agents did here ---------------------------------------------------

    /// Outcomes from the agents' own records: pull requests merged and closed unmerged, parked items and repeated reasons,
    /// runs, retries, hours and tokens. Activity alone is no success.
    internal static List<Finding> Outcomes(string agentsRoot, string leaf)
    {
        var list = new List<Finding>();
        var repo = new AgentStore(agentsRoot).Load().Repos.FirstOrDefault(r => r.Leaf.Equals(leaf, StringComparison.OrdinalIgnoreCase));
        var runs = new List<JsonElement>();
        var metrics = Path.Combine(agentsRoot, "metrics.jsonl");
        if (File.Exists(metrics))
            foreach (var line in File.ReadLines(metrics))
                try { using var d = JsonDocument.Parse(line); if (d.RootElement.TryGetProperty("repo", out var r) && r.GetString() == leaf) runs.Add(d.RootElement.Clone()); }
                catch (JsonException) { }
        if (repo == null && runs.Count == 0) return list;
        var items = repo?.Items ?? [];
        var merged = items.Count(i => i.Merged);
        var unmerged = items.Count(i => i is { Pr: not null, Merged: false, State: ItemState.Done });
        var parked = items.Where(i => i.State == ItemState.NeedsOwner).ToList();
        var retries = runs.Count(r => r.TryGetProperty("attempt", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() > 1);
        var hours = runs.Sum(r => r.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0) / 3600.0;
        list.Add(new(Severity.Info, "outcomes", $"{merged} agent pull request(s) merged, {unmerged} closed unmerged, {parked.Count} waiting on you; {runs.Count} run(s), {retries} retried, {hours:0.#} h"));
        foreach (var g in parked.Where(i => i.Note != null).GroupBy(i => Regex.Replace(i.Note!, @"\d+", "N")).Where(g => g.Count() >= 2 && g.Key.Length > 0))
            list.Add(new(Severity.Limit, "outcomes", $"{g.Count()} items stopped for the same reason: {g.Key}", "fix the cause once (instructions, verification or the issues), not each item"));
        if (merged + unmerged >= 4 && unmerged * 2 > merged + unmerged)
            list.Add(new(Severity.Limit, "outcomes", "most agent pull requests were closed without merging", "sharpen issues' acceptance criteria and the repository's instructions before more runs"));
        return list;
    }

    // -- bootstrap --------------------------------------------------------------

    /// What bootstrap would add, as a preview: nothing is written. Only what is missing, nothing that duplicates
    /// existing instructions.
    public static string Bootstrap(DoctorReport report)
    {
        var sb = new StringBuilder("Bootstrap preview (nothing written; commit what you want, or ask agents to as a pull request):\n");
        if (!report.HasPolicy || report.CiCommands.Count > 0)
        {
            var policy = new { version = Policy.Version, checks = report.CiCommands.Select(c => new { run = c.Run, os = c.Os }) };
            sb.Append($"\n--- {Policy.FileName} (or {Policy.LocalPath(report.Repo)} to keep it outside the repository); its checks are this repository's pull-request CI commands, to trim to what proves a change locally\n")
              .AppendLine(JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        if (!report.HasAgentsMd && !report.HasClaudeMd)
            sb.Append("\n--- AGENTS.md (Claude Code reads it when there is no CLAUDE.md)\n# Agent instructions\n\n")
              .AppendLine("## Verify\n" + (report.CiCommands.Count > 0 ? string.Join("\n", report.CiCommands.Select(c => $"- `{c.Run}`{(c.Os is { } o ? $" ({o})" : "")}")) : "- (the command that proves a change works)"))
              .AppendLine("\n## Boundaries\n- Work on a branch; never push to the base, merge, deploy, publish or touch secrets.\n- Stop and ask when the issue leaves a product decision open.");
        else if (report is { HasClaudeMd: true, HasAgentsMd: false })
            sb.Append("\n--- AGENTS.md (optional, for agents other than Claude Code)\nRead CLAUDE.md: it is this repository's agent instructions.\n");
        if (!report.HasIssueTemplate)
            sb.Append("\n--- .github/ISSUE_TEMPLATE/task.md (a .github change: Patch Goblins holds it for you)\n")
              .AppendLine("---\nname: Task\nabout: A change agents can take\n---\n\n## Goal\n\n## Acceptance criteria\n- [ ] \n\n## Verification\nThe command or check that proves it.\n\n## Dependencies\nIssues that must land first (#N), if any.\n\n## Out of scope\n");
        return sb.ToString().TrimEnd();
    }

    // -- command line -------------------------------------------------------------

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    /// `patch-goblins.exe doctor [folder] [--base branch] [--verify command] [--bootstrap]`: exit 0 ready, 1 limited, 2 blocked, 3 unusable.
    public static int Run(string[] args)
    {
        AttachConsole(-1); // a window app writes to the console it was started from
        Console.OutputEncoding = Encoding.UTF8;
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--") && a != Arg("--base") && a != Arg("--verify")) ?? Environment.CurrentDirectory;
        if (Cli.Locate() is not { } cli) { Console.Error.WriteLine("git is not installed"); return 3; }
        var report = AssessAsync(cli, path, Arg("--base"), null, Directory.Exists(AgentStore.DefaultRoot) ? AgentStore.DefaultRoot : null, Arg("--verify")).GetAwaiter().GetResult();
        Console.WriteLine(Engine.Redact(report.Text()));
        if (args.Contains("--bootstrap")) Console.WriteLine("\n" + Engine.Redact(Bootstrap(report)));
        return report.Verdict switch { "READY" => 0, "LIMITED" => 1, _ => 2 };
    }
}
