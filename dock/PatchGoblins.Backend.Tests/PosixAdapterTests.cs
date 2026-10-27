using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

public sealed class PosixAdapterTests
{
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
