using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PatchGoblins;

/// POSIX counterpart of the Windows Job Object adapter. The same Supervisor owns
/// its work, checks and recovery. A detached helper keeps file stdio independent
/// from the backend and a process group provides descendant cleanup.
public sealed class AgentProcess : IDisposable
{
    readonly Process process;
    readonly string record;
    readonly bool outlive;
    bool disposed;
    int? exitCode;
    public int Pid { get; }
    public long CreatedTicks { get; }
    internal static bool BelowNormal = true;

    AgentProcess(Process process, string record, bool outlive)
    {
        this.process = process;
        this.record = record;
        this.outlive = outlive;
        Pid = process.Id;
        CreatedTicks = process.StartTime.ToUniversalTime().Ticks;
    }

    static string Record(string job) => Path.Combine(DockSettings.Dir, "processes",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job))).ToLowerInvariant() + ".json");

    public static AgentProcess Start(string exe, IReadOnlyList<string> args, string workDir, string jobName,
        string stdinPath, string stdoutPath, string stderrPath, IReadOnlyDictionary<string, string?>? env = null) =>
        Start(exe, args, workDir, jobName, stdinPath, stdoutPath, stderrPath, env, true);

    public static AgentProcess StartShell(string commandLine, string workDir, string jobName, string stdinPath, string logPath,
        IReadOnlyDictionary<string, string?>? env = null) =>
        Start("/bin/sh", ["-c", commandLine], workDir, jobName, stdinPath, logPath, logPath, env, false);

    static AgentProcess Start(string exe, IReadOnlyList<string> args, string workDir, string jobName,
        string stdinPath, string stdoutPath, string stderrPath, IReadOnlyDictionary<string, string?>? env, bool outlive)
    {
        var record = Record(jobName);
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        File.SetUnixFileMode(Path.GetDirectoryName(record)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var python = DockSettings.Env("PYTHON") ?? Engine.FindOnPath("python3")
            ?? throw new EngineException("Bundled Python runtime is unavailable");
        var helper = Path.Combine(AppContext.BaseDirectory, "worker_host.py");
        var psi = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir };
        var prefix = JsonSerializer.Deserialize<string[]>(DockSettings.Env("PYTHON_ARGS") ?? "[]") ?? [];
        foreach (var a in prefix.Concat(new[] { helper, outlive ? "worker" : "verification", record, stdinPath, stdoutPath, stderrPath, workDir, exe })) psi.ArgumentList.Add(a);
        if (exe == DockSettings.Env("ENGINE")) foreach (var a in Engine.PrefixArgs) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (key, value) in env ?? new Dictionary<string, string?>())
            if (value == null) psi.Environment.Remove(key); else psi.Environment[key] = value;
        var process = Process.Start(psi) ?? throw new EngineException("Could not start worker host");
        var adapter = new AgentProcess(process, record, outlive);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(record) && !process.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(10);
        if (!File.Exists(record))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            adapter.Dispose();
            throw new EngineException("Worker host failed before its recovery record was written");
        }
        if (BelowNormal) try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception e) when (e is Win32Exception or PlatformNotSupportedException or InvalidOperationException) { }
        return adapter;
    }

    public static AgentProcess? Attach(string jobName, int pid, long createdTicks)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != createdTicks) { process.Dispose(); return null; }
            var path = Record(jobName);
            using var state = JsonDocument.Parse(File.ReadAllText(path));
            if (state.RootElement.GetProperty("pid").GetInt32() != pid) { process.Dispose(); return null; }
            return new AgentProcess(process, path, true);
        }
        catch (Exception e) when (e is ArgumentException or IOException or JsonException or InvalidOperationException or Win32Exception)
        { process?.Dispose(); return null; }
    }

    public static void Reap(string jobName)
    {
        // A write-ahead job without a confirmed PID cannot safely identify a
        // recycled process. The private helper records its own PID before work.
        var path = Record(jobName);
        try
        {
            using var state = JsonDocument.Parse(File.ReadAllText(path));
            var pid = state.RootElement.GetProperty("pid").GetInt32();
            using var process = Process.GetProcessById(pid);
            var started = DateTimeOffset.FromUnixTimeMilliseconds((long)(state.RootElement.GetProperty("started").GetDouble() * 1000));
            if (Math.Abs((process.StartTime.ToUniversalTime() - started.UtcDateTime).TotalSeconds) > 2) return;
            KillGroup(pid, 9);
        }
        catch (Exception e) when (e is ArgumentException or IOException or JsonException or InvalidOperationException or Win32Exception) { }
    }

    public bool HasExited
    {
        get
        {
            if (disposed) return true;
            try { return process.HasExited; } catch (InvalidOperationException) { return true; }
        }
    }

    public int? ExitCode
    {
        get
        {
            if (exitCode != null) return exitCode;
            try
            {
                using var state = JsonDocument.Parse(File.ReadAllText(record));
                if (state.RootElement.GetProperty("exitCode") is { ValueKind: JsonValueKind.Number } code) return exitCode = code.GetInt32();
            }
            catch (Exception e) when (e is IOException or JsonException) { }
            return null;
        }
    }

    public async Task WaitForExitAsync()
    {
        while (!HasExited) await Task.Delay(50);
    }

    internal static List<(int Pid, int Parent, int Group)> ProcessTable()
    {
        var rows = new List<(int, int, int)>();
        using var ps = Process.Start(new ProcessStartInfo("/bin/ps", ["-axo", "pid=,ppid=,pgid="])
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        if (ps == null) return rows;
        var output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit(5000);
        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 3 && int.TryParse(fields[0], out var pid) && int.TryParse(fields[1], out var parent) && int.TryParse(fields[2], out var group)) rows.Add((pid, parent, group));
        }
        return rows;
    }

    public int[] ProcessIds() => disposed ? [] : ProcessTable().Where(p => p.Group == Pid).Select(p => p.Pid).ToArray();
    public TimeSpan CpuTime
    {
        get
        {
            var time = TimeSpan.Zero;
            foreach (var pid in ProcessIds()) try { using var p = Process.GetProcessById(pid); time += p.TotalProcessorTime; } catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
            return time;
        }
    }

    public long WorkingSet()
    {
        long bytes = 0;
        foreach (var pid in ProcessIds()) try { using var p = Process.GetProcessById(pid); bytes += p.WorkingSet64; } catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
        return bytes;
    }

    public async Task StopAsync(TimeSpan grace)
    {
        if (!HasExited)
        {
            var rows = ProcessTable().Where(p => p.Group == Pid && p.Pid != Pid).ToList();
            // Leave the cswap wrapper time to save/seal its profile after Claude
            // exits. A direct child without descendants is itself the command.
            var nested = rows.Where(p => p.Parent != Pid).ToList();
            foreach (var row in nested.Count > 0 ? nested : rows) Kill(row.Pid, 15);
            await Task.WhenAny(WaitForExitAsync(), Task.Delay(grace));
        }
        await ReapAsync();
    }

    public void Reap()
    {
        if (disposed) return;
        // Guard against PID reuse before signalling the stored process group.
        try
        {
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks != CreatedTicks) return;
            KillGroup(Pid, 9);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
    }

    public async Task ReapAsync()
    {
        Reap();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!HasExited && DateTime.UtcNow < until) await Task.Delay(25);
    }

    public void Dispose()
    {
        if (disposed) return;
        if (!outlive && !HasExited) Reap();
        disposed = true;
        process.Dispose();
    }

    public static long UnbiasedMilliseconds => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
    static void Kill(int pid, int signal) { if (pid > 1) kill(pid, signal); }
    static void KillGroup(int group, int signal) { if (group > 1 && group != Environment.ProcessId) kill(-group, signal); }
    [DllImport("libc", SetLastError = true)] static extern int kill(int pid, int signal);
}
