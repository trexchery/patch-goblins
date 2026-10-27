using System.Text.Json;
using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

/// The worker guard and the permission rules on POSIX paths (macOS, Linux). The guard runs on any host here, because POSIX
/// paths are resolved by hand; only Confine reads the real folders, so its test runs on a POSIX host.
public class PosixPolicyTests
{
    static readonly string[] Roots = ["/Users/x/wt/k-21", "/Users/x/shared"];

    static int Guard(string tool, string input, string[]? roots = null) =>
        Policy.Guard(roots ?? Roots, JsonSerializer.Serialize(new { cwd = "/Users/x/wt/k-21", tool_name = tool, tool_input = JsonDocument.Parse(input).RootElement }), TextWriter.Null, posix: true);

    [Theory]
    [InlineData("Read", """{"file_path":"/Users/x/wt/k-21/package.json"}""", 0)]
    [InlineData("Read", """{"file_path":"src/a.ts"}""", 0)]
    [InlineData("Edit", """{"file_path":"/Users/x/shared/lib/x.ts"}""", 0)]
    [InlineData("Edit", """{"file_path":"/Users/x/wt/k-21/../k-21/./a"}""", 0)]
    [InlineData("Grep", """{"pattern":"audit"}""", 0)]
    [InlineData("Glob", """{"pattern":"**/package-lock.json"}""", 0)]
    [InlineData("Glob", """{"pattern":"/Users/x/wt/k-21/src/**"}""", 0)]
    [InlineData("Read", """{"file_path":"/Users/x/wt/k-2/package.json"}""", 2)] // a sibling worktree: no prefix match
    [InlineData("Read", """{"file_path":"/Users/x/wt/k-210/a"}""", 2)]
    [InlineData("Read", """{"file_path":"../k-2/x"}""", 2)]
    [InlineData("Read", """{"file_path":"/Users/x/wt/k-21/../../.ssh/id_ed25519"}""", 2)]
    [InlineData("Read", """{"file_path":"/Users/x/WT/k-21/package.json"}""", 2)] // POSIX paths compare exactly
    [InlineData("Write", """{"file_path":"/etc/hosts"}""", 2)]
    [InlineData("Read", """{"file_path":"~\\x"}""", 0)] // a backslash is a file name character here, not a separator: a file in the worktree
    [InlineData("Grep", """{"pattern":"x","path":"/"}""", 2)]
    [InlineData("Glob", """{"pattern":"/**"}""", 2)]
    [InlineData("Glob", """{"pattern":"/Users/**/*.json"}""", 2)]
    [InlineData("Glob", """{"pattern":"../../**/*.json"}""", 2)]
    public void FileToolsReachOnlyTheWorktreeAndTheApprovedFolders(string tool, string input, int exit) => Assert.Equal(exit, Guard(tool, input));

    [Fact]
    public void AnUnreadableCallIsRefusedAndTheRefusalSaysWhy()
    {
        Assert.Equal(2, Policy.Guard(Roots, "not json", TextWriter.Null, posix: true));
        var why = new StringWriter();
        Assert.Equal(2, Policy.Guard(Roots, """{"cwd":"/Users/x/wt/k-21","tool_name":"Read","tool_input":{"file_path":"/etc/hosts"}}""", why, posix: true));
        Assert.Contains("/etc/hosts is outside this agent's worktree", why.ToString());
    }

    [Fact]
    public void ARootThatIsTheFilesystemRootCoversEverythingAndTheHomeIsExpanded()
    {
        Assert.True(Policy.Covers("/", "/etc/hosts", posix: true));
        Assert.True(Policy.Covers("/a/b/", "/a/b", posix: true));
        Assert.False(Policy.Covers("/a/b", "/a/B/c", posix: true));
        Assert.Equal(["/Users/x/.ssh/k"], Policy.Targets("""{"tool_name":"Read","tool_input":{"file_path":"~/.ssh/k"}}""", "/w", "/Users/x", posix: true));
    }

    [Fact]
    public void PermissionRulesNameAbsolutePathsLikeClaudeCodeDoOnEachSystem()
    {
        Assert.Equal("//Users/x/Library/Application Support/p", Policy.Rule("/Users/x/Library/Application Support/p", posix: true));
        Assert.Equal("//c/a b/c", Policy.Rule(@"C:\a b\c", posix: false));
    }

    [Fact]
    public void POSIXGlobsMatchExactlyAndRootedPatternsStayRooted()
    {
        Assert.True(Policy.Glob("/Users/x/p/*", "/Users/x/p/agents", posix: true));
        Assert.False(Policy.Glob("/Users/x/p/*", "/Users/x/p/agents/wt", posix: true));
        Assert.False(Policy.Glob("/Users/x/P/*", "/Users/x/p/agents", posix: true));
        Assert.False(Policy.Glob("/users/x/p/*", "/Users/x/p/agents", posix: true));
        Assert.True(Policy.Glob("/Users/x/**", "/Users/x/p/agents/wt", posix: true));
        Assert.True(Policy.Glob("**/.env", "/Users/x/p/.env", posix: true));
    }

    [Fact]
    public void ThePosixHookIsSingleQuotedAndAlsoAllowsClaudesToolOutputFolder()
    {
        var hooks = JsonSerializer.SerializeToElement(Supervisor.Guard("/Users/x/it's here/k-21", ["/Users/x/shared"], posix: true)).GetProperty("PreToolUse");
        Assert.Equal(Policy.GuardedTools, hooks.EnumerateArray().Select(h => h.GetProperty("matcher").GetString()));
        var command = hooks[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
        Assert.Contains(" 'guard' '/Users/x/it'\\''s here/k-21' '/tmp/claude/-Users-x-it-s-here-k-21' ", command);
        Assert.Contains("'/private/tmp/claude/-Users-x-it-s-here-k-21'", command);
        Assert.EndsWith(" '/Users/x/shared'", command);
        Assert.DoesNotContain('"', command);
        Assert.DoesNotContain('|', command);
    }

    [Fact]
    public void ARuleThatWouldTakeInTheWorktreeIsNarrowedOnPosix()
    {
        if (OperatingSystem.IsWindows()) return; // reads real folders: runs where paths are POSIX
        using var tmp = new TempDir();
        var home = Path.Combine(tmp.Path, "home");
        var store = Path.Combine(home, "Library", "Application Support", "patch-goblins");
        var wt = Path.Combine(store, "agents", "wt", "k-21");
        foreach (var d in new[] { wt, Path.Combine(store, "agents", "wt", "k-2"), Path.Combine(store, "agents", "runs") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(store, "agents", "state.json"), "{}");
        string Rule(string p) => "/" + p;
        List<string> notes = [];
        var rules = Policy.Confine(["Read(~/Library/Application Support/patch-goblins/agents/*)", "Read(//etc/passwd)", "Read(~/library/application support/patch-goblins/agents/*)", "Bash(cswap *)"], wt, home, notes);
        Assert.Equal(new[]
        {
            "Read(//etc/passwd)", "Bash(cswap *)",
            "Read(~/library/application support/patch-goblins/agents/*)", // another case is another folder on a case-sensitive volume: untouched
            $"Read({Rule(Path.Combine(store, "agents", "runs"))})", $"Read({Rule(Path.Combine(store, "agents", "state.json"))})",
            $"Read({Rule(Path.Combine(store, "agents", "wt", "k-2"))})",
            $"Edit({Rule(Path.Combine(wt, ".claude", "settings.json"))})", $"Edit({Rule(Path.Combine(wt, ".claude", "settings.local.json"))})",
        }.Order(), rules.Order());
        Assert.All(rules.Where(r => r.StartsWith("Read(/") || r.StartsWith("Edit(/")), r => Assert.StartsWith("//", r[(r.IndexOf('(') + 1)..]));
        Assert.Single(notes);
    }
}
