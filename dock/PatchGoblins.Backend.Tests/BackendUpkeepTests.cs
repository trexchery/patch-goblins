using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

public sealed class BackendUpkeepTests
{
    [Fact]
    public async Task DefaultUpkeepNeverInvokesCredentialEngineOrTouchesSessions()
    {
        using var temp = new TempDir();
        var before = Environment.GetEnvironmentVariable("PATCH_GOBLINS_DATA");
        var context = SynchronizationContext.Current;
        Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", temp.Path);
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            var engine = new Engine(Path.Combine(temp.Path, "must-never-run"));
            var settings = new DockSettings { AgentSummaryAt = DateTimeOffset.Now };
            Snapshot current = new(1, 1, [Account(10)]);
            var notices = new List<string>();
            var refreshes = 0;
            using var supervisor = new Supervisor(engine, new Cli("must-never-git", null), new AgentStore(Path.Combine(temp.Path, "agents")), new AgentsConfig());
            using var upkeep = new BackendUpkeep(engine, settings, () => current, (_, text) => notices.Add(text), () => { refreshes++; return Task.CompletedTask; });
            await upkeep.TickAsync(supervisor);
            current = current with { Accounts = [Account(100)] };
            await upkeep.TickAsync(supervisor);
            Assert.Empty(notices);
            Assert.Equal(0, refreshes);
            Assert.False(File.Exists(Path.Combine(temp.Path, "prime.json")));
            Assert.False(File.Exists(Path.Combine(temp.Path, "resumed.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", before);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    [Fact]
    public async Task QuotaOptInReusesCrossingPolicyAndStaleReadingsNeverResetIt()
    {
        using var temp = new TempDir();
        var before = Environment.GetEnvironmentVariable("PATCH_GOBLINS_DATA");
        var context = SynchronizationContext.Current;
        Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", temp.Path);
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            var engine = new Engine("must-never-run");
            var settings = new DockSettings { AgentSummaryAt = DateTimeOffset.Now, QuotaNotifications = true };
            Snapshot current = new(1, 1, [Account(10)]);
            var notices = new List<string>();
            using var supervisor = new Supervisor(engine, new Cli("must-never-git", null), new AgentStore(Path.Combine(temp.Path, "agents")), new AgentsConfig());
            using var upkeep = new BackendUpkeep(engine, settings, () => current, (_, text) => notices.Add(text), () => Task.CompletedTask);
            await upkeep.TickAsync(supervisor);
            current = current with { Accounts = [Account(100)] };
            await upkeep.TickAsync(supervisor);
            Assert.Single(notices);
            Assert.Contains("limit reached", notices[0]);
            current = current with { Accounts = [Account(0) with { UsageStatus = "unavailable" }] };
            await upkeep.TickAsync(supervisor);
            Assert.Single(notices);
            current = current with { Accounts = [Account(0)] };
            await upkeep.TickAsync(supervisor);
            Assert.Equal(2, notices.Count);
            Assert.Contains("reset", notices[1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATCH_GOBLINS_DATA", before);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    static Account Account(double percent) => new(1, "synthetic@example.com", true, "ok", new Usage(new Window(percent, null), null, null, null));
}
