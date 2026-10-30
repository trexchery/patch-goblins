namespace PatchGoblins;

internal sealed partial class BackendUpkeep
{
    readonly Dictionary<int, DateTime> freed = [];
    readonly HashSet<int> heldProfiles = [];

    /// Free profiles held only by this Supervisor's headless agents. User
    /// terminals always remain open, including idle or paused sessions.
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
                        : "It becomes available after its work finishes and you close its terminal."));
                return false;
            }
            // There is no reliable confirmation that Terminal.app actually
            // started a replacement, and terminating the original discards
            // terminal state. Never free an account by closing user sessions.
            if (windows.Count > 0)
            {
                Held($"#{number} is in use by a terminal. Close or move that session yourself when it is safe to free the account.");
                return false;
            }
        }
        catch (Exception error) { Program.Log(error); return false; }

        // Only this Supervisor's known headless runs can be yielded. Unknown
        // processes and user terminals have already been ruled out above.
        if (!agents) return false;
        if (!ResumeStopped.DefaultProfileClear(ResumeStopped.DefaultDir))
        {
            Held("The default login is in use or its sessions cannot be inspected. Agents keep working until it is safe to switch.");
            return false;
        }
        SwitchResult? switched = null;
        try
        {
            freed[number] = DateTime.UtcNow;
            await supervisor.YieldAsync(account, TimeSpan.FromMinutes(30));
            var free = false;
            for (var attempt = 0; attempt < 60 && !(free = await Task.Run(() => ResumeStopped.LiveProfiles([(dir, number)]).Count == 0)); attempt++)
                await Task.Delay(500);
            if (free && !disposed && settings.AutoSwitch && !limits.Switching && autoTask is not { IsCompleted: false })
                switched = await engine.SwitchNowAsync(number);
        }
        catch (Exception error) { Program.Log(error); }

        if (switched is { Switched: true })
            notify($"Agents freed #{number} for your login",
                Format.SwitchNotice(switched.From, switched.To) + ". Agents continue their work on another account.");
        return true;
    }
}
