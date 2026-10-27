using Xunit;

namespace PatchGoblins.Tests;

/// The tray's and the macOS backend's unattended switch share one call. Where nothing follows the two logins of a held account
/// (macOS) it stays `--exclusive`; elsewhere (Windows) it is not, and the engine's own follow keeps the logins together.
public class SwitchNowArgsTests
{
    [Fact]
    public void MacOSKeepsTheExclusiveRefusalOfAHeldAccount() =>
        Assert.Equal(["switch", "3", "--json", "--exclusive"], Engine.SwitchNowArgs(3, macOS: true));

    [Fact]
    public void WindowsSwitchesOntoAHeldAccount() =>
        Assert.Equal(["switch", "3", "--json"], Engine.SwitchNowArgs(3, macOS: false));
}
