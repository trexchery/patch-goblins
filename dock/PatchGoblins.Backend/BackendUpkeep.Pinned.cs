namespace PatchGoblins;

internal sealed partial class BackendUpkeep
{
    readonly Dictionary<int, DateTime> freed = [];
    readonly HashSet<int> heldProfiles = [];

    /// The Windows tray's FreePinnedAsync flow, using the same account, idle,
    /// ownership and move guards. Only sessions already judged idle can close.
    internal async Task<bool> FreePinnedAsync(Supervisor supervisor, Snapshot snap)
    {
        var due = ResumeStopped.FreeTargets(snap.Accounts, LiveOrLaunching(), limits.RefusedUntil,
            DateTimeOffset.UtcNow, EffectiveThreshold, hysteresis, followsLogins);
        if (due.Count == 0) heldProfiles.Clear();
        foreach (var number in due)
            if (await FreePinnedAsync(supervisor, snap, number)) return true;
        return false;
    }

    async Task<bool> FreePinnedAsync(Supervisor supervisor, Snapshot snap, int number)
    {
        void Held(string message)
        {
            if (heldProfiles.Add(number)) notify($"#{number} is held by a pinned terminal", message);
        }
        if (disposed || !settings.AutoSwitch || limits.Switching || autoTask is { IsCompleted: false }
            || ResumeStopped.ProfileDir(number) is not { } dir) return false;
        if (freed.TryGetValue(number, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(30))
        {
            Held($"#{number} was freed under half an hour ago and is in use again. It is not freed repeatedly.");
            return false;
        }
        var account = snap.Accounts.First(a => a.Number == number);
        List<StoppedSession> windows;
        bool agents;
        try
        {
            windows = await Task.Run(() => ResumeStopped.Scan([(dir, number)], DateTime.UtcNow,
                includeBusy: true, agentLimits: agentLimits));
            var (records, runs) = await Task.Run(() => ResumeStopped.LiveCounts(dir));
            agents = ResumeStopped.AgentsOnly(windows, records, runs, supervisor.RunsOn(account));
            if (!agents && !ResumeStopped.Releasable(windows, records, runs))
            {
                var holders = ResumeStopped.Holders(windows, records, runs);
                if (holders.Count > 0)
                    Held($"{string.Join(", ", holders)} keeps #{number}. " + (windows.Any(w => w.Status == "agents")
                        ? "Close the agents terminal to free it, then reopen it from the Agents menu."
                        : "It becomes available after its work finishes or its terminal becomes idle."));
                return false;
            }
            if (windows.Count > 0 && ResumeStopped.UserIdle() < TimeSpan.FromSeconds(60))
            {
                Held($"#{number}'s idle windows move once nobody has typed for a minute.");
                return false;
            }
            if (windows.Any(w => ResumeStopped.MovesInLastHour(moves, w.SessionId, DateTime.UtcNow) >= ResumeStopped.MovesPerHour
                || moves.TryGetValue($"{w.SessionId}#{number}", out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(10)))
            {
                Held($"A window on #{number} moved recently and is not closed again so soon.");
                return false;
            }
            foreach (var window in windows) ResumeStopped.CopyTranscript(window, ResumeStopped.DefaultDir);
            // A failed copy or journal write closes nothing.
            ResumeStopped.SaveMoves(movesPath, moves, DateTime.UtcNow);
        }
        catch (Exception error) { Program.Log(error); return false; }

        var closed = new List<StoppedSession>();
        var yielded = false;
        SwitchResult? switched = null;
        try
        {
            foreach (var window in windows)
            {
                if (disposed || !settings.AutoSwitch || limits.Switching || autoTask is { IsCompleted: false } || !ResumeStopped.Unchanged(window)
                    || ResumeStopped.UserIdle() < TimeSpan.FromSeconds(60)) break;
                if (window.Limit is { } old) watch.Remember(old.At);
                await ResumeStopped.CloseAsync(window.Pid, window.Record);
                closed.Add(window);
            }
            yielded = agents && closed.Count == windows.Count;
            if (closed.Count == 0 && !yielded) return false;
            freed[number] = DateTime.UtcNow;
            // Yield only this Supervisor's known runs, after every owner window
            // was safely closed. Unknown headless work never qualifies above.
            if (yielded) await supervisor.YieldAsync(account, TimeSpan.FromMinutes(30));
            var free = false;
            for (var attempt = 0; attempt < 60 && !(free = await Task.Run(() => ResumeStopped.LiveProfiles([(dir, number)]).Count == 0)); attempt++)
                await Task.Delay(500);
            if (free && !disposed && settings.AutoSwitch && !limits.Switching && autoTask is not { IsCompleted: false })
                switched = await engine.SwitchNowAsync(number);
        }
        catch (Exception error) { Program.Log(error); }
        finally
        {
            // A refused switch or changed state still brings every window that
            // closed back on its original account, as the Windows tray does.
            foreach (var window in closed)
            {
                try
                {
                    try { ResumeStopped.CopyTranscript(window, ResumeStopped.DefaultDir); } catch (Exception error) { Program.Log(error); }
                    moves[$"{window.SessionId}#{number}"] = moves[ResumeStopped.MoveStamp(window.SessionId, DateTime.UtcNow)] = DateTime.UtcNow;
                    engine.LaunchConsole($"Claude · {account.ShortName} · moved", window.Cwd,
                        Launch.Run(number, settings.PreserveRemoteControl, ResumeStopped.ResumeArgs(window)));
                }
                catch (Exception error)
                {
                    Program.Log(error);
                    notify("Could not reopen a stopped window", $"In {window.Cwd}: cswap run {number} -- --resume {window.SessionId}");
                }
            }
            try { ResumeStopped.SaveMoves(movesPath, moves, DateTime.UtcNow); } catch (Exception error) { Program.Log(error); }
        }
        if (switched is { Switched: true })
            notify(closed.Count == 0 ? $"Agents freed #{number} for your login" : "Moved pinned windows to your login",
                Format.SwitchNotice(switched.From, switched.To) + (yielded ? ". Agents continue their work on another account." : ""));
        return closed.Count > 0 || yielded;
    }
}
