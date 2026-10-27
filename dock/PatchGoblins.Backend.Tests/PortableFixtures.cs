using System.Diagnostics;
using PatchGoblins;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PatchGoblins.Tests;

sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "patch-goblins-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
}

static class AgentProcessTests
{
    public static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out awaiting process condition");
            await Task.Delay(100);
        }
    }
}

public class PortableProcessTests
{
    [Fact]
    public async Task WorkerFilesExitCodesAndReattachmentUseProcessIdentity()
    {
        using var tmp = new TempDir();
        Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", tmp.Path);
        var input = System.IO.Path.Combine(tmp.Path, "in");
        var output = System.IO.Path.Combine(tmp.Path, "out");
        File.WriteAllText(input, "synthetic input\n");
        var job = "pg-" + Guid.NewGuid().ToString("N");
        using var process = AgentProcess.StartShell("cat; sleep 1; exit 7", tmp.Path, job, input, output);
        using var attached = AgentProcess.Attach(job, process.Pid, process.CreatedTicks);
        Assert.NotNull(attached);
        Assert.Null(AgentProcess.Attach(job, process.Pid, process.CreatedTicks + 1));
        await process.WaitForExitAsync();
        Assert.Equal(7, process.ExitCode);
        Assert.Contains("synthetic input", File.ReadAllText(output));
    }

    [Fact]
    public async Task DetachedWorkerSurvivesWrapperDisposalThenStopsItsOwnDescendants()
    {
        using var tmp = new TempDir();
        Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", tmp.Path);
        var input = System.IO.Path.Combine(tmp.Path, "in");
        var output = System.IO.Path.Combine(tmp.Path, "out");
        File.WriteAllText(input, "");
        var job = "pg-" + Guid.NewGuid().ToString("N");
        var worker = AgentProcess.Start("/bin/sh", ["-c", "sleep 60 & wait"], tmp.Path, job, input, output, output);
        var pid = worker.Pid;
        var created = worker.CreatedTicks;
        worker.Dispose();
        using var again = AgentProcess.Attach(job, pid, created);
        Assert.NotNull(again);
        await again.StopAsync(TimeSpan.FromSeconds(1));
        Assert.True(again.HasExited);
    }
}
