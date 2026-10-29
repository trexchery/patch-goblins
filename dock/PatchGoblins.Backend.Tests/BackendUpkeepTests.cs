using System.Diagnostics;
using System.Text.Json;
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
    [Fact]
    public async Task IdleAndLimitedUserSessionsAreNeverTerminatedByUpkeep()
    {
        if (OperatingSystem.IsWindows()) return; // This backend is used on macOS; CI also runs it on Linux.
        using var tmp = new TempDir();
        using var loop = new Loop();
        var oldHome = Environment.GetEnvironmentVariable("HOME");
        var oldXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var oldData = Environment.GetEnvironmentVariable("PATCH_GOBLINS_DATA");
        Environment.SetEnvironmentVariable("HOME", tmp.Path);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(tmp.Path, "xdg"));
        Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", Path.Combine(tmp.Path, "desktop"));
        using var child = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "sleep 60"]) { UseShellExecute = false })!;
        try
        {
            var dir = Path.Combine(StoreSecurity.BackupRoot, "sessions", "2-spare");
            var records = Path.Combine(dir, "sessions");
            var project = Path.Combine(dir, "projects", "test");
            Directory.CreateDirectory(records);
            Directory.CreateDirectory(project);
            var transcript = Path.Combine(project, "sid.jsonl");
            var record = Path.Combine(records, child.Id + ".json");
            File.WriteAllText(record, JsonSerializer.Serialize(new { pid = child.Id, sessionId = "sid", cwd = tmp.Path, status = "idle", kind = "interactive" }));
            File.WriteAllText(transcript, """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Done."}]}}""");
            File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow.AddMinutes(-5));
            var reset = DateTimeOffset.Now.AddHours(2);
            Account A(int n, double quota, bool active) => new(n, $"synthetic{n}@example.invalid", active, "ok",
                new Usage(new Window(quota, reset), new Window(quota, reset), null, null));
            var snapshot = new Snapshot(1, 1, [A(1, 100, true), A(2, 10, false)]);
            var notices = new List<string>();
            Assert.Single(ResumeStopped.Scan([(dir, 2)], DateTime.UtcNow, includeBusy: true));
            await loop.Run(async () =>
            {
                using var supervisor = new Supervisor(new Engine("/bin/true"), new Cli("/usr/bin/git", null),
                    new AgentStore(Path.Combine(tmp.Path, "agents")), new AgentsConfig());
                using var upkeep = new BackendUpkeep(new Engine("/bin/true"), new DockSettings { AutoSwitch = true, AutoLine = false },
                    () => snapshot, (_, text) => notices.Add(text), () => Task.CompletedTask, followsLogins: false);
                Assert.False(await upkeep.FreePinnedAsync(supervisor, snapshot));
                return true;
            });
            Assert.False(child.HasExited);
            Assert.True(File.Exists(record));
            Assert.Contains(notices, text => text.Contains("terminal", StringComparison.OrdinalIgnoreCase));
            notices.Clear();
            var limited = new Snapshot(1, 1, [A(1, 10, true), A(2, 100, false)]);
            File.WriteAllText(transcript, JsonSerializer.Serialize(new
            {
                type = "assistant", timestamp = DateTimeOffset.UtcNow.AddSeconds(2), isApiErrorMessage = true,
                apiError = "usage_limit_reached", apiErrorParams = new { rate_limit_info = new { status = "rejected", rateLimitType = "five_hour" } },
                message = new { role = "assistant", content = new[] { new { type = "text", text = "You've hit your limit" } } },
            }));
            Assert.True(Assert.Single(ResumeStopped.Scan([(dir, 2)], DateTime.UtcNow, includeBusy: true)).AtLimit);
            await loop.Run(async () =>
            {
                using var upkeep = new BackendUpkeep(new Engine("/bin/true"), new DockSettings { AutoSwitch = true, AutoLine = false },
                    () => limited, (_, text) => notices.Add(text), () => Task.CompletedTask, followsLogins: false);
                await upkeep.ResumeAsync(limited);
                return true;
            });
            Assert.False(child.HasExited);
            Assert.True(File.Exists(record));
            Assert.Contains(notices, text => text.Contains("remains open", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            Environment.SetEnvironmentVariable("HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldXdg);
            Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", oldData);
        }
    }

}
