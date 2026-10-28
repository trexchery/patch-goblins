using System.Diagnostics;
using System.Text;

namespace PatchGoblins;

public sealed record CliResult(int Code, string Out, string Err)
{
    public bool Ok => Code == 0;
    /// Last meaningful line of stderr (else stdout), redacted, for owner-facing notes.
    public string Why => Engine.Redact(
        (Err + "\n" + Out).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"exit {Code}");
}

/// Short-lived git and gh calls made by the supervisor itself (with the owner's normal credentials,
/// unlike workers). Never interactive: prompts would hang a tray with no console.
public sealed class Cli(string git, string? gh)
{
    public string Git { get; } = git;
    public string? Gh { get; } = gh;
    /// Tests shorten it, so a hung call is killed and logged before their own deadline.
    public TimeSpan GhTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public static Cli? Locate() =>
        Engine.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git") is { } git
            ? new Cli(git, Engine.FindOnPath(OperatingSystem.IsWindows() ? "gh.exe" : "gh")) : null;

    public Task<CliResult> GitAsync(string cwd, params string[] args) => RunAsync(Git, cwd, TimeSpan.FromMinutes(5), args);

    public Task<CliResult> GhAsync(string cwd, params string[] args) =>
        Gh == null ? Task.FromResult(new CliResult(127, "", "gh (GitHub CLI) is not installed")) : RunAsync(Gh, cwd, GhTimeout, args);

    public static async Task<CliResult> RunAsync(string exe, string cwd, TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = cwd,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        psi.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            Program.Log($"{Path.GetFileNameWithoutExtension(exe)} {string.Join(' ', args.Take(2))} timed out after {timeout.TotalSeconds:0} s");
            return new CliResult(124, "", $"{Path.GetFileNameWithoutExtension(exe)} timed out");
        }
        return new CliResult(p.ExitCode, await stdout, await stderr);
    }
}
