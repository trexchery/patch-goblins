using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PatchGoblins;

public static class StoreSecurity
{
    // Same resolution as the engine's Path.home() (USERPROFILE first).
    public static string UserProfile =>
        Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME") is { Length: > 0 } p ? p : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// The engine's store on Windows (paths.get_backup_root).
    public static string BackupRoot => OperatingSystem.IsLinux()
        ? Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
            ? xdg : Path.Combine(UserProfile, ".local", "share"), "claude-swap")
        : Path.Combine(UserProfile, ".claude-swap-backup");

    /// Replace the directory's DACL with "current user + SYSTEM only", no
    /// inherited entries, propagated to everything below it. Backups and
    /// session profiles inside then never inherit broader profile ACLs.
    public static void Harden(string dir)
    {
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            if (new DirectoryInfo(dir).LinkTarget != null) throw new IOException("Private data directory must not be a symbolic link");
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var item in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                File.SetUnixFileMode(item.FullName, item is DirectoryInfo
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(sec);
        // Files created by tools that set explicit ACLs keep them; reset children to inherit.
        foreach (var f in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
        {
            if (File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint)) continue;
            if (Directory.Exists(f))
            {
                var ds = new DirectorySecurity();
                ds.SetAccessRuleProtection(false, false);
                new DirectoryInfo(f).SetAccessControl(ds);
            }
            else
            {
                var fs = new FileSecurity();
                fs.SetAccessRuleProtection(false, false);
                new FileInfo(f).SetAccessControl(fs);
            }
        }
    }

    /// True when only the current user and SYSTEM hold access entries.
    public static bool IsHardened(string dir)
    {
        if (!OperatingSystem.IsWindows()) return (File.GetUnixFileMode(dir) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;
        var me = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var acl = new DirectoryInfo(dir).GetAccessControl();
        return acl.AreAccessRulesProtected && acl.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .All(r => r.IdentityReference.Equals(me) || r.IdentityReference.Equals(system));
    }
}

public static class ClaudeCli
{
    /// Claude Code's executable (native installer, winget, or npm shim).
    public static string? Find() =>
        !OperatingSystem.IsWindows() ? Engine.FindOnPath("claude")
            ?? new[] { Path.Combine(StoreSecurity.UserProfile, ".local", "bin", "claude"), "/opt/homebrew/bin/claude", "/usr/local/bin/claude" }.FirstOrDefault(File.Exists)
        : Engine.FindOnPath("claude.exe")
        ?? new[] { Path.Combine(StoreSecurity.UserProfile, ".local", "bin", "claude.exe") }.FirstOrDefault(File.Exists)
        ?? Engine.FindOnPath("claude.cmd");

    /// Number of running Claude Code processes (default-profile and pinned), including ones the updater
    /// renamed to claude.exe.old.<stamp>.<pid>.
    public static int RunningCount()
    {
        var procs = Process.GetProcesses();
        var n = 0;
        foreach (var p in procs)
        {
            try { if (p.ProcessName.Split('.')[0].Equals("claude", StringComparison.OrdinalIgnoreCase)) n++; } catch { }
            p.Dispose();
        }
        return n;
    }

    /// Official `claude auth login` against a throwaway CLAUDE_CONFIG_DIR, then
    /// `cswap enroll` imports it. The default login is never touched.
    public static Task<string> EnrollAsync(Engine engine)
    {
        var claude = Find() ?? throw new EngineException("Claude Code (claude.exe) not found on PATH.");
        return LoginInTempHomeAsync(engine, claude, ["auth", "login"], "CLAUDE_CONFIG_DIR", ".credentials.json", ["enroll"], null);
    }

    internal static string EnrollRoot => Path.Combine(DockSettings.Dir, "enroll");

    /// Deletes login dirs left behind when the tray exited or crashed mid-login.
    /// Startup only: the single-instance mutex means no enrollment is running.
    internal static void PurgeEnrollLeftovers(string root)
    {
        try { foreach (var d in Directory.GetDirectories(root)) try { Directory.Delete(d, recursive: true); } catch { } }
        catch { } // best-effort; usually the folder doesn't exist
    }

    /// Runs `exe args` in a visible console with `homeVar` pointing at a fresh,
    /// hardened temp dir, then `cswap <enrollArgs> <dir>`; the dir is always deleted.
    internal static async Task<string> LoginInTempHomeAsync(Engine engine, string exe, string[] args, string homeVar,
        string expectedFile, string[] enrollArgs, Action<string, ProcessStartInfo>? prepare)
    {
        var root = EnrollRoot;
        StoreSecurity.Harden(root);
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var psi = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo(Engine.Cmd) { ArgumentList = { "/c", exe } }
                : new ProcessStartInfo(exe);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = false; // visible console: the user completes the official login there
            psi.WorkingDirectory = dir;
            psi.Environment[homeVar] = dir;
            prepare?.Invoke(dir, psi);
            using (var p = Process.Start(psi) ?? throw new EngineException($"could not start {Path.GetFileName(exe)}"))
                await p.WaitForExitAsync();
            if (!File.Exists(Path.Combine(dir, expectedFile)))
                throw new EngineException("Login was not completed.");
            var output = await engine.CommandAsync([.. enrollArgs, dir]);
            return output.Trim();
        }
        finally
        {
            // The directory holds a live credential; never leave it behind.
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

/// Codex CLI and Grok Build: detection and enrollment. Their engines live in
/// src/claude_swap/providers; Patch Goblins only starts the official login.
public static class ProviderCli
{
    public static readonly string[] All = ["codex", "grok"];
    public static readonly string[] Every = ["claude", .. All];

    /// Any CLI Patch Goblins can open, Claude included.
    public static string? FindAny(string provider) => provider == "claude" ? ClaudeCli.Find() : Find(provider);

    /// "Continue in" targets for work interrupted in `source`. Grok sessions can't be read yet, so Grok is a target only.
    public static IEnumerable<string> HandoffTargets(string source) =>
        source == "grok" ? [] : Every.Where(p => p != source && FindAny(p) != null);

    public static string Title(string provider) => provider switch { "codex" => "Codex", "grok" => "Grok", _ => "Claude" };

    public static string? Find(string provider)
    {
        if (!OperatingSystem.IsWindows()) return Engine.FindOnPath(provider)
            ?? new[] { Path.Combine(StoreSecurity.UserProfile, ".local", "bin", provider), Path.Combine("/opt/homebrew/bin", provider), Path.Combine("/usr/local/bin", provider) }.FirstOrDefault(File.Exists);
        var home = StoreSecurity.UserProfile;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Engine.FindOnPath(provider + ".exe")
            ?? new[]
            {
                Path.Combine(home, ".local", "bin", provider + ".exe"),
                Path.Combine(home, "." + provider, "bin", provider + ".exe"),
            }.FirstOrDefault(File.Exists)
            ?? Engine.FindOnPath(provider + ".cmd")
            ?? new[] { Path.Combine(appData, "npm", provider + ".cmd") }.FirstOrDefault(File.Exists);
    }

    /// Worth polling: the CLI is installed or accounts were enrolled earlier.
    public static bool Present(string provider) =>
        Find(provider) != null || File.Exists(Path.Combine(StoreSecurity.BackupRoot, "providers", provider, "accounts.json"));

    public static Task<string> EnrollAsync(Engine engine, string provider)
    {
        var exe = Find(provider) ?? throw new EngineException($"{Title(provider)} CLI ({provider}) not found on PATH.");
        var homeVar = provider == "codex" ? "CODEX_HOME" : "GROK_HOME";
        return ClaudeCli.LoginInTempHomeAsync(engine, exe, ["login"], homeVar, "auth.json", [provider, "enroll"], (dir, psi) =>
        {
            // Codex may otherwise pick the OS keyring; the engine imports auth.json.
            if (provider == "codex") File.WriteAllText(Path.Combine(dir, "config.toml"), "cli_auth_credentials_store = \"file\"\n");
            // An inline or relocated grok credential would bypass the temp home.
            psi.Environment.Remove("GROK_AUTH");
            psi.Environment.Remove("GROK_AUTH_PATH");
        });
    }
}
