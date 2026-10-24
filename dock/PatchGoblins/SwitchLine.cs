namespace PatchGoblins;

/// The automatic switch line: per account, how full it may get before a window (or the default login) moves off it.
/// 100% minus what one long turn would spend at the account's recent burn rate, so a quiet account is used almost to the
/// end and one being hammered is left before a turn can run it dry. Burn is per window (5-hour, weekly, each model's own),
/// from the readings the tray already takes; a window counts only for what uses it (Fable's for a window on Fable).
public sealed class SwitchLine
{
    public const double Floor = 80, Ceiling = 98, Unknown = 95;
    /// How far back the burn is measured, and the turn it must leave room for.
    // ponytail: one fixed lookahead for every turn; per-session turn lengths if moves still land mid-turn
    static readonly TimeSpan Span = TimeSpan.FromMinutes(15), Ahead = TimeSpan.FromMinutes(10), Least = TimeSpan.FromMinutes(3);

    readonly Dictionary<(string Account, string Window), List<(DateTimeOffset At, double Pct)>> _seen = [];

    /// Records a listing. A reading's own fetch time, so the engine's cached reading is not counted twice.
    public void Observe(IEnumerable<Account> accounts, DateTimeOffset now)
    {
        foreach (var a in accounts)
        {
            if (a.Usage is not { } u) continue;
            var at = a.UsageFetchedAt ?? (a.UsageAgeSeconds is { } s ? now - TimeSpan.FromSeconds(s) : now);
            foreach (var (name, w) in Windows(u))
            {
                var list = _seen.TryGetValue((Key(a), name), out var l) ? l : _seen[(Key(a), name)] = [];
                if (list.Count > 0 && list[^1].At >= at) continue;
                if (list.Count > 0 && w.Pct < list[^1].Pct) list.Clear(); // the window reset: its burn starts over
                list.Add((at, w.Pct));
                list.RemoveAll(p => now - p.At > Span + Least);
            }
        }
    }

    /// Percentage points per minute the account spends on what `model` uses (null: the 5-hour and weekly windows, and the
    /// scoped ones the engine watches); null until there are readings `Least` apart.
    public double? Burn(Account a, string? model)
    {
        double? most = null;
        foreach (var (name, _) in a.Usage is { } u ? Windows(u) : [])
        {
            if (name.StartsWith("scoped:") && !Counts(name[7..], model)) continue;
            if (!_seen.TryGetValue((Key(a), name), out var list) || list.Count < 2) continue;
            var (first, last) = (list.First(p => list[^1].At - p.At <= Span), list[^1]);
            var minutes = (last.At - first.At).TotalMinutes;
            if (minutes < Least.TotalMinutes) continue;
            var rate = Math.Max(0, (last.Pct - first.Pct) / minutes);
            most = Math.Max(most ?? 0, rate);
        }
        return most;
    }

    /// The line for a window on `model` on this account (null model: the default login's, as the engine judges it).
    public double For(Account a, string? model) =>
        Burn(a, model) is { } rate ? Math.Clamp(100 - rate * Ahead.TotalMinutes, Floor, Ceiling) : Unknown;

    static bool Counts(string scope, string? model) =>
        model != null ? model.Contains(scope, StringComparison.OrdinalIgnoreCase) : ResumeStopped.Models.Contains("all") || ResumeStopped.Models.Contains(scope);

    static IEnumerable<(string Name, Window W)> Windows(Usage u) =>
        new[] { ("five_hour", u.FiveHour), ("seven_day", u.SevenDay) }.Where(x => x.Item2 != null).Select(x => (x.Item1, x.Item2!))
            .Concat((u.Scoped ?? []).Where(w => w.Name != null).Select(w => ("scoped:" + w.Name, w)));

    static string Key(Account a) => a.Provider + ":" + a.Email;
}
