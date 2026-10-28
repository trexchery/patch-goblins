using System.Text.Json;

namespace PatchGoblins;

/// A folder assigned to an account (one entry of the engine's mappings.json).
public sealed record ProjectMapping(string Folder, string Email, string Org)
{
    /// Last path segment, for compact labels.
    public string Leaf => Path.GetFileName(Folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Folder;

    /// The engine's rule (switcher._find_account_slot): same email and same organization, "" when none.
    public bool Matches(Account a) => a.Provider == "claude" && Email == a.Email && Org == (a.OrganizationUuid ?? "");
}

/// Read-only view of <backup root>/mappings.json. The engine owns the file and its path
/// normalization (mappings.py); the dock changes it only through `cswap map` / `cswap unmap`.
public static class ProjectMap
{
    public static List<ProjectMapping> Load(string? path = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path ?? Path.Combine(StoreSecurity.BackupRoot, "mappings.json")));
            if (doc.RootElement is not { ValueKind: JsonValueKind.Object } root
                || !root.TryGetProperty("mappings", out var m) || m.ValueKind != JsonValueKind.Object) return [];
            var list = new List<ProjectMapping>();
            foreach (var p in m.EnumerateObject())
            {
                var email = Str(p.Value, "email");
                if (p.Name.Length > 0 && email.Length > 0) list.Add(new(p.Name, email, Str(p.Value, "organizationUuid")));
            }
            return [.. list.OrderBy(x => x.Folder, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    /// The account a Claude terminal in `folder` runs as: the nearest mapped ancestor's, as
    /// MappingStore.resolve picks it. Null when no mapping covers it or that account is gone.
    /// ponytail: no symlink resolution (the engine resolves them); map and use the real path.
    public static Account? AccountFor(string folder, IEnumerable<Account> accounts, IEnumerable<ProjectMapping> maps)
    {
        var dir = Path.GetFullPath(folder).TrimEnd('\\', '/');
        var best = maps.Select(m => (Map: m, Key: Path.GetFullPath(m.Folder).TrimEnd('\\', '/')))
            .Where(x => dir.Equals(x.Key, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || dir.StartsWith(x.Key + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(x => x.Key.Length)
            .Select(x => x.Map)
            .FirstOrDefault();
        return best == null ? null : accounts.FirstOrDefault(best.Matches);
    }
}

/// Engine arguments for starting Claude Code through `cswap run`.
public static class Launch
{
    /// `run [NUM] [--restore-rc] [-- claude args]`. Without NUM the engine resolves the working
    /// directory's mapping (else the default login). Engine flags go before `--`, which ends them.
    public static string[] Run(int? account, bool restoreRc, params string[] claudeArgs)
    {
        var a = new List<string> { "run" };
        if (account is { } n) a.Add(n.ToString());
        if (restoreRc) a.Add("--restore-rc");
        if (claudeArgs.Length > 0) { a.Add("--"); a.AddRange(claudeArgs); }
        return [.. a];
    }

    /// The general agent: not tied to any repo, one fixed folder, run with no account (`cswap run` in an unmapped
    /// folder is the default login, whichever it is at launch), so auto-switch keeps it on one with room.
    /// An existing `handoff-general` keeps its name: its Claude sessions are filed under that path.
    public static string GeneralFolder => Directory.Exists(Path.Combine(StoreSecurity.UserProfile, "handoff-general"))
        ? Path.Combine(StoreSecurity.UserProfile, "handoff-general") : Path.Combine(StoreSecurity.UserProfile, "patch-goblins-general");

    /// Continue-in Claude: through `cswap run` when an account is known, else null (launch claude directly).
    /// A folder-mapped account is left to the engine (no NUM); otherwise the active account is named.
    public static string[]? Continue(Account? mapped, Account? active, bool restoreRc, string prompt) =>
        (mapped ?? active) == null ? null : Run(mapped == null ? active!.Number : null, restoreRc, prompt);
}
