using System.Text.RegularExpressions;

namespace PatchGoblins;

/// The one shell Patch Goblins runs verification, policy checks and goal checks with (AgentProcess.StartShell), and what a
/// command must look like to run under it. A command is never rewritten: one written for another shell is refused where
/// it comes in (a goal's checks, a new verification command or policy), or the owner is asked before it would run.
public static class CheckShell
{
    public enum Kind { Cmd, Posix }

    /// This machine's runner: cmd.exe on Windows, /bin/sh elsewhere.
    public static Kind Runner => OperatingSystem.IsWindows() ? Kind.Cmd : Kind.Posix;

    /// The policy's `os` for this machine.
    public static string Os => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// For prompts and tool descriptions: what runs the commands, and how to write one that needs another shell.
    public static string Describe(Kind runner) => runner == Kind.Cmd
        ? "Each command runs as one cmd.exe command line (`cmd /d /s /c`) on Windows, in the checkout's root: chain with `&&`, " +
          "give the programs after it a variable with `set NAME=value&& program` (cmd expands %NAME% on that same line before `set` runs), " +
          "discard output with `>NUL`. Unix shell syntax (`NAME=value cmd`, `$(...)`, `$VAR`, " +
          "`;` between commands, for/while loops, `/dev/null`) does not work there: write it for PowerShell instead, as " +
          "`powershell -NoProfile -NonInteractive -Command \"...\"`, or as `bash -c '...'` only where Git Bash is what `bash` runs."
        : "Each command runs with /bin/sh -c in the checkout's root. cmd.exe syntax (`%VAR%`, `set NAME=value`, `>NUL`) does not work there.";

    static readonly string[] Shells = ["bash", "sh", "zsh", "dash", "wsl", "pwsh", "powershell", "cmd"];

    /// Why `command` cannot run under `runner`, or null when it can. A command that starts by naming a shell is that
    /// shell's to parse, so only its first word is judged. Text in double quotes is a program's argument under either shell.
    public static string? Problem(string command, Kind runner)
    {
        var first = Regex.Match(command.TrimStart(), @"^""?([^""\s]+)").Groups[1].Value;
        var exe = Path.GetFileNameWithoutExtension(first.Replace('/', '\\').Split('\\')[^1]).ToLowerInvariant();
        if (Shells.Contains(exe)) return null;
        var bare = Regex.Replace(command, @"""(?:[^""\\]|\\.)*""", "\"\""); // arguments in double quotes are the program's
        if (runner == Kind.Posix) bare = Regex.Replace(bare, @"'[^']*'", "''");
        foreach (var (pattern, why) in runner == Kind.Cmd ? ForCmd : ForPosix)
            if (Regex.Match(bare, pattern) is { Success: true } m)
                return $"`{m.Value.Trim()}` is {why}";
        return null;
    }

    /// Where a command starts: the line's start, or after a separator. A lookbehind, so the match quotes only the offending text.
    const string Start = @"(?<=^|[;&|(]\s*)";

    static readonly (string Pattern, string Why)[] ForCmd =
    [
        (Start + @"[A-Za-z_][A-Za-z0-9_]*=[^\s=]*(?=\s+[^\s&|;])", "a Unix inline environment assignment; cmd.exe runs it as a program name"),
        (@"\$\(", "Unix command substitution; cmd.exe passes it on as text"),
        (@"\$\{?[A-Za-z_?#@*!0-9]", "a Unix variable; cmd.exe expands %NAME%"),
        (@"`[^`]+`", "Unix command substitution; cmd.exe passes it on as text"),
        (@";\s*(?:do|then|else|fi|done|esac)\b", "a Unix shell loop or condition"),
        (Start + @"(?:\[\[?\s|export\s|source\s|unset\s)", "Unix shell syntax cmd.exe has no command for"),
        (@"(?:\|\||&&|;)\s*(?:true|false)\b", "a Unix builtin cmd.exe does not have"),
        (@"\S\s*;\s+\S", "a Unix command separator; cmd.exe chains with &&"),
        (@"/dev/null", "a Unix path; cmd.exe discards output to NUL"),
        (@"(?:^|\s)~/", "a Unix home path; cmd.exe does not expand ~"),
    ];

    static readonly (string Pattern, string Why)[] ForPosix =
    [
        (@"%[A-Za-z_][A-Za-z0-9_]*%", "a cmd.exe variable; sh expands $NAME"),
        (Start + @"set\s+[A-Za-z_][A-Za-z0-9_]*=", "a cmd.exe assignment; sh writes NAME=value"),
        (@">\s*NUL\b", "a cmd.exe device; sh discards output to /dev/null"),
        (Start + @"(?:if\s+(?:not\s+)?exist|copy|del|xcopy|rmdir\s+/s)\s", "a cmd.exe command sh does not have"),
    ];

    /// The first command that cannot run here, as a refusal for whoever proposed it; null when all can.
    public static string? Refusal(IEnumerable<string> commands, Kind runner, string what) =>
        commands.Select(c => (c, why: Problem(c, runner))).FirstOrDefault(x => x.why != null) is ({ } bad, { } reason)
            ? $"{what} `{Brief(bad)}` cannot run here: {reason}. {Describe(runner)}"
            : null;

    static string Brief(string s) => s.Length > 120 ? s[..119] + "…" : s;
}
