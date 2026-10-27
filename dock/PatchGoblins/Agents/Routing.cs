using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatchGoblins;

/// How a step runs: the Director's recommendation as validated, plus what failures escalated. Model, effort and
/// ultracode are three separate choices; none implies another.
public sealed class Route
{
    /// trivial, standard or hard.
    public string Difficulty { get; set; } = "standard";
    /// haiku, sonnet or opus.
    public string Model { get; set; } = "sonnet";
    /// low, medium, high, xhigh or max.
    public string Effort { get; set; } = "medium";
    /// Recommended; a run gets it only when the owner allows ultracode and there is room (Routing.UltracodeOn).
    public bool Ultracode { get; set; }
    /// The Director's reason, or what escalated it.
    public string? Why { get; set; }
    /// "director", "default", or "escalated".
    public string Source { get; set; } = "default";
    public int Escalations { get; set; }
    /// A verification (or pull request check) failed before: the next one escalates. The first may be a flake.
    public bool VerifyFailed { get; set; }
    /// A failure raised the model: it waits for that model rather than fall back to a weaker one.
    public bool ModelRaised { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public string Line => $"{Model} · {Effort}{(Ultracode ? " · ultracode" : "")} ({Difficulty}, {Source})";
}

/// What one run is started with.
public sealed record RunSettings(string Model, string Effort, bool Ultracode);

/// An account's usage when a run started or ended, in percent; not money.
/// `At`: when the reading was taken; `ResetsAt`: the soonest reset among its windows (a delta across it means nothing).
public sealed record QuotaSnap(double? FiveHour, double? SevenDay, double? Model, DateTimeOffset? At = null, DateTimeOffset? ResetsAt = null);

/// Deterministic routing: validates the Director's recommendations, picks reviewer and planner settings, escalates
/// after genuine failures, and gates ultracode. The Director advises; this decides.
public static class Routing
{
    public static readonly string[] Models = ["haiku", "sonnet", "opus"], Efforts = ["low", "medium", "high", "xhigh", "max"], Difficulties = ["trivial", "standard", "hard"];

    /// Quota points an account needs below the threshold before a run there may fan out with ultracode.
    /// ponytail: a flat conservative margin; derive it from metrics.jsonl once ultracode runs have been measured.
    public const double UltracodeRoom = 30;

    public static Route Default => Validate(null, null, null, null, null, "default");

    /// The Director's words made safe: unknown values fall back by difficulty, Haiku only for trivial work.
    public static Route Validate(string? difficulty, string? model, string? effort, bool? ultracode, string? why, string source = "director")
    {
        var d = Pick(difficulty, Difficulties) ?? (difficulty?.Trim().ToLowerInvariant() is "critical" or "complex" ? "hard" : "standard");
        var m = Pick(model, Models) ?? d switch { "trivial" => "haiku", "hard" => "opus", _ => "sonnet" };
        if (m == "haiku" && d != "trivial") m = "sonnet"; // Haiku only for genuinely trivial, low-risk work
        var e = Pick(effort, Efforts) ?? d switch { "trivial" => "low", "hard" => "high", _ => "medium" };
        var w = why?.ReplaceLineEndings(" ").Trim();
        return new Route { Difficulty = d, Model = m, Effort = e, Ultracode = ultracode == true, Why = w is { Length: > 200 } ? w[..199] + "…" : w, Source = source };
    }

    static string? Pick(string? value, string[] allowed) => value?.Trim().ToLowerInvariant() is { } v && allowed.Contains(v) ? v : null;

    /// A model's "true", "yes" or true.
    public static bool? Flag(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => e.GetString()?.Trim().ToLowerInvariant() is "true" or "yes" or "on" ? true : e.GetString()?.Trim().ToLowerInvariant() is "false" or "no" or "off" ? false : null,
        _ => null,
    };

    /// A fresh reviewer, never Haiku: the review is the quality gate. Read-only, so no ultracode.
    public static RunSettings Review(Route work) => work.Difficulty switch
    {
        "trivial" => new("sonnet", "low", false),
        "hard" => new("opus", "high", false),
        _ => new(work.Model == "opus" ? "opus" : "sonnet", "medium", false),
    };

    /// A goal's plan reads the whole project and decides its architecture: Opus. Routing the backlog judges bounded
    /// descriptions and runs whenever new issues arrive: Sonnet, at high effort, keeps Opus for the work that needs it.
    public static readonly RunSettings Plan = new("opus", "high", false), Triage = new("sonnet", "high", false);

    /// After a genuine failure: one lever moves, chosen by the kind of failure. A review that found problems wants better
    /// reasoning (model first); a check that keeps failing wants more care (effort first); with both at the top, ultracode.
    /// Returns null when nothing is left to raise. `ultracode`: the owner allows it (otherwise that lever does nothing).
    public static Route? Escalate(Route r, string kind, bool ultracode)
    {
        string[] order = kind == "review" ? ["model", "effort", "ultracode"] : ["effort", "model", "ultracode"];
        foreach (var lever in order)
        {
            var next = new Route { Difficulty = r.Difficulty, Model = r.Model, Effort = r.Effort, Ultracode = r.Ultracode, Source = "escalated", Escalations = r.Escalations + 1, VerifyFailed = r.VerifyFailed || kind == "verify", ModelRaised = r.ModelRaised };
            switch (lever)
            {
                case "model" when Array.IndexOf(Models, r.Model) is var i && i < Models.Length - 1:
                    (next.Model, next.ModelRaised) = (Models[i + 1], true);
                    break;
                case "effort" when Array.IndexOf(Efforts, r.Effort) is var j && j < Efforts.Length - 1:
                    next.Effort = Efforts[j + 1];
                    break;
                case "ultracode" when ultracode && !r.Ultracode:
                    next.Ultracode = true;
                    break;
                default:
                    continue;
            }
            next.Why = $"{lever} raised after a {kind} failure";
            return next;
        }
        return null;
    }

    /// Ultracode runs only when the owner turned it on, it is recommended, the account runs nothing else, and its usage
    /// (every window it has, the model's own included) is known and at least UltracodeRoom below the threshold. Any model, any effort.
    public static bool UltracodeOn(Route r, bool allowed, Account a, int othersOnAccount, double threshold) =>
        allowed && r.Ultracode && othersOnAccount == 0 && Peak(Snap(a, r.Model)) <= threshold - UltracodeRoom;

    /// A reading's highest window, as AccountPicker.Peak: one the account does not have (organization plans report no
    /// overall weekly window) counts as empty; null when neither the 5-hour nor the weekly window is known.
    public static double? Peak(QuotaSnap? s) =>
        s is { } q && (q.FiveHour ?? q.SevenDay) != null ? Math.Max(Math.Max(q.FiveHour ?? 0, q.SevenDay ?? 0), q.Model ?? 0) : null;

    public static QuotaSnap? Snap(Account? a, string? model, DateTimeOffset? now = null)
    {
        if (a?.Usage is not { } u) return null; // live only: the last good reading may be hours old
        var t = now ?? DateTimeOffset.Now;
        double? Live(Window? w) => w is null ? null : w.ResetsAt is { } r && r <= t ? 0 : w.Pct;
        var scoped = model == null ? null : (u.Scoped ?? []).Where(w => w.Name?.Contains(model, StringComparison.OrdinalIgnoreCase) == true).Select(Live).Max();
        var resets = new[] { u.FiveHour, u.SevenDay }.Concat(u.Scoped ?? []).Select(w => w?.ResetsAt).Where(r => r > t).Min();
        return new(Live(u.FiveHour), Live(u.SevenDay), scoped, a.UsageFetchedAt ?? (a.UsageAgeSeconds is { } s ? t - TimeSpan.FromSeconds(s) : null), resets);
    }

    /// The flags that fix a run's model, effort and ultracode, so nothing is inherited from the profile's settings. The
    /// allowlist keeps subagents and workflow agents on these three models too; --settings is not restored on a resume,
    /// so every launch passes it, with the run's `hooks` (Claude Code reads only one --settings).
    public static string[] Args(RunSettings s, object? hooks = null)
    {
        var settings = new Dictionary<string, object> { ["ultracode"] = s.Ultracode, ["availableModels"] = Models };
        if (hooks != null) settings["hooks"] = hooks;
        return ["--model", s.Model, "--effort", s.Effort, "--settings", JsonSerializer.Serialize(settings)];
    }
}
