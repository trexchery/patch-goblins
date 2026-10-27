using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PatchGoblins;

/// The agents terminal: an ordinary Claude Code session in the repository, opened like any other
/// Patch Goblins terminal (`cswap run`, so the folder's assigned account or the default login), plus
/// session-only files passed on the command line. Nothing in the owner's own settings changes.
///   settings.json  the `Patch Goblins · agents` status line, Patch Goblins' theme, and permission for its read-only tools
///   mcp.json       the Patch Goblins MCP bridge for this repository
///   statusline.sh  the owner's own status line (if any), with the label on its last row
///   theme/         a theme-only plugin: the Patch Goblins theme (green accent) on the owner's own built-in theme
public static class AgentsTerminal
{
    /// The tab's title; the status line says `Status` alone.
    public const string Label = "Patch Goblins · agents", Status = "Patch Goblins";

    /// Seconds between Claude's own status-line refreshes: each one moves the gradient a step.
    public const int RefreshSeconds = 5;

    /// The green loop in 72 steps. Neighbouring letters sit 3 steps apart, and each refresh moves the
    /// gradient 9 steps, about three letters: a step that reads as motion (a full loop in 40 s).
    public const int Hues = 72;
    const int FrameStep = 9, LetterStep = 3;

    /// The built-in themes the Patch Goblins theme can sit on: the owner's own, so only the greens change.
    /// Any other (auto, a custom theme, none set) gets a dark base.
    static readonly string[] BuiltInThemes = ["dark", "light", "dark-daltonized", "light-daltonized", "dark-ansi", "light-ansi"];

    static string Dir(string root, string repo) => Path.Combine(root, "terminal", AgentStore.KeyFor(repo));

    public static void Open(Engine engine, string root, string repo, bool restoreRc, string state)
    {
        var dir = Dir(root, repo);
        Directory.CreateDirectory(dir);
        SetState(root, repo, state);
        foreach (var old in Directory.GetFiles(dir, "user-statusline*"))
            try { File.Delete(old); } catch (IOException) { } // a terminal still open just renders its own again
        var script = Path.Combine(dir, "statusline.sh");
        // LF only: Git Bash runs it, and a stray CR would end up inside every token.
        File.WriteAllText(script, StatusScript(UserStatusLine(repo), Posix(Path.Combine(dir, "state")), Posix(Path.Combine(dir, "user-statusline"))).ReplaceLineEndings("\n"));
        var plugin = WriteTheme(dir, UserSetting(repo, null, "theme") is JsonValue v && v.TryGetValue<string>(out var theme) ? theme : null);
        var settings = Path.Combine(dir, "settings.json");
        var session = new JsonObject
        {
            // Sourced, not started: the status line already runs in a Git Bash; no second process.
            ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = ". " + BashQuote(Posix(script)), ["refreshInterval"] = RefreshSeconds },
            ["permissions"] = new JsonObject { ["allow"] = new JsonArray("mcp__patch-goblins__agents_status", "mcp__patch-goblins__agents_details", "mcp__patch-goblins__agents_doctor") },
        };
        session["theme"] = $"custom:{ThemePlugin}:{ThemeSlug}";
        File.WriteAllText(settings, session.ToJsonString());
        var mcp = Path.Combine(dir, "mcp.json");
        File.WriteAllText(mcp, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["patch-goblins"] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = Environment.ProcessPath,
                    ["args"] = new JsonArray("agents-mcp", "--repo", repo),
                },
            },
        }.ToJsonString());
        var leaf = Path.GetFileName(repo.TrimEnd('\\', '/'));
        engine.LaunchConsole($"{Label} · {leaf}", repo, Mascot(dir), Launch.Run(null, restoreRc,
            ["--settings", settings, "--mcp-config", mcp, "--plugin-dir", plugin]));
    }

    /// The goblin in place of Claude Code's mascot, for this session alone: `mascot.js` (see it) written beside the
    /// session's files and preloaded into Claude Code through BUN_OPTIONS, which its Bun runtime reads. Nothing is
    /// installed and no file of Claude's changes. The path goes in its 8.3 form where it holds a space (BUN_OPTIONS
    /// splits on spaces); where there is none, the goblin is skipped rather than risk a broken option.
    internal static IReadOnlyDictionary<string, string> Mascot(string dir)
    {
        var script = Path.Combine(dir, "mascot.js");
        using (var s = typeof(AgentsTerminal).Assembly.GetManifestResourceStream("mascot.js")!)
        using (var f = File.Create(script)) s.CopyTo(f);
        var path = script.Contains(' ') && OperatingSystem.IsWindows() ? ShortPath(script) : script;
        if (path == null || path.Contains(' ')) return new Dictionary<string, string>();
        var had = Environment.GetEnvironmentVariable("BUN_OPTIONS");
        // Forward slashes: BUN_OPTIONS reads a backslash as an escape and would drop it.
        return new Dictionary<string, string> { ["BUN_OPTIONS"] = (string.IsNullOrWhiteSpace(had) ? "" : had + " ") + "--preload " + path.Replace('\\', '/') };
    }

    static string? ShortPath(string path)
    {
        var sb = new StringBuilder(260);
        return GetShortPathName(path, sb, sb.Capacity) is > 0 and < 260 ? sb.ToString() : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern int GetShortPathName(string longPath, StringBuilder shortPath, int size);

    const string ThemePlugin = "goblin", ThemeSlug = "patch-goblins"; // the plugin's name prefixes its commands: /goblin:needs

    /// The Patch Goblins theme, a theme-only plugin loaded for this session alone with --plugin-dir and selected through the
    /// session's --settings: the owner's own built-in theme with a green brand accent (the spinner and the assistant
    /// label) and a green mascot color, as if picked in /theme. Nothing goes into ~/.claude and the owner's theme setting
    /// is never touched, so every other terminal keeps its look. The mascot's pixels are not a theme's to change (nor a
    /// mod's: no hook draws the header); `Mascot` swaps them for the goblin's, which this color then paints.
    internal static string WriteTheme(string dir, string? ownerTheme)
    {
        var baseTheme = BuiltInThemes.Contains(ownerTheme) ? ownerTheme! : "dark";
        var (body, accent, shimmer) = baseTheme.StartsWith("light") ? (Brand.LightHex, Brand.LightHex, "#43A047") : (Brand.DarkHex, "#6FD36F", "#A6EBA6"); // the body is the tray's goblin green
        var plugin = Path.Combine(dir, "theme");
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        Directory.CreateDirectory(Path.Combine(plugin, "themes"));
        File.WriteAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"), new JsonObject
        {
            ["name"] = ThemePlugin,
            ["version"] = "1.0.0",
            ["description"] = "Patch Goblins agents terminal: a green accent and its commands",
        }.ToJsonString());
        Directory.CreateDirectory(Path.Combine(plugin, "commands"));
        foreach (var (name, text) in Commands) File.WriteAllText(Path.Combine(plugin, "commands", name + ".md"), text);
        File.WriteAllText(Path.Combine(plugin, "themes", ThemeSlug + ".json"), new JsonObject
        {
            ["name"] = "Patch Goblins",
            ["base"] = baseTheme,
            ["overrides"] = new JsonObject { ["clawd_body"] = body, ["claude"] = accent, ["claudeShimmer"] = shimmer },
        }.ToJsonString());
        return plugin;
    }

    /// The agents terminal's own slash commands, for this session alone (the plugin is loaded with --plugin-dir).
    /// They only steer Claude to the Patch Goblins tools; what the tools allow is unchanged.
    internal static readonly (string Name, string Text)[] Commands =
    [
        ("needs", """
            ---
            description: What Patch Goblins needs from you, and answer it
            argument-hint: "[push #21 | answer #2 <text> | retry #2]"
            allowed-tools: mcp__patch-goblins__agents_status, mcp__patch-goblins__agents_details, mcp__patch-goblins__agents_queue, mcp__patch-goblins__agents_goal
            ---
            Arguments: $ARGUMENTS

            - No arguments: call agents_status. List only what waits for the owner (items needing them, goals with a question),
              one line each: what it is, why it waits, and the exact `/goblin:needs …` that answers it
              (`push <item>` for a held push, `answer <item> <text>` for a question, `retry <item>` to run it again).
              Nothing waits: say so in one line.
            - `push <item>`: approve its held push as it is: agents_queue with action "answer", that item, and no text.
            - `answer <item> <text>`: agents_queue with action "answer", that item and that text; for a goal (g1) use agents_goal.
            - `retry <item>`: agents_queue with action "retry" and that item.

            After an action, report its result in one line. Never act on an item the owner did not name.
            """),
        ("goal", """
            ---
            description: Give the Patch Goblins Director a goal to plan and work through
            argument-hint: "<what you want done, and what done looks like>"
            allowed-tools: mcp__patch-goblins__agents_goal, mcp__patch-goblins__agents_status
            ---
            Goal: $ARGUMENTS

            No goal given: call agents_goal with action "list" and show each goal in one line.
            Otherwise call agents_goal with action "set", a short title (at most 8 words) and the owner's words as the text,
            unchanged. Add checks only if the owner named commands. Then say in one line that the Director is planning it.
            """),
    ];

    /// The state behind the status line: needs you, paused or off are said; working and idle show the name alone.
    public static string StateOf(RepoAgents? repo) =>
        repo is not { Authorized: true } ? "off"
        : repo.Paused ? "paused"
        : repo.Items.Any(i => i.State == ItemState.NeedsOwner) || repo.Goals.Any(g => g.State == GoalState.NeedsOwner) ? "attention"
        : repo.Items.Any(i => i.State is ItemState.Working or ItemState.Checking) || repo.Goals.Any(g => g.Run is { Live: true }) ? "working"
        : "idle";

    /// What each goblin is on, one status-line row each under the label: the item, its account and its pull request,
    /// the row a link (OSC 8) to the pull request once there is one. Titles come from GitHub: control characters go.
    /// The status line's file: the state (with a link to the first thing that needs the owner), then a row per goblin and per question.
    public static string StatusOf(RepoAgents? repo, string? web) =>
        StateOf(repo) + (StateOf(repo) == "attention" && Asks(repo!, web).Select(a => a.Link).FirstOrDefault(l => l != null) is { } first ? " " + first : "") + WorkOf(repo, web);

    public static string WorkOf(RepoAgents? repo, string? web = null)
    {
        if (repo is not { Authorized: true }) return "";
        static string Who(Run r) => Clean(r.Email.Split('@')[0], 20);
        var rows = repo.Items.Where(i => i is { State: ItemState.Working or ItemState.Checking, Run.Live: true }).Select(i =>
        {
            var text = $"{Clean(i.Label, 60)} · {Who(i.Run!)}" + (i.Pr is { } pr ? $" · PR #{pr}" : "") + (i.State == ItemState.Checking ? " · checking" : "");
            return Link(i.PrUrl is { } url && url.StartsWith("https://github.com/") ? url : null, text);
        }).Concat(repo.Goals.Where(g => g.Run is { Live: true }).Select(g => $"planning {Clean(g.Title, 60)} · {Who(g.Run!)}"));
        return string.Concat(rows.Select(r => "\n▸ " + r)) + string.Concat(Asks(repo, web).Select(a => "\n? " + Link(a.Link, a.Text)));
    }

    /// What "needs you" means, each with the command that answers it, linked to where to look: a question to its issue
    /// (an answer there counts), a held push to its worktree (the change is not on GitHub yet).
    static IEnumerable<(string Text, string? Link)> Asks(RepoAgents repo, string? web)
    {
        string? Issue(int? n) => n is { } k && web != null ? $"{web}/issues/{k}" : null;
        foreach (var i in repo.Items.Where(i => i.State == ItemState.NeedsOwner))
            yield return ($"{Clean(i.Label, 50)} · " + i.HoldFor switch
            {
                "push" => $"waits to push: /goblin:needs push {Ref(i)}",
                "review" => $"review notes remain: /goblin:needs push {Ref(i)}, or answer what to change",
                "sensitive" or "workflow" => $"touches protected files: look, then /goblin:needs push {Ref(i)}",
                _ => "has a question: /goblin:needs",
            }, i.HoldFor != null && i.Worktree is { } wt ? new Uri(wt).AbsoluteUri : Issue(i.Issue));
        foreach (var g in repo.Goals.Where(g => g.State == GoalState.NeedsOwner))
            yield return ($"goal {g.Id} {Clean(g.Title, 40)} · has a question: /goblin:needs", Issue(g.Issue));
    }

    static string Clean(string s, int max) { var t = new string(s.Where(c => !char.IsControl(c)).ToArray()); return t.Length > max ? t[..(max - 1)] + "…" : t; }

    /// An OSC 8 link around the text: Ctrl+click opens it in Windows Terminal.
    static string Link(string? url, string text) =>
        url is { } u && !u.Any(c => char.IsControl(c) || c == ' ') ? $"\u001b]8;;{u}\u001b\\{text}\u001b]8;;\u001b\\" : text;

    /// The repository's GitHub page from its origin remote (https or ssh), looked up once per repository; null off GitHub.
    internal static string? WebOf(string repo)
    {
        lock (Webs)
        {
            if (Webs.TryGetValue(repo, out var known)) return known;
            string? url = null;
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", ["-C", repo, "remote", "get-url", "origin"]) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
                url = Web(p.StandardOutput.ReadToEnd().Trim());
                p.WaitForExit(5000);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return Webs[repo] = url;
        }
    }

    static readonly Dictionary<string, string?> Webs = [];

    internal static string? Web(string remote) =>
        System.Text.RegularExpressions.Regex.Match(remote, @"^(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)([\w.-]+/[\w.-]+?)(?:\.git)?/?$") is { Success: true } m
            ? "https://github.com/" + m.Groups[1].Value : null;

    static string Ref(WorkItem i) => i.Issue is { } n ? $"#{n}" : i.Id;

    public static void SetState(string root, string repo, string state)
    {
        var dir = Dir(root, repo);
        if (!Directory.Exists(dir)) return; // no terminal was ever opened for it
        try { File.WriteAllText(Path.Combine(dir, "state"), state + "\n"); }
        catch (IOException) { }
    }

    /// The status line the owner already has for this folder, highest-precedence file first.
    internal static string? UserStatusLine(string repo, string? home = null)
    {
        try
        {
            return UserSetting(repo, home, "statusLine") is JsonObject s && s["type"]?.GetValue<string>() == "command"
                   && s["command"]?.GetValue<string>() is { Length: > 0 } c ? c : null;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return null; }
    }

    /// One of the owner's Claude Code settings for this folder, highest-precedence file first.
    internal static JsonNode? UserSetting(string repo, string? home, string key)
    {
        home ??= StoreSecurity.UserProfile;
        foreach (var file in new[]
                 {
                     Path.Combine(repo, ".claude", "settings.local.json"),
                     Path.Combine(repo, ".claude", "settings.json"),
                     Path.Combine(home, ".claude", "settings.json"),
                 })
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject o && o[key] is { } value) return value;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return null;
    }

    /// Bash builtins only for the label: the frame follows the clock, so each refresh (every few seconds,
    /// and on every message) shifts the gradient one step. The whole line is one gradient, the state
    /// word included; the dots stay neutral. Nothing runs between refreshes.
    /// The owner's own status line re-runs only when the conversation changed; idle refreshes reuse it
    /// (one cache per session: two terminals on one repository never show each other's).
    internal static string StatusScript(string? userCommand, string statePath, string cachePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Patch Goblins agents label; rewritten whenever the agents terminal opens.");
        sb.AppendLine("IFS= read -r -d '' __ad_in");
        if (userCommand != null)
        {
            sb.AppendLine("__ad_cache=" + BashQuote(cachePath));
            sb.AppendLine("""[[ $__ad_in =~ \"session_id\":\"([^\"]*)\" ]] && __ad_cache+=-${BASH_REMATCH[1]}""");
            sb.AppendLine("__ad_tr=");
            sb.AppendLine("""[[ $__ad_in =~ \"transcript_path\":\"([^\"]*)\" ]] && __ad_tr=${BASH_REMATCH[1]//\\\\//}""");
            sb.AppendLine("""if [[ -z $__ad_tr || ! -f $__ad_cache || $__ad_tr -nt $__ad_cache ]]; then""");
            sb.AppendLine("""  __ad_out=$(printf '%s' "$__ad_in" | eval """ + BashQuote(userCommand) + " 2>/dev/null)");
            sb.AppendLine("""  printf '%s' "$__ad_out" > "$__ad_cache" 2>/dev/null""");
            sb.AppendLine("else");
            sb.AppendLine("""  IFS= read -r -d '' __ad_out < "$__ad_cache" """);
            sb.AppendLine("fi");
            sb.AppendLine("""[ -n "$__ad_out" ] && printf '%s\n' "$__ad_out" """);
        }
        sb.AppendLine("__ad_w=()");
        sb.AppendLine(OperatingSystem.IsWindows() ? "mapfile -t __ad_w 2>/dev/null < " + BashQuote(statePath)
            : "while IFS= read -r __ad_row; do __ad_w+=(\"$__ad_row\"); done < " + BashQuote(statePath));
        sb.AppendLine("__ad_l=${__ad_w[0]:-off}");
        sb.AppendLine("__ad_s=${__ad_l%% *}");
        sb.AppendLine("""__ad_u=; [[ $__ad_l == *' '* ]] && __ad_u=${__ad_l#* }""");
        sb.AppendLine("__ad_p=(" + string.Join(' ', Palette().Select(c => $"'{c}'")) + ")");
        sb.AppendLine("case $__ad_s in");
        foreach (var (state, text) in Lines)
            sb.AppendLine($"  {state}) __ad_c=({string.Join(' ', text.Select(c => BashQuote(c.ToString())))}) ;;");
        sb.AppendLine("esac");
        sb.AppendLine(OperatingSystem.IsWindows() ? "printf -v __ad_t '%(%s)T' -1" : "__ad_t=$(date +%s)");
        sb.AppendLine($"__ad_f=$(( __ad_t / {RefreshSeconds} * {FrameStep} ))");
        sb.AppendLine("__ad_o=");
        sb.AppendLine("""for __ad_i in "${!__ad_c[@]}"; do""");
        sb.AppendLine("  case ${__ad_c[__ad_i]} in");
        sb.AppendLine("    ' ') __ad_o+=' ' ;;");
        sb.AppendLine("""    '·') __ad_o+=$'\e[38;2;128;128;128m·' ;;""");
        sb.AppendLine($$"""    *) __ad_o+=$'\e[38;2;'"${__ad_p[(__ad_f + __ad_i * {{LetterStep}}) % {{Hues}}]}m${__ad_c[__ad_i]}" ;;""");
        sb.AppendLine("  esac");
        sb.AppendLine("done");
        // "needs you" links to the first thing that needs the owner.
        sb.AppendLine("""if [[ -n $__ad_u ]]; then printf '\e]8;;%s\e\\%s\e[0m\e]8;;\e\\\n' "$__ad_u" "$__ad_o"; else printf '%s\e[0m\n' "$__ad_o"; fi""");
        // The goblins at work, one row each (the state file's later lines).
        sb.AppendLine("""for __ad_r in "${__ad_w[@]:1}"; do printf '\e[38;2;160;160;160m%s\e[0m\n' "$__ad_r"; done""");
        return sb.ToString();
    }

    /// The line for each state; needs you, paused and off say so, working and idle show the name alone
    /// (also for a state file from an older Patch Goblins).
    internal static readonly (string State, string Text)[] Lines =
    [
        ("attention", Status + " · needs you"),
        ("paused", Status + " · paused"),
        ("off", Status + " · off"),
        ("*", Status),
    ];

    /// The goblin greens as `r;g;b`, from lime to spring green and back so the loop has no seam: soft enough to read on dark
    /// and light terminals alike.
    internal static IEnumerable<string> Palette() =>
        Enumerable.Range(0, Hues).Select(k => Hsl(80 + 85 * (1 - Math.Cos(2 * Math.PI * k / Hues)) / 2, 0.55, 0.68)).Select(c => $"{c.Item1};{c.Item2};{c.Item3}");


    internal static (int, int, int) Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }

    static string BashQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// Git Bash eats backslashes: C:\x\y becomes C:/x/y.
    static string Posix(string path) => path.Replace('\\', '/');
}
