using System.Runtime.InteropServices;
using Xunit;

namespace PatchGoblins.Tests;

public sealed class WindowSpotTests
{
    [Fact]
    public void HiddenCompatibilityWindowWithValidRectangleIsNotATerminalSpot()
    {
        // An isolated ConPTY experiment returned a hidden, non-minimized 16x16
        // compatibility HWND. A successful GetWindowRect alone accepted it.
        var window = CreateWindowEx(0, "STATIC", "synthetic compatibility window", 0,
            0, 0, 16, 16, 0, 0, 0, 0);
        Assert.NotEqual(nint.Zero, window);
        try
        {
            Assert.True(GetWindowRect(window, out var rect));
            Assert.True(rect.Right > rect.Left && rect.Bottom > rect.Top);
            Assert.False(IsIconic(window));
            Assert.Null(WindowSpot.Capture(window));
        }
        finally { DestroyWindow(window); }
    }

    [Fact]
    public void MissingWindowIsNotATerminalSpot() => Assert.Null(WindowSpot.Capture(0));

    [Fact]
    public void VisibleTopLevelWindowUsesItsActualRectangle()
    {
        // Visible flag, but offscreen with no activation: no interactive window
        // is placed over the user's work surface.
        var window = CreateWindowEx(0x08000000 /* WS_EX_NOACTIVATE */, "STATIC", "synthetic visible window",
            0x90000000 /* WS_POPUP | WS_VISIBLE */, -32000, -32000, 120, 80, 0, 0, 0, 0);
        Assert.NotEqual(nint.Zero, window);
        try
        {
            Assert.True(GetWindowRect(window, out var rect));
            var spot = Assert.IsType<WindowSpot>(WindowSpot.Capture(window));
            Assert.Equal(new WindowSpot(rect.Left, rect.Top, rect.Right, rect.Bottom, false), spot);
        }
        finally { DestroyWindow(window); }
    }

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint window);
}
