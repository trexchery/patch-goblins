using System.Text.Json;
using System.Text.RegularExpressions;

namespace PatchGoblins;

// Auto-Prime: start an account's fresh 5-hour window early with one tiny request, so it also resets
// early. Off by default: once on, Patch Goblins tracks the windows and primes each fresh one invisibly
// on its regular tick (a tray-menu click does it now). Turning it on doesn't settle whether Anthropic's
// terms allow this use of a subscription (their Consumer Terms restrict automated access); the owner decides.

/// What Patch Goblins keeps per account: diagnostics only, never the prompt or the reply.
public sealed class PrimeRecord
{
    /// The latest 5-hour reset seen in a live reading; the window after it is identified by it.
    public DateTimeOffset? LastResetsAt { get; set; }
    /// The window last primed, recorded before the request goes out: a crash never sends twice.
    public string? Window { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    /// sent, unknown (no clear answer: it may have gone out), verified, in use (something else started
    /// the window first), unverified, failed (refused before anything went out).
    public string? Outcome { get; set; }
    public string? Detail { get; set; }
}

/// Sent: Claude answered. Refused: the engine stopped it before Claude started, so nothing went out.
/// Unknown: anything else, so it may have gone out.
public enum PrimeResult { Sent, Refused, Unknown }

public sealed class Primer
{
    public static string DefaultPath => Path.Combine(DockSettings.Dir, "prime.json");

    /// A reading older than this may miss a window someone started meanwhile.
    static readonly TimeSpan MaxReadingAge = TimeSpan.FromMinutes(15);
    /// At the click, eligibility comes from a reading this recent: the engine's 3-minute floor, and some.
    public static readonly TimeSpan SendReadingAge = TimeSpan.FromMinutes(5);
    static readonly TimeSpan VerifyFor = TimeSpan.FromMinutes(30);
    /// The engine serves a cached reading for three minutes after it fetched one.
    static readonly TimeSpan RereadAfter = TimeSpan.FromSeconds(210);

    readonly string _path;
    bool _unreadable;
    DateTimeOffset? _rereadAt;

    public Primer(string path)
    {
        _path = path;
        Records = Load();
    }

    /// Keyed by email|organization: account numbers are reused after a remove.
    public Dictionary<string, PrimeRecord> Records { get; private set; }

    /// Why nothing can be primed: without its ledger Patch Goblins could prime a window twice.
    public string? Problem { get; private set; }

    public DateTimeOffset? LastPrime => Records.Values.Where(r => r.Outcome is "sent" or "verified" or "unverified").Max(r => r.SentAt);

    Dictionary<string, PrimeRecord> Load()
    {
        try
        {
            var records = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, PrimeRecord>>(File.ReadAllText(_path)) ?? [] : [];
            (_unreadable, Problem) = (false, null);
            return records;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Left as it is, never overwritten: the owner fixes or deletes it.
            (_unreadable, Problem) = (true, $"can't read {_path}: fix or delete it to prime again");
            return [];
        }
    }

    /// False, with the reason in Problem, when the ledger couldn't be written: priming waits for a save that works.
    public bool Save()
    {
        if (_unreadable) return false;
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(Records, new JsonSerializerOptions { WriteIndented = true }));
            Problem = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Problem = "can't save the prime ledger: " + e.Message;
            return false;
        }
    }

    static string Key(Account a) => a.Email + "|" + (a.OrganizationUuid ?? "");

    internal static bool Same(Account a, Account b) => Key(a) == Key(b);

    PrimeRecord Of(Account a) => Records.TryGetValue(Key(a), out var r) ? r : Records[Key(a)] = new();

    static string WindowId(PrimeRecord r) => r.LastResetsAt?.ToUniversalTime().ToString("O") ?? "idle";

    /// When a live reading was taken; last-known readings never count.
    static DateTimeOffset? FetchedAt(Account a, DateTimeOffset now) =>
        a is { Provider: "claude", UsageStatus: "ok", Usage: not null }
            ? a.UsageFetchedAt ?? (a.UsageAgeSeconds is { } s ? now - TimeSpan.FromSeconds(s) : null)
            : null;

    /// Never an excluded or API-key account, one with paid extra usage, or one near a weekly limit, so a
    /// prime shouldn't cost anything as far as the reading shows. The margin also covers unlimited extra
    /// usage, which the reading doesn't report: it only bills once a plan limit is reached.
    static bool Eligible(Account a) =>
        a is { Provider: "claude", Disabled: false, UsageStatus: "ok", Usage: { Spend: null } u }
        && (u.SevenDay?.Pct ?? 0) < 95 && (u.Scoped ?? []).All(w => w.Pct < 95);

    /// Accounts whose fresh 5-hour window hasn't started, read within `within` and after the previous
    /// window ended, and not primed for this window yet (a refused prime may be tried again). None while
    /// the ledger is unreadable or unsaved.
    public List<Account> Ready(IEnumerable<Account> accounts, DateTimeOffset now, TimeSpan? within = null) =>
        Problem != null ? [] :
        accounts.Where(a => Eligible(a) && a.Usage!.FiveHour is { Pct: <= 0, ResetsAt: null }
                            && FetchedAt(a, now) is { } at && now - at <= (within ?? MaxReadingAge) && Fresh(a, at)).ToList();

    bool Fresh(Account a, DateTimeOffset fetched)
    {
        if (!Records.TryGetValue(Key(a), out var r)) return true;
        if (r.LastResetsAt is { } last && fetched < last) return false; // read before the last window ended
        if (r.Window != WindowId(r)) return true;
        // Primed for this window: again only once that window is surely over (nobody may have seen it
        // start), or after a refusal, on a reading taken since. Anything that may have gone out waits.
        return r.SentAt is { } sent && (fetched >= sent + TimeSpan.FromHours(5) || r.Outcome == "failed" && fetched > sent);
    }

    /// Learns each live reading's window and settles primes on evidence. True when there's anything to save.
    public bool Observe(IEnumerable<Account> accounts, DateTimeOffset now)
    {
        if (_unreadable)
        {
            Records = Load(); // fixed or deleted since?
            if (_unreadable) return false;
        }
        var changed = Problem != null; // a save that failed is tried again
        foreach (var a in accounts)
        {
            if (FetchedAt(a, now) is not { } at || a.Usage!.FiveHour?.ResetsAt is not { } resets) continue;
            var r = Of(a);
            if (r is { Outcome: "sent" or "unknown", SentAt: { } sent } && at > sent)
            {
                // Evidence: the window now ends about five hours after the request (resets may be rounded to the hour).
                var started = resets >= sent + TimeSpan.FromHours(4) && resets <= sent + TimeSpan.FromHours(5) + TimeSpan.FromMinutes(10);
                (r.Outcome, r.Detail) = started ? ("verified", $"window resets {resets.ToLocalTime():HH:mm}") : ("in use", "the window had already started");
                changed = true;
            }
            if (r.LastResetsAt != resets)
            {
                r.LastResetsAt = resets;
                changed = true;
            }
        }
        foreach (var r in Records.Values.Where(r => r is { Outcome: "sent" or "unknown", SentAt: { } s } && now - s > VerifyFor))
        {
            (r.Outcome, r.Detail) = ("unverified", "no reading showed the new window");
            changed = true;
        }
        return changed;
    }

    /// A forced re-read for primes still waiting for evidence, once the engine's cached reading can show
    /// them, and no more often than that, until a reading settles them or they go unverified.
    public bool WantsReread(DateTimeOffset now) =>
        !(now - _rereadAt < RereadAfter)
        && Records.Values.Any(r => r is { Outcome: "sent" or "unknown", SentAt: { } s } && now - s >= RereadAfter);

    /// Counted only once a forced re-read came back: a failed one is tried again on the next tick.
    public void Reread(DateTimeOffset now) => _rereadAt = now;

    /// Recorded, durably, before the request goes out: a crash means a missed prime, never a second one.
    /// False when it couldn't be, and then nothing may be sent.
    public bool Begin(Account a, DateTimeOffset now)
    {
        if (Problem != null) return false;
        var r = Of(a);
        var was = (r.Window, r.SentAt, r.Outcome, r.Detail);
        (r.Window, r.SentAt, r.Outcome, r.Detail) = (WindowId(r), now, "sent", null);
        if (Save()) return true;
        (r.Window, r.SentAt, r.Outcome, r.Detail) = was;
        return false;
    }

    /// Claude's answer is only "sent": success needs a reading's evidence. A refusal may be tried again on
    /// a newer reading; an unclear end waits for evidence like a sent prime, or for the window to be over.
    public void Finish(Account a, PrimeResult result, string detail)
    {
        var r = Of(a);
        if (r.Outcome == "sent" && result != PrimeResult.Sent)
            (r.Outcome, r.Detail) = (result == PrimeResult.Refused ? "failed" : "unknown", detail);
        Save();
    }

    /// The tray menu's lines: why priming is paused, last prime, the next fresh window, failures.
    public List<string> Status(IReadOnlyList<Account> accounts, DateTimeOffset now)
    {
        var lines = new List<string>();
        if (Problem != null) lines.Add("Paused: " + Problem);
        if (LastPrime is { } last) lines.Add($"Last primed {last.ToLocalTime():ddd HH:mm}");
        if (accounts.Where(Eligible).Select(a => a.Usage!.FiveHour?.ResetsAt).Where(t => t > now).Min() is { } next)
            lines.Add($"Next fresh window ~{next.ToLocalTime():HH:mm}");
        foreach (var a in accounts)
            if (Records.TryGetValue(Key(a), out var r) && r.Outcome is "failed" or "unknown" or "unverified")
                lines.Add($"#{a.Number} {r.Outcome}: {r.Detail}");
        return lines;
    }
}

public sealed partial class Engine
{
    /// One "Hi": Haiku, low effort, one turn, no tools, MCP servers or customizations, nothing saved.
    /// The signed-in account runs on its own login (the engine's fast path: no switch, no copy); any
    /// other in its own sealed profile, refused if it became the signed-in one meanwhile.
    internal static string[] PrimeArgs(Account a) =>
    [
        "run", a.Number.ToString(), .. a.Active ? (string[])[] : ["--require-session"], "--",
        "-p", "Hi", "--model", "haiku", "--effort", "low", "--output-format", "json",
        "--no-session-persistence", "--safe-mode", "--strict-mcp-config", "--disable-slash-commands",
        "--setting-sources", "project", // not the owner's settings: no proxy, API-key helper or provider from there
        "--tools", "",
    ];

    /// No API key, token, endpoint, cloud provider, config folder or parent session from the tray's
    /// environment: a prime runs on the account's subscription login only. Git Bash's location stays
    /// (Claude Code needs it on Windows).
    internal static List<string> PrimeUnset(IEnumerable<string> names) =>
        names.Where(n => n.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
                         || n.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase) && !n.Equals("CLAUDE_CODE_GIT_BASH_PATH", StringComparison.OrdinalIgnoreCase)
                         || n.Equals("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase))
            .Concat(SessionMarkers(names)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// Sends one prime, silently: no window, no transcript.
    public async Task<(PrimeResult Result, string Detail)> PrimeAsync(Account a)
    {
        var dir = Path.Combine(DockSettings.Dir, "prime");
        Directory.CreateDirectory(dir); // an empty folder: no project instructions to read
        EngineResult r;
        try { r = await RunAsync(PrimeArgs(a), TimeSpan.FromSeconds(90), unset: PrimeUnset(Environment.GetEnvironmentVariables().Keys.OfType<string>().ToList()), workDir: dir); }
        catch (EngineException e) { return (PrimeResult.Unknown, e.Message); } // timed out: Claude may have sent it
        var json = r.Stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        try
        {
            if (json != null)
            {
                using var doc = JsonDocument.Parse(json);
                var e = doc.RootElement;
                if (e.TryGetProperty("type", out var t) && t.GetString() == "result")
                    return e.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.False
                        ? (PrimeResult.Sent, "sent")
                        : (PrimeResult.Unknown, Clip(e.TryGetProperty("result", out var why) ? why.ToString() : "Claude reported an error"));
            }
        }
        catch (JsonException) { }
        // The engine's own refusal: "Error: …" and no launch line, so Claude never started.
        var last = LastLine(r.Stderr);
        return json == null && r.ExitCode != 0 && !r.Stdout.Contains("aunching") && last != null && Ansi().Replace(last, "").StartsWith("Error: ")
            ? (PrimeResult.Refused, Clip(last))
            : (PrimeResult.Unknown, Clip(last ?? $"exited {r.ExitCode}"));
    }

    [GeneratedRegex(@"\e\[[0-9;]*m")]
    private static partial Regex Ansi();

    static string Clip(string s)
    {
        s = Redact(Ansi().Replace(s, "")).ReplaceLineEndings(" ").Trim();
        return s.Length > 120 ? s[..120] + "…" : s;
    }
}
