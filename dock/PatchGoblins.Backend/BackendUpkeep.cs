using System.Collections.Concurrent;
using System.Globalization;

namespace PatchGoblins;

/// Desktop scheduling only: quota, prime and stopped-window decisions remain in
/// the same classes the Windows tray uses. The host calls this on its context.
internal sealed partial class BackendUpkeep : IDisposable
{
    readonly Engine engine;
    readonly DockSettings settings;
    readonly Func<Snapshot?> snapshot;
    readonly Action<string, string> notify;
    readonly Func<Task> refresh;
    readonly QuotaAlerts alerts = new();
    readonly SwitchLine switchLine = new();
    readonly Primer primer = new(Primer.DefaultPath);
    readonly ConcurrentQueue<LimitHit> hits = new();
    readonly Dictionary<string, Limit> agentLimits = [];
    readonly HashSet<string> reported = [], told = [];
    readonly Dictionary<string, DateTime> moves;
    readonly string movesPath = Path.Combine(DockSettings.Dir, "resumed.json");
    readonly LimitWatch watch;
    readonly LimitSwitch limits;
    DateTimeOffset lastConfig, lastPrime;
    double? threshold;
    double hysteresis = 10;
    bool ticking, disposed;

    public double EffectiveThreshold => settings.AutoLine && snapshot()?.Accounts.FirstOrDefault(a => a.Active) is { } active
        ? switchLine.For(active, null) : threshold ?? 97;

    public BackendUpkeep(Engine engine, DockSettings settings, Func<Snapshot?> snapshot,
        Action<string, string> notify, Func<Task> refresh)
    {
        (this.engine, this.settings, this.snapshot, this.notify, this.refresh) = (engine, settings, snapshot, notify, refresh);
        moves = ResumeStopped.LoadMoves(movesPath);
        watch = new(hits.Enqueue, Program.Log);
        limits = new(snapshot, ResumeStopped.DefaultLogin, LiveOrLaunching,
            engine.ImportUsageAsync, engine.SwitchNowAsync, reported, Program.Log,
            threshold: () => EffectiveThreshold, hysteresis: () => hysteresis);
    }

    HashSet<int> LiveOrLaunching()
    {
        var live = ResumeStopped.LiveProfiles(ResumeStopped.ConfigDirs(null));
        live.UnionWith(ResumeStopped.Launching(moves, DateTime.UtcNow));
        return live;
    }

    public async Task TickAsync(Supervisor supervisor)
    {
        if (disposed || ticking || snapshot() is not { } snap) return;
        ticking = true;
        try
        {
            var notices = alerts.Observe("claude", snap.Accounts);
            switchLine.Observe(snap.Accounts, DateTimeOffset.Now);
            supervisor.Config.Threshold = EffectiveThreshold;
            if (settings.QuotaNotifications && QuotaAlerts.Balloon(notices) is { } balloon)
                notify(balloon.Title, balloon.Text);
            var now = DateTimeOffset.Now;
            if (now.Hour >= 8 && settings.AgentSummaryAt?.Date != now.Date)
            {
                var since = settings.AgentSummaryAt ?? now.AddDays(-1);
                settings.AgentSummaryAt = now;
                settings.Save();
                if (supervisor.Summary(since) is { } summary) notify("Agents since last time", summary);
            }
            if ((settings.AutoSwitch || settings.AutoPrime) && now - lastConfig > TimeSpan.FromMinutes(3))
                await ReadConfigAsync();
            if (settings.AutoPrime)
            {
                if (primer.Observe(snap.Accounts, now)) primer.Save();
                if (primer.WantsReread(now)) { await engine.ListAsync(refresh: true); await refresh(); primer.Reread(now); }
                // The original 30-minute guard and current login reserve apply.
                if (threshold != null && supervisor.IsRunning && now - lastPrime >= TimeSpan.FromMinutes(30)
                    && !(snap.Accounts.FirstOrDefault(a => a.Active) is { } login && ResumeStopped.Peak(login) >= EffectiveThreshold - hysteresis)
                    && primer.Ready(snap.Accounts, now) is { Count: > 0 } ready)
                {
                    lastPrime = now;
                    var fresh = await engine.ListAsync(refresh: true);
                    if (primer.Observe(fresh.Accounts, DateTimeOffset.Now)) primer.Save();
                    foreach (var account in primer.Ready(fresh.Accounts, DateTimeOffset.Now, Primer.SendReadingAge)
                                 .Where(a => ready.Any(b => Primer.Same(a, b))))
                    {
                        if (disposed || !settings.AutoPrime || !supervisor.IsRunning || !primer.Begin(account, DateTimeOffset.Now)) break;
                        var (result, detail) = await engine.PrimeAsync(account);
                        primer.Finish(account, result, detail);
                        if (result != PrimeResult.Sent) notify("Auto-Prime", $"#{account.Number}: {detail}");
                    }
                    await refresh();
                }
            }
            if (!settings.AutoSwitch || threshold == null || disposed) return;
            var dirs = ResumeStopped.ConfigDirs(snap.ActiveAccountNumber);
            watch.Sync(dirs.Select(d => d.Dir));
            await Task.Run(() => watch.Poll(ResumeStopped.LiveTranscripts(dirs, DateTime.UtcNow)));
            while (hits.TryDequeue(out var hit))
            {
                if (!settings.AutoSwitch || disposed) return;
                if (ResumeStopped.AgentSession(hit.ConfigDir, hit.File) is { } parent) agentLimits[parent] = hit.Limit;
                var outcome = await limits.OnLimitAsync(hit);
                if (outcome?.Switched is { Switched: true } switched)
                {
                    notify("Account switched", Format.SwitchNotice(switched.From, switched.To));
                    await refresh();
                }
                else if (outcome?.Error is { } error && told.Add(error)) notify("Auto-switch needs attention", error);
            }
            if (await FreePinnedAsync(supervisor, snapshot() ?? snap)) { await refresh(); return; }
            await ResumeAsync(snapshot() ?? snap);
        }
        catch (Exception error) { Program.Log(error); }
        finally { ticking = false; }
    }

    async Task ReadConfigAsync()
    {
        var config = await engine.ConfigAsync();
        if (!double.TryParse(config.GetValueOrDefault("autoswitch.threshold"), NumberStyles.Float, CultureInfo.InvariantCulture, out var line)) return;
        threshold = line;
        if (double.TryParse(config.GetValueOrDefault("autoswitch.hysteresisPct"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) hysteresis = h;
        ResumeStopped.Models = (config.GetValueOrDefault("autoswitch.model") ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lastConfig = DateTimeOffset.Now;
    }

    async Task ResumeAsync(Snapshot snap)
    {
        var login = ResumeStopped.DefaultLogin();
        var stops = await Task.Run(() => ResumeStopped.Scan(ResumeStopped.ConfigDirs(snap.ActiveAccountNumber), DateTime.UtcNow,
            login.Account == snap.ActiveAccountNumber ? login.ChangedAt : DateTime.MaxValue, includeBusy: true, agentLimits: agentLimits));
        if (ResumeStopped.LimitReport(stops, snap.Accounts, DateTimeOffset.UtcNow, reported) is { } report)
            await engine.ImportUsageAsync(report);
        var load = new Dictionary<int, int>();
        foreach (var stop in stops.DistinctBy(s => s.SessionId))
        {
            if (!settings.AutoSwitch || disposed) return;
            if (stop.Status == "agents")
            {
                if (stop.AtLimit && told.Add(stop.SessionId + "@" + stop.Limit?.At))
                    notify("Agents terminal stopped at a limit", $"Reopen the agents terminal in {stop.Cwd} from the Patch Goblins menu.");
                continue;
            }
            var target = stop.Idle ? ResumeStopped.Target(stop, snap.Accounts, n => ResumeStopped.ProfileDir(n) != null,
                EffectiveThreshold, hysteresis, DateTimeOffset.UtcNow, load, limits.RefusedUntil, LiveOrLaunching(), limits.ScopedRefusedUntil,
                line: settings.AutoLine ? a => switchLine.For(a, stop.Model) : null) : null;
            var key = $"{stop.SessionId}#{target}";
            var decision = ResumeStopped.Decide(stop, ResumeStopped.UserIdle(), TimeSpan.FromSeconds(5), target,
                moves.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(10),
                ResumeStopped.MovesInLastHour(moves, stop.SessionId, DateTime.UtcNow), () => ResumeStopped.Unchanged(stop));
            if (decision != ResumeStopped.Step.Move || target is not { } number) continue;
            var account = snap.Accounts.First(a => a.Number == number);
            var destination = account.Active ? ResumeStopped.DefaultDir : ResumeStopped.ProfileDir(number)!;
            var destinations = new[] { destination, ResumeStopped.DefaultDir, ResumeStopped.ProfileDir(number) }
                .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            void Copy() { foreach (var dir in destinations) ResumeStopped.CopyTranscript(stop, dir); }
            if (stop.Limit is { } old) watch.Remember(old.At);
            if (agentLimits.TryGetValue(stop.SessionId, out var agent)) watch.Remember(agent.At);
            await Task.Run(Copy); // a failed copy closes nothing
            if (!settings.AutoSwitch || disposed || !ResumeStopped.Unchanged(stop)
                || ResumeStopped.UserIdle() < (stop.AtLimit ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(60))
                || !ResumeStopped.SafeToClose(number, limits.RefusedUntil, limits.Switching,
                    login.Account, ResumeStopped.DefaultLogin().Account, DateTimeOffset.UtcNow)) continue;
            moves[key] = DateTime.UtcNow;
            ResumeStopped.SaveMoves(movesPath, moves, DateTime.UtcNow); // failure here closes nothing
            await ResumeStopped.CloseAsync(stop.Pid, stop.Record);
            try { await Task.Run(Copy); } catch (Exception error) { Program.Log(error); }
            moves[key] = moves[ResumeStopped.MoveStamp(stop.SessionId, DateTime.UtcNow)] = DateTime.UtcNow;
            try { ResumeStopped.SaveMoves(movesPath, moves, DateTime.UtcNow); }
            catch (Exception error) { Program.Log(error); } // a closed session must still be reopened if this save fails
            try
            {
                engine.LaunchConsole($"Claude · {account.ShortName} · resumed", stop.Cwd,
                    Launch.Run(number, settings.PreserveRemoteControl, ResumeStopped.ResumeArgs(stop, ResumeStopped.Moved(stop, number))));
            }
            catch (Exception error)
            {
                Program.Log(error);
                notify("Could not reopen a stopped window", $"In {stop.Cwd}: cswap run {number} -- --resume {stop.SessionId}");
            }
            limits.Forget(stop.SessionId);
            agentLimits.Remove(stop.SessionId);
            load[number] = load.GetValueOrDefault(number) + 1;
        }
    }

    public void Dispose() { disposed = true; watch.Dispose(); }
}
