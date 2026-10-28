using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

public sealed class PosixAdapterTests
{
    /// The macOS move (BackendUpkeep.ResumeAsync) shares `Decide`: an idle window whose project has a workflow running is left alone,
    /// and one at its limit is only reported. Synthetic run folders on the native file system.
    [Fact]
    public void AWindowRunningAWorkflowIsNeverMovedOnPosix()
    {
        using var tmp = new TempDir();
        var project = Path.Combine(tmp.Path, "projects", "-w");
        var run = Path.Combine(project, "old-sid", "subagents", "workflows", "wf_a");
        Directory.CreateDirectory(run);
        File.WriteAllLines(Path.Combine(run, "journal.jsonl"), ["""{"type":"launched"}""", """{"type":"started","key":"k","agentId":"a","label":"x","phase":"P"}"""]);
        var runs = ResumeStopped.RunningWorkflows(project, DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(["k"], Assert.Single(runs).InFlight);
        var idle = new StoppedSession(1, "sid", "/w", Path.Combine(tmp.Path, "profile"), 3, Path.Combine(project, "sid.jsonl"), false, Runs: runs);
        ResumeStopped.Step Of(StoppedSession s) => ResumeStopped.Decide(s, TimeSpan.FromHours(1), TimeSpan.Zero, 4, false, 0, () => true);
        Assert.Equal(ResumeStopped.Step.Ignore, Of(idle));
        Assert.Equal(ResumeStopped.Step.Left, Of(idle with { AtLimit = true }));
        Assert.Equal(ResumeStopped.Step.Move, Of(idle with { Runs = null }));
    }

    /// The check right before a close holds only for the process that wrote the record: a missing, unreadable or other
    /// start stamp is not a match (unknown means alive only where nothing is killed). Reads only; this process is the pid.
    [Fact]
    public void AWindowIsClosedOnlyWhenTheRecordProvesItsProcess()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX start stamps: runs on Linux and macOS
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "sid.jsonl");
        var record = Path.Combine(tmp.Path, "1.json");
        File.WriteAllText(transcript, """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"All done."}]}}""");
        var s = new StoppedSession(Environment.ProcessId, "sid", tmp.Path, tmp.Path, 3, transcript, false, record, File.GetLastWriteTimeUtc(transcript));
        Assert.True(s.Movable);
        bool With(string? procStart)
        {
            File.WriteAllText(record, JsonSerializer.Serialize(new { pid = Environment.ProcessId, sessionId = "sid", cwd = tmp.Path, status = "idle", kind = "interactive", procStart }));
            return ResumeStopped.Unchanged(s);
        }
        string real, other;
        if (OperatingSystem.IsLinux())
        {
            var stat = File.ReadAllText($"/proc/{Environment.ProcessId}/stat"); // field 22, start time in clock ticks
            real = stat[(stat.LastIndexOf(')') + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[19];
            other = (long.Parse(real) + 1000).ToString();
        }
        else
        {
            string Lstart(DateTime t) => t.ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture); // ps lstart, C locale, UTC
            var start = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            (real, other) = (Lstart(start), Lstart(start.AddHours(-1)));
        }
        Assert.False(With(null));
        Assert.False(With("not a start time"));
        Assert.False(With(other));
        Assert.True(With(real));
    }

    [Fact]
    public async Task AStaleRecoveryRecordNeverKillsAnUnrelatedProcessGroup()
    {
        using var tmp = new TempDir();
        var previous = Environment.GetEnvironmentVariable("PATCH_GOBLINS_DATA");
        Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", tmp.Path);
        var python = Engine.FindOnPath("python3")!;
        using var unrelated = Process.Start(new ProcessStartInfo(python,
            ["-c", "import os,time; os.setsid(); print('ready',flush=True); time.sleep(60)"])
            { UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            Assert.Equal("ready", await unrelated.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var job = "stale-" + Guid.NewGuid().ToString("N");
            var records = Path.Combine(tmp.Path, "processes");
            Directory.CreateDirectory(records);
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job))).ToLowerInvariant() + ".json";
            File.WriteAllText(Path.Combine(records, name), JsonSerializer.Serialize(new
            {
                pid = unrelated.Id,
                started = (unrelated.StartTime.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds,
                exitCode = (int?)null,
            }));
            AgentProcess.Reap(job);
            await Task.Delay(100);
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            if (!unrelated.HasExited) unrelated.Kill(entireProcessTree: true);
            await unrelated.WaitForExitAsync();
            Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", previous);
        }
    }

    [Fact]
    public void HardeningRetainsWorktreeExecutablesAndDoesNotFollowLinks()
    {
        using var temp = new TempDir();
        var data = Path.Combine(temp.Path, "data");
        Directory.CreateDirectory(data);
        var executable = Path.Combine(data, "verify.sh");
        File.WriteAllText(executable, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var outside = Path.Combine(temp.Path, "outside");
        File.WriteAllText(outside, "synthetic");
        File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        File.CreateSymbolicLink(Path.Combine(data, "link"), outside);
        StoreSecurity.Harden(data);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(executable));
        Assert.True(File.GetUnixFileMode(outside).HasFlag(UnixFileMode.OtherRead));
    }

    [Fact]
    public async Task AStaleJobRecordCannotMakeANewLaunchReady()
    {
        using var tmp = new TempDir();
        var previous = Environment.GetEnvironmentVariable("PATCH_GOBLINS_DATA");
        Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", tmp.Path);
        try
        {
            var job = "recovered-" + Guid.NewGuid().ToString("N");
            var records = Path.Combine(tmp.Path, "processes");
            Directory.CreateDirectory(records);
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job))).ToLowerInvariant() + ".json";
            File.WriteAllText(Path.Combine(records, name), "{\"pid\":2147483647,\"started\":0,\"exitCode\":0}");
            var input = Path.Combine(tmp.Path, "in");
            var output = Path.Combine(tmp.Path, "out");
            File.WriteAllText(input, "");
            using var worker = AgentProcess.Start("/bin/sh", ["-c", "sleep 30"], tmp.Path, job, input, output, output);
            try
            {
                using var reattached = AgentProcess.Attach(job, worker.Pid, worker.CreatedTicks);
                Assert.NotNull(reattached);
                Assert.Throws<EngineException>(() => AgentProcess.Start("/bin/sh", ["-c", "exit 0"], tmp.Path, job, input, output, output));
                using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(records, name)));
                Assert.Equal(worker.Pid, state.RootElement.GetProperty("pid").GetInt32());
            }
            finally { await worker.StopAsync(TimeSpan.FromSeconds(1)); }
        }
        finally { Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", previous); }
    }

    [Fact]
    public async Task WorkerArgumentsRemainLiteralAndPrivateFilesStayPrivate()
    {
        using var tmp = new TempDir();
        var previous = Environment.GetEnvironmentVariable("PATCH_GOBLINS_DATA");
        Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", tmp.Path);
        try
        {
            var input = Path.Combine(tmp.Path, "in");
            var output = Path.Combine(tmp.Path, "out");
            File.WriteAllText(input, "");
            var literal = "literal $(touch SHOULD_NOT_EXIST); ' \" ";
            using var worker = AgentProcess.Start("/usr/bin/printf", ["%s", literal], tmp.Path,
                "literal-" + Guid.NewGuid().ToString("N"), input, output, output);
            await worker.WaitForExitAsync();
            Assert.Equal(0, worker.ExitCode);
            Assert.Equal(literal, File.ReadAllText(output));
            Assert.False(File.Exists(Path.Combine(tmp.Path, "SHOULD_NOT_EXIST")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(output));
        }
        finally { Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", previous); }
    }
}
