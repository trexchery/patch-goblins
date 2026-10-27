using System.Diagnostics;
using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

public sealed class BackendUpkeepTests
{
    [Fact]
    public async Task AnUnknownHeadlessRunKeepsItsProfileAndIsNeverTerminated()
    {
        using var tmp = new TempDir();
        using var loop = new Loop();
        var oldHome = Environment.GetEnvironmentVariable("HOME");
        var oldData = Environment.GetEnvironmentVariable("PATCH_GOBLINS_DATA");
        Environment.SetEnvironmentVariable("HOME", tmp.Path);
        Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", Path.Combine(tmp.Path, "desktop"));
        using var unknown = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "sleep 60"])
            { UseShellExecute = false })!;
        try
        {
            var markerDir = Path.Combine(StoreSecurity.BackupRoot, "sessions", "2-spare", ".cswap-runs");
            Directory.CreateDirectory(markerDir);
            File.WriteAllText(Path.Combine(markerDir, unknown.Id.ToString()), "");
            var reset = DateTimeOffset.Now.AddHours(2);
            Account A(int n, double quota, bool active) => new(n, $"synthetic{n}@example.invalid", active, "ok",
                new Usage(new Window(quota, reset), new Window(quota, reset), null, null));
            var snapshot = new Snapshot(1, 1, [A(1, 100, true), A(2, 10, false)]);
            var notices = new List<string>();
            Assert.Contains(2, ResumeStopped.LiveProfiles(ResumeStopped.ConfigDirs(1)));
            await loop.Run(async () =>
            {
                using var supervisor = new Supervisor(new Engine("/bin/true"), new Cli("/usr/bin/git", null),
                    new AgentStore(Path.Combine(tmp.Path, "agents")), new AgentsConfig());
                using var upkeep = new BackendUpkeep(new Engine("/bin/true"), new DockSettings { AutoSwitch = true, AutoLine = false },
                    () => snapshot, (_, text) => notices.Add(text), () => Task.CompletedTask, followsLogins: false); // macOS mode, whatever runs this test
                Assert.False(await upkeep.FreePinnedAsync(supervisor, snapshot));
                Assert.Equal(0, supervisor.RunsOn(snapshot.Accounts[1]));
                return true;
            });
            Assert.False(unknown.HasExited);
            Assert.Contains(notices, text => text.Contains("headless", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (!unknown.HasExited) unknown.Kill(entireProcessTree: true);
            await unknown.WaitForExitAsync();
            Environment.SetEnvironmentVariable("HOME", oldHome);
            Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", oldData);
        }
    }
}
