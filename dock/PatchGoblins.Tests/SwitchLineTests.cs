using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

/// The automatic switch line: 100% minus one long turn (10 min) at the account's burn over the last 15 minutes, 80–98%.
public class SwitchLineTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow;

    static Account A(double five, double week = 10, double? fable = null, int minute = 0) =>
        new(1, "a@example.com", true, "ok", new Usage(new Window(five, T0.AddHours(3)), new Window(week, T0.AddDays(3)),
            fable is { } f ? [new Window(f, T0.AddDays(5), "Fable")] : null, null)) { UsageFetchedAt = T0.AddMinutes(minute) };

    static SwitchLine Fed(params (int Minute, double Five, double? Fable)[] readings)
    {
        var line = new SwitchLine();
        foreach (var (m, five, fable) in readings) line.Observe([A(five, fable: fable, minute: m)], T0.AddMinutes(m));
        return line;
    }

    [Fact]
    public void AQuietAccountIsUsedAlmostToTheEnd() =>
        Assert.Equal(SwitchLine.Ceiling, Fed((0, 50, null), (10, 50.5, null)).For(A(50.5, minute: 10), null)); // 0.05 pt/min

    [Fact]
    public void ABusyAccountIsLeftEarlyEnoughForOneMoreLongTurn() =>
        Assert.Equal(90, Fed((0, 40, null), (5, 45, null), (10, 50, null)).For(A(50, minute: 10), null), 1); // 1 pt/min: 100 - 10

    [Fact]
    public void AHammeredAccountNeverMovesLaterThanTheFloor() =>
        Assert.Equal(SwitchLine.Floor, Fed((0, 10, null), (5, 40, null)).For(A(40, minute: 5), null)); // 6 pt/min

    [Fact]
    public void TooFewReadingsGiveTheSafeDefault()
    {
        Assert.Equal(SwitchLine.Unknown, new SwitchLine().For(A(50), null));
        Assert.Equal(SwitchLine.Unknown, Fed((0, 50, null), (1, 60, null)).For(A(60, minute: 1), null)); // a minute apart: too little to judge
    }

    [Fact]
    public void AResetStartsTheBurnOver()
    {
        var line = Fed((0, 90, null), (5, 95, null), (6, 2, null), (12, 4, null)); // reset between 5 and 6
        Assert.Equal(96.7, line.For(A(4, minute: 12), null), 1); // only 2 pt over the 6 min since the reset, not the 90s before it
    }

    [Fact]
    public void OldReadingsLeaveTheWindowSoABurstEndsAndTheLineComesBack()
    {
        var line = Fed((0, 10, null), (5, 40, null), (30, 41, null), (40, 42, null)); // a burst, then quiet for half an hour
        Assert.Equal(SwitchLine.Ceiling, line.For(A(42, minute: 40), null));
    }

    [Fact]
    public void FablesBurnCountsForAFableWindowAndNotForOthers()
    {
        var line = Fed((0, 20, 40), (10, 21, 60)); // general quiet, Fable 2 pt/min
        Assert.Equal(80, line.For(A(21, fable: 60, minute: 10), "claude-fable-5-1"), 1);
        Assert.Equal(SwitchLine.Ceiling, line.For(A(21, fable: 60, minute: 10), "claude-sonnet-5-5"));
    }

    [Fact]
    public void AWindowMovesAtItsOwnAccountsLineAndOnlyToAnAccountUnderItsOwn()
    {
        Account B(int n, double five) => new(n, $"{n}@example.com", false, "ok", new Usage(new Window(five, T0.AddHours(2)), new Window(10, T0.AddDays(3)), null, null));
        var accounts = new[] { B(1, 92), B(2, 93), B(3, 40) };
        var s = new StoppedSession(1, "s", @"C:\w", @"C:\p", 1, "t", AtLimit: false);
        var lines = new Dictionary<int, double> { [1] = 90, [2] = 98, [3] = 98 }; // #1 burns fast; #2 and #3 are quiet
        Assert.Equal(3, ResumeStopped.Target(s, accounts, _ => true, threshold: 97, hysteresis: 5, now: T0, line: a => lines[a.Number]));
        Assert.Null(ResumeStopped.Target(s, accounts, _ => true, threshold: 97, hysteresis: 5, now: T0)); // fixed 97: #1 at 92 stays
    }
}
