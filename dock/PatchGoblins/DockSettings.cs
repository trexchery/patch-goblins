using System.Text.Json;
using Microsoft.Win32;

namespace PatchGoblins;

/// Dock-only preferences. Auto-switch thresholds live in the engine's
/// settings.json (`cswap config`) so the CLI and the dock never disagree.
public sealed class DockSettings
{
    public bool AutoSwitch { get; set; }
    public bool AutoSwitchPolicyAccepted { get; set; }
    public int RefreshSeconds { get; set; } = 180;
    public bool Pinned { get; set; }
    public int? PinnedX { get; set; }
    public int? PinnedY { get; set; }
    public bool PreserveRemoteControl { get; set; } = true; // keys absent from an older settings.json keep these defaults
    public bool AutoProjectAccount { get; set; }
    public bool QuotaNotifications { get; set; }
    public string? AgentLastRepo { get; set; }
    public bool AutoPrime { get; set; }
    public int AgentIssues { get; set; } = 1;
    public int AgentOpenPrs { get; set; } = 10;
    public bool AgentDraftPrs { get; set; }
    public bool AgentUltracode { get; set; }
    public bool AskApproval { get; set; } = true;
    public bool AutoLine { get; set; } = true; // the switch line follows each account's burn (SwitchLine); off: the engine's fixed threshold // off: agents never wait for a yes, except on files the repository's policy protects
    public DateTimeOffset? AgentSummaryAt { get; set; }
    public string? TerminalFolder { get; set; } // where "Open terminal" starts browsing; null: the user folder

    public const int MinRefreshSeconds = 60;

    /// The `PATCH_GOBLINS_<name>` variable, or its pre-rename `HANDOFF_<name>`.
    public static string? Env(string name) =>
        Environment.GetEnvironmentVariable("PATCH_GOBLINS_" + name) is { Length: > 0 } v ? v : Environment.GetEnvironmentVariable("HANDOFF_" + name);

    /// PATCH_GOBLINS_DATA runs a separate, isolated instance (development, benchmarks).
    public static string Dir => Env("DATA") is { Length: > 0 } d ? d : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "patch-goblins");

    static string FilePath => Path.Combine(Dir, "settings.json");

    public static DockSettings Load(string? path = null)
    {
        try
        {
            var s = JsonSerializer.Deserialize<DockSettings>(File.ReadAllText(path ?? FilePath)) ?? new();
            s.RefreshSeconds = Math.Max(MinRefreshSeconds, s.RefreshSeconds);
            return s;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string? path = null) =>
        AtomicFile.WriteAllText(path ?? FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

public static class AtomicFile
{
    /// Write to a sibling temp file, then rename over the target, so a crash
    /// never leaves a half-written file.
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(fs))
            {
                w.Write(text);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}

public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "patch-goblins";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string;
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --minimized");
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }
}
