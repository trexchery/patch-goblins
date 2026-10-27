namespace PatchGoblins;

public static class Format
{
    /// "in 2h 10m" within a day, "Tue 14:00" beyond.
    public static string When(DateTimeOffset t, DateTimeOffset now)
    {
        var d = t - now;
        if (d <= TimeSpan.Zero) return "now";
        if (d < TimeSpan.FromHours(1)) return $"in {Math.Max(1, (int)d.TotalMinutes)}m";
        if (d < TimeSpan.FromDays(1)) return $"in {(int)d.TotalHours}h {d.Minutes:00}m";
        return t.ToLocalTime().ToString("ddd HH:mm");
    }

    public static string Age(double seconds) =>
        seconds < 3600 ? $"{(int)(seconds / 60)}m ago" : seconds < 86400 ? $"{(int)(seconds / 3600)}h ago" : $"{(int)(seconds / 86400)}d ago";

    /// Compact tray tooltip (Windows caps it at 127 chars).
    public static string Tooltip(Snapshot? s, bool auto)
    {
        var active = s?.Accounts.FirstOrDefault(a => a.Active);
        var text = active == null
            ? "Patch Goblins — no managed account active"
            : $"Patch Goblins — {(string.IsNullOrEmpty(active.Alias) ? active.Email : active.Alias)}" +
              (active.ShownUsage is { } u
                  ? $"\n5h {u.FiveHour?.Pct ?? 0:0}% · week {u.SevenDay?.Pct ?? 0:0}%"
                  : "") +
              (auto ? "\nAuto-switch on" : "");
        return text.Length <= 127 ? text : text[..127];
    }

    /// The single, quiet balloon after a Claude switch.
    public static string SwitchNotice(AccountRef? from, AccountRef? to) =>
        (from == null ? "switched" : $"{from.Email} switched out") + $" · {to?.Email ?? "(none)"} active";

    public static double Peak(Account? a) =>
        a?.ShownUsage is { } u
            ? new[] { u.FiveHour?.Pct ?? 0, u.SevenDay?.Pct ?? 0 }.Concat((u.Scoped ?? []).Select(s => s.Pct)).Max()
            : 0;
}
