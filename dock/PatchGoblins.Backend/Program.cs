using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace PatchGoblins;

/// The desktop host supplies the same single-thread context as the Windows tray.
internal static class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine("Patch Goblins backend: --serve | status | doctor | agents-terminal --repo PATH | call TOOL JSON --repo PATH | agents-mcp --repo PATH");
            return 0;
        }
        if (args is ["--version"]) { Console.WriteLine("Patch Goblins 0.7.0"); return 0; }
        if (args is ["agents-mcp", ..]) return AgentsLink.ServeMcp(args);
        // A worker's PreToolUse hook, as on Windows: 0 lets the file tool call run, 2 refuses it with the reason on stderr.
        if (args is ["guard", _, ..])
        {
            using var why = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            return Policy.Hook(args[1..], () => new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)).ReadToEnd(), why);
        }
        if (args is ["doctor", ..]) return Doctor.Run(args);
        if (args is ["call", var tool, var json, "--repo", var repo])
        {
            using var doc = JsonDocument.Parse(json);
            var text = AgentsLink.CallAsync(tool, doc.RootElement, Path.GetFullPath(repo)).GetAwaiter().GetResult();
            if (text == null) { Console.Error.WriteLine("Start Patch Goblins from Applications first."); return 2; }
            Console.WriteLine(text);
            return 0;
        }
        if (args is ["status"])
        {
            var text = AgentsLink.CallAsync("desktop_status", JsonSerializer.SerializeToElement(new { }), "").GetAwaiter().GetResult();
            if (text == null) return 2;
            Console.WriteLine(text);
            return 0;
        }
        if (args is ["agents-terminal", "--repo", var folder])
        {
            folder = Path.GetFullPath(folder);
            AgentsTerminal.Open(Engine.Locate(), AgentStore.DefaultRoot, folder, DockSettings.Load().PreserveRemoteControl, "idle");
            return 0;
        }
        if (args is not ["--serve"])
        {
            Console.Error.WriteLine("Usage: patch-goblins-backend --serve | status | doctor | agents-terminal --repo PATH | call TOOL JSON --repo PATH | agents-mcp --repo PATH");
            return 2;
        }
        try
        {
            // FileShare.None locks the same private state directory for all launch paths.
            StoreSecurity.Harden(DockSettings.Dir);
            using var lease = new FileStream(Path.Combine(DockSettings.Dir, "backend.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var context = new HostContext();
            SynchronizationContext.SetSynchronizationContext(context);
            var task = new BackendHost().RunAsync();
            task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.Run();
            task.GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e)
        {
            Log(e);
            Console.Error.WriteLine(Engine.Redact(e.Message));
            return 1;
        }
    }

    internal static void Log(Exception? error) => Log(error?.ToString() ?? "unknown error");
    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DockSettings.Dir);
            var path = Path.Combine(DockSettings.Dir, "patch-goblins.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {Engine.Redact(message)}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class HostContext : SynchronizationContext, IDisposable
{
    readonly BlockingCollection<(SendOrPostCallback, object?)> queue = new();
    public override void Post(SendOrPostCallback callback, object? state)
    {
        try { queue.Add((callback, state)); }
        catch (InvalidOperationException) { }
    }
    public void Run()
    {
        foreach (var (callback, state) in queue.GetConsumingEnumerable()) callback(state);
    }
    public void Complete() => queue.CompleteAdding();
    public void Dispose() => queue.Dispose();
}
