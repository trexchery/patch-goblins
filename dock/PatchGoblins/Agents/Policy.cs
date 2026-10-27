using System.Text.Json;
using System.Text.RegularExpressions;

namespace PatchGoblins;

public sealed class PolicyException(string message) : Exception(message);

/// One verification command, run when a change touches one of its paths (all paths when none are given) on its
/// operating system (any when none is given).
public sealed record PolicyCheck(string Run, List<string>? Paths = null, string? Os = null);

/// A repository's restrictions: the shared policy below always applies, and a repository can only add to it, from
/// `.patch-goblins.json` on its base branch (what the owner merged, never a worktree's copy) and from the owner's
/// local file outside Git (`policies\<folder>.json` in the Patch Goblins folder), for repositories that should not
/// change. Every field is optional.
public sealed class RepoPolicy
{
    /// The policy format this build reads; a newer one is refused, never half-applied.
    public int Version { get; set; } = 1;
    /// `branch`, or `branch@sha` to pin it: agents refuse to start while the branch has moved past the commit.
    public string? Base { get; set; }
    /// "ask": every push waits for the owner's yes (a push starts the repository's remote CI).
    public string? Push { get; set; }
    /// Paths whose changes get an adversarial review and wait for the owner before they are pushed.
    public List<string> Sensitive { get; set; } = [];
    /// More Claude Code tool rules agents may not use, such as `Bash(ddc *)`. Rules match command text, so they stop
    /// mistakes, not a determined program (docs/agents.md, isolation).
    public List<string> Deny { get; set; } = [];
    public List<PolicyCheck> Checks { get; set; } = [];
    /// Folders outside its worktree that a repository's agents may also read and edit (Claude Code's `--add-dir`), by
    /// full path. None by default, and only the owner's local file can name them: a repository cannot widen what its
    /// own agents reach.
    public List<string> AddDirs { get; set; } = [];

    public (string Branch, string? Pin) BaseRef =>
        Base?.Split('@', 2) is [var b, var sha] ? (b, sha) : (Base ?? "", null);
}

public static class Policy
{
    public const int Version = 1;
    public const string FileName = ".patch-goblins.json";

    /// Shared policy v1: changes that can run code on the owner's machine or in CI, change what agents are told, or hold
    /// secrets. Each waits for the owner before it is pushed.
    public static readonly string[] Shared =
    [
        ".github/**", ".gitattributes", FileName, ".claude/**", ".mcp.json", "AGENTS.md", "CLAUDE.md", "CODEOWNERS",
        ".env", ".env.*", "*.pem", "*.key", "*.pfx", "*.p12", "id_rsa*", "*.tfvars", ".npmrc", ".pypirc",
    ];

    static readonly string[] Pushes = ["auto", "ask"];

    /// Secret file names and token- or key-shaped text: the doctor reports them committed, and a change that adds or
    /// touches one is sensitive.
    public static readonly Regex SecretFile = new(@"^(\.env(\..+)?|.+\.(pem|key|pfx|p12)|id_(rsa|ed25519|ecdsa)|credentials\.json|.+\.env|secrets?\.(json|ya?ml))$", RegexOptions.IgnoreCase);
    public const string SecretPattern = @"(ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{50,}|sk-ant-[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY-----)";

    public static RepoPolicy Parse(string json, string source)
    {
        RepoPolicy? p;
        try { p = JsonSerializer.Deserialize<RepoPolicy>(json, Json.Options); }
        catch (JsonException e) { throw new PolicyException($"{source} is not valid JSON: {e.Message}"); }
        if (p == null) throw new PolicyException($"{source} is empty");
        if (p.Version > Version) throw new PolicyException($"{source} is policy version {p.Version}; this Patch Goblins reads version {Version}. Update Patch Goblins.");
        // A pin is a commit, never an empty or one-letter prefix that any tip would start with.
        if (p.Base != null && !Regex.IsMatch(p.Base, @"^[^@\s]+(@[0-9a-fA-F]{7,40})?$")) throw new PolicyException($"{source}: base is a branch, or branch@commit with at least 7 hex digits of the commit");
        if (p.Push != null && !Pushes.Contains(p.Push)) throw new PolicyException($"{source}: push must be \"auto\" or \"ask\"");
        if (p.Checks.Any(c => string.IsNullOrWhiteSpace(c.Run))) throw new PolicyException($"{source}: every check needs a `run` command");
        if (p.Deny.Any(d => !Regex.IsMatch(d, @"^\w+(\(.+\))?$"))) throw new PolicyException($"{source}: deny entries are Claude Code tool rules, like Bash(ddc *)");
        // Narrow on purpose: never a drive, the user folder, Patch Goblins' own folder or one holding them, and nothing
        // that would need quoting in the hook command line.
        CheckAddDirs(p.AddDirs, source, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DockSettings.Dir, IsPosix, IsMac);
        return p;
    }

    /// `posix`/`mac` and the two folders are parameters so a test can run either rule set anywhere. Relative entries, `~`
    /// and `$HOME`-style text are not full paths and are refused; `..`, `.`, repeated and trailing slashes are resolved
    /// before comparing (Full), and on macOS the comparison ignores case, so `/users/me` is the user folder.
    internal static void CheckAddDirs(List<string> dirs, string source, string home, string dockDir, bool posix, bool mac)
    {
        if (dirs.FirstOrDefault(d => !(posix ? d.StartsWith('/') : Path.IsPathFullyQualified(d)) || d.IndexOfAny(['*', '?', '"', '$', '%', '`']) >= 0
                || (posix ? Full(d, "/", true) == "/" : Path.GetPathRoot(d) == Path.GetFullPath(d))
                || Covers(d, home, posix, mac) || Covers(d, dockDir, posix, mac) || Covers(dockDir, d, posix, mac)) is { } bad)
            throw new PolicyException($"{source}: addDirs are folders by full path, never a drive, the user folder or Patch Goblins' own: {bad}");
    }

    /// Both restrict: lists add up, "ask" wins, the owner's local base wins over the repository's.
    public static RepoPolicy Merge(RepoPolicy repo, RepoPolicy? local) => local == null ? repo : new()
    {
        Version = Math.Max(repo.Version, local.Version),
        Base = local.Base ?? repo.Base,
        Push = repo.Push == "ask" || local.Push == "ask" ? "ask" : local.Push ?? repo.Push,
        Sensitive = [.. repo.Sensitive.Union(local.Sensitive)],
        Deny = [.. repo.Deny.Union(local.Deny)],
        Checks = [.. repo.Checks.Concat(local.Checks).DistinctBy(c => (c.Run, c.Os, Describe(c.Paths)))],
        AddDirs = local.AddDirs,
    };

    public static string LocalPath(string repoPath) =>
        Path.Combine(DockSettings.Dir, "policies", Path.GetFileName(Path.GetFullPath(repoPath).TrimEnd('\\', '/')) + ".json"); // ponytail: by folder name; two clones of one name share it

    /// The repository's policy at `gitRef` merged with the owner's local one. Throws PolicyException when either is
    /// unreadable: an unreadable restriction must stop the work, not be skipped.
    public static async Task<RepoPolicy> LoadAsync(Cli cli, string repoPath, string gitRef, string? localPath = null)
    {
        var repo = new RepoPolicy();
        var tree = await cli.GitAsync(repoPath, "ls-tree", "--name-only", gitRef, "--", FileName);
        if (!tree.Ok) throw new PolicyException($"could not look for {FileName} on {gitRef}: {tree.Why}"); // not knowing is not "no policy"
        if (tree.Out.Trim().Length > 0)
        {
            var show = await cli.GitAsync(repoPath, "show", $"{gitRef}:{FileName}");
            repo = show.Ok ? Parse(show.Out, $"{FileName} on {gitRef}") : throw new PolicyException($"could not read {FileName} on {gitRef}: {show.Why}");
            if (repo.AddDirs.Count > 0) throw new PolicyException($"{FileName} on {gitRef}: addDirs belong in the owner's local policy ({LocalPath(repoPath)}), never in the repository");
        }
        return Merge(repo, ReadLocal(localPath ?? LocalPath(repoPath)));
    }

    /// The owner's local policy, or null when there is none. A file that is there but cannot be read throws.
    public static RepoPolicy? ReadLocal(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new PolicyException($"could not read {path}: {e.Message}"); }
        return Parse(text, path);
    }

    /// Changed files the shared or the repository's policy calls sensitive, and every file the doctor would call a secret.
    public static List<string> SensitiveIn(RepoPolicy policy, IEnumerable<string> files) =>
        files.Where(f => SecretFile.IsMatch(Path.GetFileName(f)) || Shared.Concat(policy.Sensitive).Any(g => Glob(g, f))).ToList();

    /// The checks that cover these changed files, and those that do but need another operating system.
    public static (List<PolicyCheck> Run, List<PolicyCheck> OtherOs) Select(IEnumerable<PolicyCheck> checks, IReadOnlyCollection<string> files, string os = "windows")
    {
        var covering = checks.Where(c => c.Paths is not { Count: > 0 } || files.Any(f => c.Paths.Any(g => Glob(g, f)))).ToList();
        var here = covering.Where(c => c.Os == null || c.Os.Equals(os, StringComparison.OrdinalIgnoreCase)).ToList();
        return (here, covering.Except(here).ToList());
    }

    /// The checks as the owner confirms them: one line each.
    public static string Describe(IEnumerable<PolicyCheck> checks) =>
        string.Join("\n", checks.Select(c => $"`{c.Run}`" + (c.Paths is { Count: > 0 } ? $" when {Describe(c.Paths)} change" : "") + (c.Os is { } o ? $" ({o} only)" : "")));

    static string Describe(List<string>? paths) => paths is { Count: > 0 } ? string.Join(", ", paths) : "";

    /// GitHub skips its workflows for a head commit that says so: such a push proves nothing.
    static readonly Regex SkipCi = new(@"\[(skip ci|ci skip|no ci|skip actions|actions skip)\]|^skip-checks:\s*true\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static bool SkipsCi(string commitMessages) => SkipCi.IsMatch(commitMessages);

    /// gitignore-style: `**` spans folders, `*` and `?` stay within one, a pattern without `/` matches at any depth.
    /// Case-insensitive, as Windows paths are. `posix`: a POSIX path, matched exactly, a backslash an ordinary character
    /// and a leading `/` the filesystem root; with `mac` it is matched ignoring case, as macOS's default volume does.
    public static bool Glob(string pattern, string path, bool posix = false, bool mac = false)
    {
        if (!posix) (path, pattern) = (path.Replace('\\', '/'), pattern.Replace('\\', '/'));
        var p = posix && path.StartsWith('/') ? pattern : pattern.TrimStart('/');
        var rx = Regex.Escape(p).Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
        return Regex.IsMatch(path, (p.Contains('/') ? "^" : "^(.*/)?") + rx + "$", posix && !mac ? RegexOptions.None : RegexOptions.IgnoreCase);
    }

    /// Policy paths follow the operating system the agents run on: Windows paths compare ignoring case, POSIX ones are
    /// absolute from `/` and compare exactly. The `posix` parameters below let a test exercise either rule set anywhere.
    static readonly bool IsPosix = !OperatingSystem.IsWindows();

    /// macOS's default (APFS) volume ignores case, so every comparison there ignores it too: the safe direction, a case
    /// variant of a refused or protected path is refused, never a way round the rule. Plain Linux stays exact. Symbolic
    /// links are not followed anywhere (`/tmp` is `/private/tmp` on macOS), so a link inside an allowed folder, or the
    /// other spelling of a folder, is not seen; Unicode normalization (HFS+) is not folded either.
    static readonly bool IsMac = OperatingSystem.IsMacOS();

    /// `path` made absolute against `from` with `.` and `..` resolved, no symbolic link followed. POSIX is resolved by hand
    /// so it works on any host; a relative `from` is taken from the root, which only refuses more.
    internal static string Full(string path, string from, bool posix)
    {
        if (!posix) return Path.GetFullPath(path, from);
        List<string> parts = [];
        foreach (var part in (path.StartsWith('/') ? path : from + "/" + path).Split('/'))
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (part is not ("" or ".")) parts.Add(part);
        return "/" + string.Join('/', parts);
    }

    /// `dir` is `path` or one of its folders.
    public static bool Covers(string dir, string path) => Covers(dir, path, IsPosix, IsMac);

    internal static bool Covers(string dir, string path, bool posix, bool mac = false)
    {
        if (posix)
        {
            var (a, b) = (Full(dir, "/", true).TrimEnd('/'), Full(path, "/", true).TrimEnd('/'));
            var cmp = mac ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return b.Equals(a, cmp) || b.StartsWith(a + "/", cmp);
        }
        var (d, p) = (Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        return p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static readonly Regex PathRule = new(@"^(Read|Edit|Write|MultiEdit|NotebookEdit|Glob|Grep)\((.+)\)$");

    /// Claude Code's absolute path rule form (`//` is the filesystem root): `C:\a b` is `//c/a b`, `/Users/x` is `//Users/x`.
    internal static string Rule(string path, bool posix) => posix ? "/" + path : "//" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');

    /// The deny rules a worker in `worktree` gets. Claude Code matches path rules gitignore-style, and a folder that
    /// matches takes everything below it: `Read(~/AppData/Local/patch-goblins/agents/*)` matches `agents\wt`, so it denies
    /// every worktree, and the worker can neither read its files nor run a command there. Such a rule is narrowed to what
    /// it denies beside the worktree: the other entries it matches beside the first folder it takes in, and everything
    /// beside each folder below that, as they are at launch. One that takes in the worktree's files only is dropped. The
    /// worker's own Claude Code settings in its worktree are never its to edit: its next run would load them.
    public static List<string> Confine(IEnumerable<string> deny, string worktree, string home, List<string> notes) =>
        Confine(deny, worktree, home, notes, IsPosix, IsMac);

    internal static List<string> Confine(IEnumerable<string> deny, string worktree, string home, List<string> notes, bool posix, bool mac)
    {
        string Slash(string p) => posix ? p : p.Replace('\\', '/');
        string Rule(string p) => Policy.Rule(p, posix);
        worktree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
        var chain = new List<string>();
        for (var d = worktree; d != null; d = Path.GetDirectoryName(d)) chain.Insert(0, d);
        var rules = new List<string>();
        foreach (var rule in deny)
        {
            if (PathRule.Match(rule) is not { Success: true } m) { rules.Add(rule); continue; }
            var (tool, pattern) = (m.Groups[1].Value, m.Groups[2].Value);
            // `~/` is the user folder and `//c/` a drive (`//` the root on POSIX); anything else is relative to the worktree.
            var abs = pattern.StartsWith("~/") ? Slash(home) + pattern[1..]
                : posix ? pattern.StartsWith("//") ? pattern[1..] : null
                : Regex.IsMatch(pattern, "^//[A-Za-z]/") ? pattern[2] + ":" + pattern[3..] : null;
            string[] own = abs == null ? ["probe", "probe/probe"] : [Slash(worktree) + "/probe", Slash(worktree) + "/probe/probe"];
            var at = abs == null ? -1 : chain.FindIndex(a => Glob(abs, Slash(a), posix, mac));
            if (at < 0 && !own.Any(o => Glob(abs ?? Regex.Replace(pattern, @"^\./", ""), o, posix, mac))) { rules.Add(rule); continue; }
            List<string> narrowed = [];
            if (at > 0) narrowed.AddRange(Entries(chain[at - 1]).Where(e => !Same(e, chain[at]) && Glob(abs!, Slash(e), posix, mac)));
            for (var j = at + 1; at >= 0 && j < chain.Count; j++) narrowed.AddRange(Entries(chain[j - 1]).Where(e => !Same(e, chain[j])));
            // ponytail: a rule high up (`~/**`) would list hundreds of folders and overrun the command line: dropped instead
            if (at < 0 || narrowed.Count > 100) { notes.Add($"{rule} would deny the worker its own worktree; not applied"); continue; }
            rules.AddRange(narrowed.Select(e => $"{tool}({Rule(e)})"));
            notes.Add($"{rule} would deny the worker its own worktree; applied to the {narrowed.Count} entries beside it");
        }
        rules.AddRange(new[] { "settings.json", "settings.local.json" }.Select(f => $"Edit({Rule(Path.Combine(worktree, ".claude", f))})"));
        return rules;

        bool Same(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), b, posix && !mac ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        static IEnumerable<string> Entries(string dir)
        {
            try { return Directory.GetFileSystemEntries(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    /// The file tools a worker's PreToolUse hook (Guard) confines, one matcher each: a `|` on the command line would be a
    /// pipe to cmd.exe wherever the engine or claude is a .cmd.
    public static readonly string[] GuardedTools = ["Read", "Edit", "Write", "MultiEdit", "NotebookEdit", "Glob", "Grep"];

    /// `patch-goblins.exe guard <folder>...`, a worker's PreToolUse hook: its file tools reach only these folders. Auto
    /// mode alone lets a worker read and write anywhere the owner's Windows user can, and Claude Code's path rules cannot
    /// say "only here". Exit code 2 makes Claude Code refuse the call and tell the worker why; a call it cannot read is
    /// refused too. Bash is not parsed: the owner's deny rules and auto mode's classifier cover it.
    public static int Guard(string[] roots, string input, TextWriter why) => Guard(roots, input, why, IsPosix, IsMac);

    internal static int Guard(string[] roots, string input, TextWriter why, bool posix, bool mac = false)
    {
        List<string> targets;
        try { targets = Targets(input, roots[0], Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), posix); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or NotSupportedException or IndexOutOfRangeException)
        {
            why.WriteLine("Patch Goblins could not read this tool call, so it is refused.");
            return 2;
        }
        if (targets.FirstOrDefault(t => !roots.Any(r => Covers(r, t, posix, mac))) is not { } outside) return 0;
        why.WriteLine($"{outside} is outside this agent's worktree and the folders its owner approved. Work inside the worktree; " +
            "if the task truly needs that folder, ask the owner, who can add it to the repository's local policy as addDirs.");
        return 2;
    }

    /// The paths a file tool call reaches, resolved as Claude Code does: against the session's folder, `~` as the user folder.
    internal static List<string> Targets(string input, string fallback, string home, bool posix)
    {
        var call = JsonDocument.Parse(input).RootElement;
        var cwd = call.TryGetProperty("cwd", out var c) && c.GetString() is { Length: > 0 } s ? s : fallback;
        var args = call.GetProperty("tool_input");
        string Full(string p, string from) => Policy.Full(p == "~" || p.StartsWith("~/") || !posix && p.StartsWith("~\\") ? home + p[1..] : p, from, posix);
        List<string> targets = [];
        foreach (var key in new[] { "file_path", "notebook_path", "path" })
            if (args.TryGetProperty(key, out var v) && v.GetString() is { Length: > 0 } p) targets.Add(Full(p, cwd));
        // A Glob pattern can climb out on its own (`../../**`, `C:/Users/**`): its folders up to the first wildcard.
        if (call.GetProperty("tool_name").GetString() == "Glob" && args.TryGetProperty("pattern", out var g) && g.GetString() is { } pattern)
        {
            var fixedPart = string.Join('/', (posix ? pattern : pattern.Replace('\\', '/')).Split('/').TakeWhile(x => x.IndexOfAny(['*', '?', '[', '{']) < 0));
            targets.Add(Full(fixedPart.Length > 0 ? fixedPart : posix && pattern.StartsWith('/') ? "/" : ".", targets.FirstOrDefault() ?? cwd));
        }
        return targets;
    }
}
