using System.Runtime.InteropServices;
using System.Text;

namespace PatchGoblins;

/// Where a terminal window sat on screen (a snapped grid cell, or anywhere), so a window the tray closes is reopened
/// in the same place. Screen pixels: the tray is per-monitor DPI aware, so they hold across monitors.
public sealed record WindowSpot(int Left, int Top, int Right, int Bottom, bool Maximized)
{
    static readonly object Attaching = new(); // a process has one console: one attach at a time
    static readonly SemaphoreSlim Opening = new(1, 1); // one new window at a time, so each finds its own

    /// The window a console program draws in: the Windows Terminal window that owns its pseudoconsole, or conhost's own. 0: none.
    public static nint WindowOf(int pid)
    {
        lock (Attaching)
        {
            if (!AttachConsole(pid)) return 0;
            try { return GetConsoleWindow() is var c && c != 0 ? GetAncestor(c, GA_ROOTOWNER) : 0; }
            finally { FreeConsole(); }
        }
    }

    /// Where `pid`'s window is now; null when it has none, or is minimized (it then opens wherever the terminal puts it).
    public static WindowSpot? Of(int pid)
    {
        try { return WindowOf(pid) is var h && h != 0 && !IsIconic(h) && GetWindowRect(h, out var r) ? new(r.L, r.T, r.R, r.B, IsZoomed(h)) : null; }
        catch (Exception e) { Program.Log(e); return null; } // never in the way of a move
    }

    /// Runs `launch`, which opens one terminal window, and puts that window at `spot` (null: left where it opens).
    /// True once the new window is up, so a caller can then close the one it replaces; false when none showed within ~10 s.
    // ponytail: the new window is the first terminal window that was not there before; one opened by hand in the same few seconds can take its place.
    public static async Task<bool> OpenAtAsync(WindowSpot? spot, Action launch)
    {
        await Opening.WaitAsync();
        try
        {
            var before = Terminals();
            launch();
            for (var i = 0; i < 40; i++) // up to 10 s
            {
                await Task.Delay(250);
                if (Terminals().FirstOrDefault(h => !before.Contains(h)) is var h && h != 0)
                {
                    if (spot != null) await spot.PlaceAsync(h);
                    return true;
                }
            }
            Program.Log("opened window not found" + (spot != null ? ": left where it opened" : ""));
            return false;
        }
        finally { Opening.Release(); }
    }

    async Task PlaceAsync(nint h)
    {
        // Twice: Windows Terminal sizes a new window once more as it finishes opening.
        for (var i = 0; i < 2; i++)
        {
            ShowWindow(h, SW_RESTORE);
            SetWindowPos(h, 0, Left, Top, Right - Left, Bottom - Top, SWP_NOZORDER | SWP_NOACTIVATE);
            if (Maximized) ShowWindow(h, SW_MAXIMIZE); // on the monitor it was just put on
            await Task.Delay(500);
        }
    }

    /// Visible top-level terminal windows: Windows Terminal's and conhost's.
    static HashSet<nint> Terminals()
    {
        var found = new HashSet<nint>();
        EnumWindows((h, _) =>
        {
            var name = new StringBuilder(64);
            if (IsWindowVisible(h) && GetClassName(h, name, name.Capacity) > 0 && name.ToString() is "CASCADIA_HOSTING_WINDOW_CLASS" or "ConsoleWindowClass")
                found.Add(h);
            return true;
        }, 0);
        return found;
    }

    const uint GA_ROOTOWNER = 3, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    const int SW_RESTORE = 9, SW_MAXIMIZE = 3;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    delegate bool EnumProc(nint h, nint lParam);

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll")] static extern nint GetConsoleWindow();
    [DllImport("user32.dll")] static extern nint GetAncestor(nint h, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] static extern bool IsZoomed(nint h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint h, StringBuilder name, int max);
}
