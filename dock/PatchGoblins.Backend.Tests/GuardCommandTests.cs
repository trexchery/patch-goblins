using System.Diagnostics;
using System.Text.Json;
using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

/// `patch-goblins-backend guard <folder>...` is the worker's PreToolUse hook on macOS: the same contract as the Windows tray's
/// (tool call on stdin; exit 0 lets it run, exit 2 refuses it and says why on stderr). Run as a real process.
public class GuardCommandTests
{
    static (int Exit, string Err) Run(string root, string stdin)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "patch-goblins-backend.dll");
        Assert.True(File.Exists(dll), dll);
        var host = Engine.FindOnPath(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet") ?? throw new InvalidOperationException("no dotnet on PATH");
        using var p = Process.Start(new ProcessStartInfo(host, [dll, "guard", root]) { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, err);
    }

    static string Call(string cwd, string tool, string path) =>
        JsonSerializer.Serialize(new { cwd, tool_name = tool, tool_input = new { file_path = path } });

    [Fact]
    public void TheBackendAnswersTheGuardHookLikeTheWindowsTray()
    {
        using var tmp = new TempDir();
        var wt = Path.Combine(tmp.Path, "wt");
        Directory.CreateDirectory(wt);
        Assert.Equal(0, Run(wt, Call(wt, "Edit", Path.Combine(wt, "src", "a.ts"))).Exit);
        Assert.Equal(0, Run(wt, Call(wt, "Read", "package.json")).Exit);
        var outside = Run(wt, Call(wt, "Write", Path.Combine(tmp.Path, "elsewhere.txt")));
        Assert.Equal(2, outside.Exit);
        Assert.Contains("is outside this agent's worktree", outside.Err);
        Assert.Equal(2, Run(wt, Call(wt, "Read", Path.Combine(wt, "..", "elsewhere.txt"))).Exit);
        var unreadable = Run(wt, "not json");
        Assert.Equal(2, unreadable.Exit); // refused, never waved through
        Assert.Contains("could not read this tool call", unreadable.Err);
    }
}
