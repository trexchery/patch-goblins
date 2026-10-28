namespace PatchGoblins;

internal sealed partial class BackendUpkeep
{
    bool priming;

    public IReadOnlyList<string> PrimeStatus() => primer.Status(snapshot()?.Accounts ?? [], DateTimeOffset.Now);

    public async Task<IReadOnlyList<string>> PrimeNowAsync()
    {
        if (disposed || priming) return ["A prime is already running."];
        var fresh = await engine.ListAsync(refresh: true);
        if (primer.Observe(fresh.Accounts, DateTimeOffset.Now)) primer.Save();
        await PrimeCandidatesAsync(primer.Ready(fresh.Accounts, DateTimeOffset.Now));
        return PrimeStatus();
    }

    async Task PrimeCandidatesAsync(IReadOnlyList<Account> shown, Func<bool>? allowed = null)
    {
        if (priming || disposed) return;
        priming = true;
        try
        {
            var fresh = await engine.ListAsync(refresh: true);
            if (primer.Observe(fresh.Accounts, DateTimeOffset.Now)) primer.Save();
            foreach (var account in primer.Ready(fresh.Accounts, DateTimeOffset.Now, Primer.SendReadingAge)
                         .Where(a => shown.Any(b => Primer.Same(a, b))))
            {
                if (disposed || allowed?.Invoke() == false || !primer.Begin(account, DateTimeOffset.Now)) break;
                var (result, detail) = await engine.PrimeAsync(account);
                primer.Finish(account, result, detail);
                if (result != PrimeResult.Sent) notify("Auto-Prime", $"#{account.Number}: {detail}");
            }
            await refresh();
        }
        finally { priming = false; }
    }
}
