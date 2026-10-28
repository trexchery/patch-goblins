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
    public void AnEmptyRootNeverCoversEverything()
    {
        const string read = """{"file_path":"/etc/hosts"}""";
        Assert.Equal(2, Guard("Read", read, [""]));
        Assert.Equal(2, Guard("Read", read, ["/Users/x/wt/k-21", " "]));
        Assert.Equal(2, Guard("Read", """{"file_path":"/Users/x/wt/k-21/a"}""", ["/Users/x/wt/k-21", ""]));
        Assert.Equal(2, Policy.Hook([""], () => """{"cwd":"/w","tool_name":"Read","tool_input":{"file_path":"/etc/hosts"}}""", TextWriter.Null));
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
        var rules = Policy.Confine(["Read(~/Library/Application Support/patch-goblins/agents/*)", "Read(//etc/passwd)", "Read(~/library/application support/patch-goblins/agents/*)", "Bash(cswap *)"], wt, home, notes, posix: true, mac: false);
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

    const string Home = "/Users/nicolas", Store = "/Users/nicolas/Library/Application Support/patch-goblins";

    static string? AddDirsError(string dir, bool mac) =>
        Record.Exception(() => Policy.CheckAddDirs([dir], "local", Home, Store, posix: true, mac: mac))?.Message;

    [Theory]
    [InlineData("/users/nicolas")] // another case of the user folder
    [InlineData("/USERS/NICOLAS/")]
    [InlineData("/users/nicolas/library/application support/PATCH-goblins")] // Patch Goblins' own folder
    [InlineData("/users/nicolas/library/APPLICATION SUPPORT")] // one that holds it
    [InlineData("/Users")] // one that holds the user folder
    [InlineData("/users/")]
    public void OnMacACaseVariantOfTheUserOrPatchGoblinsFolderIsRefusedAsAnAddDir(string dir) =>
        Assert.Contains("never a drive, the user folder or Patch Goblins' own", AddDirsError(dir, mac: true));

    [Theory]
    [InlineData("/Users/nicolas/Library/Application Support/patch-goblins/x")]
    [InlineData("/Users/nicolas//Library/../.")] // double slashes, `.` and `..` are resolved first
    [InlineData("/Users/nicolas/../")]
    [InlineData("/Users//NICOLAS/./x/..")]
    [InlineData("/")]
    [InlineData("/.")]
    [InlineData("//")]
    [InlineData("/x/..")]
    [InlineData("~")] // not a full path: there is no `~` expansion here
    [InlineData("~/shared")]
    [InlineData("shared")]
    [InlineData("./shared")]
    [InlineData("/Users/shared/$HOME")]
    public void OtherSpellingsOfTheSameFoldersAreRefusedOnMacAndOnLinux(string dir)
    {
        foreach (var mac in new[] { true, false })
            if (!dir.Contains("NICOLAS")) Assert.NotNull(AddDirsError(dir, mac)); // exact-case spellings are refused on Linux as well
        Assert.NotNull(AddDirsError(dir, mac: true));
    }

    [Theory]
    [InlineData("/users/nicolas")] // Linux: another case is another folder, and stays accepted
    [InlineData("/USERS/NICOLAS/Library/Application Support/patch-goblins")]
    [InlineData("/Users/Nicolas")]
    public void OnLinuxCaseVariantsStayDistinctFolders(string dir) => Assert.Null(AddDirsError(dir, mac: false));

    [Theory]
    [InlineData("/Users/Nicolas/Documents/lib", true)] // a folder below the user folder is fine, in any case
    [InlineData("/Users/shared/lib", true)]
    [InlineData("/Users/shared/lib/", true)]
    [InlineData("/Volumes/Work/Lib", true)]
    [InlineData("/Volumes/Work/Lib", false)]
    [InlineData("/users/shared/lib", false)]
    [InlineData("/tmp/x", true)] // /tmp is /private/tmp on macOS: links are not followed, so this stays a /tmp guard root only
    public void OrdinaryFoldersAreStillAccepted(string dir, bool mac) => Assert.Null(AddDirsError(dir, mac));

    [Theory]
    [InlineData("Read", """{"file_path":"/USERS/x/.ssh/id_ed25519"}""", 2)]
    [InlineData("Read", """{"file_path":"/users/x/wt/K-21/../../.SSH/k"}""", 2)]
    [InlineData("Read", """{"file_path":"/Users/x/wt/k-2/a"}""", 2)] // still no prefix match
    [InlineData("Read", """{"file_path":"/Users/x/wt/K-210/a"}""", 2)]
    [InlineData("Glob", """{"pattern":"/USERS/**"}""", 2)]
    [InlineData("Write", """{"file_path":"/ETC/hosts"}""", 2)]
    [InlineData("Read", """{"file_path":"/Users/x/WT/K-21/package.json"}""", 0)] // the same folder on this volume
    [InlineData("Edit", """{"file_path":"/USERS/X/SHARED/lib/x.ts"}""", 0)]
    public void OnMacTheGuardComparesIgnoringCaseSoVariantSpellingsOfDeniedPathsAreDenied(string tool, string input, int exit) =>
        Assert.Equal(exit, Policy.Guard(Roots, JsonSerializer.Serialize(new { cwd = "/Users/x/wt/k-21", tool_name = tool, tool_input = JsonDocument.Parse(input).RootElement }), TextWriter.Null, posix: true, mac: true));

    [Fact]
    public void OnMacACaseVariantRuleStillNarrowsAndCoversUsesTheSameRule()
    {
        Assert.True(Policy.Covers("/a/b", "/A/B/c", posix: true, mac: true));
        Assert.False(Policy.Covers("/a/b", "/A/B/c", posix: true, mac: false));
        Assert.True(Policy.Glob("/Users/x/p/*", "/users/X/P/agents", posix: true, mac: true));
        Assert.False(Policy.Glob("/Users/x/p/*", "/users/X/P/agents", posix: true));
        if (OperatingSystem.IsWindows()) return; // Confine reads real folders: runs where paths are POSIX
        using var tmp = new TempDir();
        var wt = Path.Combine(tmp.Path, "home", "Store", "agents", "wt", "k-21");
        Directory.CreateDirectory(wt);
        Directory.CreateDirectory(Path.Combine(tmp.Path, "home", "Store", "agents", "runs"));
        var rule = "Read(~/store/AGENTS/*)";
        var notes = new List<string>();
        Assert.Contains(rule, Policy.Confine([rule], wt, Path.Combine(tmp.Path, "home"), notes, posix: true, mac: false));
        Assert.Empty(notes);
        Assert.DoesNotContain(rule, Policy.Confine([rule], wt, Path.Combine(tmp.Path, "home"), notes, posix: true, mac: true));
        Assert.Single(notes);
    }
}
