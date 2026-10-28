using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

/// Per-window, model-aware moves: a window on Fable spends the account's general usage and its own Fable window, so it moves
/// when either runs out, and only to an account with room in both; a window on another model never moves for Fable.
public class ModelSwitchTests
{
    const string Fable = "claude-fable-5-1", Sonnet = "claude-sonnet-5-5";
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    static readonly DateTimeOffset General = Now.AddDays(3), FableReset = Now.AddDays(5);

    /// `fable` null: an account with no Fable window of its own (no separate limit there).
    static Account A(int n, double five, double week, double? fable, bool active = false) =>
        new(n, $"{n}@example.com", active, "ok", new Usage(new Window(five, Now.AddHours(2)), new Window(week, General),
            fable is { } f ? [new Window(f, FableReset, "Fable")] : null, null));

    static StoppedSession Window(int account, string? model, bool atLimit, Limit? limit = null) =>
        new(1, "s", @"C:\w", @"C:\p", account, "t", atLimit, Limit: limit, OwnRefused: limit != null, Model: model);

    [Fact]
    public void TheModelIsTheLastRealReplysNotClaudeCodesOwnErrorLine()
    {
        string[] tail =
        [
            """{"type":"assistant","message":{"model":"claude-sonnet-5-5","content":[]}}""",
            """{"type":"assistant","message":{"model":"claude-fable-5-1","content":[]}}""",
            """{"type":"user","message":{"content":"go on"}}""",
            """{"type":"assistant","isApiErrorMessage":true,"message":{"model":"<synthetic>","content":[]}}""",
        ];
        Assert.Equal(Fable, ResumeStopped.ModelOf(tail));
        Assert.Null(ResumeStopped.ModelOf(["""{"type":"user","message":{"content":"hi"}}"""]));
    }

    [Fact]
    public void AFableLimitThatAsksForUsageCreditsNeverReopensWhereItWasRefused()
    {
        // As Claude Code 2.1.310 writes it: no rate_limit_info, no reset time, and #2's Fable reading still shows room.
        const string line = """{"type":"assistant","timestamp":"2026-10-24T04:49:52.558Z","message":{"model":"<synthetic>","role":"assistant","content":[{"type":"text","text":"You've reached your Fable limit. Run /usage-credits to continue or switch models with /model."}]},"apiError":"model_requires_usage_credits","error":"rate_limit","isApiErrorMessage":true,"apiErrorStatus":429}""";
        var limit = ResumeStopped.LimitOf([line]);
        Assert.Equal(("Fable", (DateTimeOffset?)null), (limit?.Window, limit?.ResetsAt));
        var accounts = new[] { A(1, 40, 50, 30), A(2, 10, 10, 60, active: true), A(3, 20, 20, 100) };
        Assert.Equal(1, ResumeStopped.Target(Window(2, Fable, atLimit: true, limit), accounts, _ => true, now: Now));
    }

    [Fact]
    public void AWindowIsJudgedOnItsGeneralUsageAndItsOwnModelsWindowOnly()
    {
        var a = A(1, 40, 50, 100);
        Assert.Equal(100, ResumeStopped.PeakFor(a, Fable));
        Assert.Equal(50, ResumeStopped.PeakFor(a, Sonnet)); // Fable's window is not Sonnet's
        Assert.Equal(ResumeStopped.Peak(a), ResumeStopped.PeakFor(a, null)); // unknown model: as before
        Assert.Equal(50, ResumeStopped.PeakFor(A(2, 40, 50, null), Fable)); // no Fable window there: its general usage binds
    }

    [Fact]
    public void AFableWindowOutOfFableMovesToAnAccountWithRoomInBoth()
    {
        // #1: Fable out. #2: general room, Fable out too. #3: general nearly out. #4: room in both.
        var accounts = new[] { A(1, 40, 50, 100), A(2, 10, 10, 100), A(3, 99, 60, 10), A(4, 30, 40, 20) };
        Assert.Equal(4, ResumeStopped.Target(Window(1, Fable, atLimit: true), accounts, _ => true, now: Now));
        // A Sonnet window on #1 has general room there: it is not stopped, and a finished turn does not move for Fable.
        Assert.Null(ResumeStopped.Target(Window(1, Sonnet, atLimit: false), accounts, _ => true, threshold: 90, now: Now));
    }

    [Fact]
    public void AFableWindowAlsoMovesWhenItsGeneralUsageRunsOut()
    {
        var accounts = new[] { A(1, 100, 50, 10), A(2, 20, 30, 100), A(3, 20, 30, 15) };
        Assert.Equal(3, ResumeStopped.Target(Window(1, Fable, atLimit: true), accounts, _ => true, now: Now)); // #2 has general room but no Fable
    }

    [Fact]
    public void AFinishedFableTurnMovesAtTheThresholdOfEither()
    {
        var accounts = new[] { A(1, 20, 30, 92), A(2, 20, 30, 95), A(3, 25, 35, 40) };
        Assert.Equal(3, ResumeStopped.Target(Window(1, Fable, atLimit: false), accounts, _ => true, threshold: 90, hysteresis: 5, now: Now));
        Assert.Null(ResumeStopped.Target(Window(1, Sonnet, atLimit: false), accounts, _ => true, threshold: 90, hysteresis: 5, now: Now));
    }

    [Fact]
    public void AnAccountRefusedForFableTakesNoFableWindowButStillTakesOthers()
    {
        var accounts = new[] { A(1, 100, 50, 10), A(2, 5, 5, 10), A(3, 30, 30, 30) };
        var scoped = new Dictionary<(int, string), DateTimeOffset> { [(2, "Fable")] = FableReset }; // a stale reading says #2 has Fable room
        Assert.Equal(3, ResumeStopped.Target(Window(1, Fable, atLimit: true), accounts, _ => true, now: Now, scopedRefused: scoped));
        Assert.Equal(2, ResumeStopped.Target(Window(1, Sonnet, atLimit: true), accounts, _ => true, now: Now, scopedRefused: scoped));
    }

    [Fact]
    public void ASevenDayRefusalIsFablesWhenItResetsWithFablesWindowOrOnlyFableReadsFull()
    {
        var a = A(1, 40, 60, 100);
        Assert.Equal("Fable", ResumeStopped.ScopeOf(new Limit("seven_day", FableReset, Now), a, Fable));
        Assert.Null(ResumeStopped.ScopeOf(new Limit("seven_day", General, Now), a, Fable)); // the general weekly window's reset
        Assert.Null(ResumeStopped.ScopeOf(new Limit("five_hour", FableReset, Now), a, Fable));
        Assert.Null(ResumeStopped.ScopeOf(new Limit("seven_day", FableReset, Now), a, Sonnet)); // Sonnet does not use Fable's window
        Assert.Null(ResumeStopped.ScopeOf(new Limit("seven_day", FableReset, Now), a, null));
        Assert.Equal("Fable", ResumeStopped.ScopeOf(new Limit("seven_day_fable", Now.AddDays(1), Now), a, Fable)); // a label naming it
        Assert.Equal("Fable", ResumeStopped.ScopeOf(new Limit("model_weekly", FableReset, Now), a, Fable)); // an unknown label: the windows decide
        Assert.Null(ResumeStopped.ScopeOf(new Limit("model_weekly", General, Now), a, Fable));
        // Both reset together: whichever reads full is the one that ran out.
        var together = new Account(1, "1@example.com", false, "ok", new Usage(new Window(40, Now), new Window(60, General), [new Window(100, General, "Fable")], null));
        Assert.Equal("Fable", ResumeStopped.ScopeOf(new Limit("seven_day", General, Now), together, Fable));
        var both = together with { Usage = new Usage(new Window(40, Now), new Window(100, General), [new Window(100, General, "Fable")], null) };
        Assert.Null(ResumeStopped.ScopeOf(new Limit("seven_day", General, Now), both, Fable)); // the account's own weekly is out: account-wide
    }

    [Fact]
    public void AFableRefusalMarksFablesWindowFullNotTheGeneralWeeklyOne()
    {
        var a = A(1, 40, 60, 80);
        var doc = ResumeStopped.LimitReport([Window(1, Fable, atLimit: true, new Limit("seven_day", FableReset, Now))], [a], Now, new HashSet<string>())!;
        Assert.Contains("\"sevenDay\":{\"pct\":60", doc);
        Assert.Contains("\"pct\":100", doc);
        Assert.Contains("\"name\":\"Fable\"", doc);
    }
}
