using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PatchGoblins;

/// How the agents terminal reaches the tray. Claude Code starts `patch-goblins.exe agents-mcp --repo <path>`
/// as a stdio MCP server; it relays every tool call over a named pipe that only the current user can
/// open to the running tray, which answers from the supervisor. It also holds one connection open for
/// as long as it runs: that is how the tray knows the terminal is open. The bridge holds no state.
public static class AgentsLink
{
    static string PipeName => PipeNameFor(DockSettings.Dir);

    internal static string PipeNameFor(string dataDir) => "patch-goblins-agents-" + AgentStore.KeyFor(dataDir);

    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// Tray side: answers one request per connection, on the tray's thread, and reports each terminal's
    /// held connection to `presence` (repository, open). Idle, it is pending asynchronous I/O: no thread
    /// and no CPU.
    public sealed class Server : IDisposable
    {
        readonly Func<string, JsonElement, string, Task<string>> _handle;
        readonly Action<string, bool>? _presence;
        readonly CancellationTokenSource _cts = new();
        readonly string _name;

        public Server(Func<string, JsonElement, string, Task<string>> handle, string? pipeName = null, Action<string, bool>? presence = null)
        {
            _handle = handle;
            _presence = presence;
            _name = pipeName ?? PipeName;
            _ = AcceptAsync();
        }

        async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(_cts.Token); }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException)
                {
                    await pipe.DisposeAsync();
                    if (e is IOException) Program.Log(e);
                    return;
                }
                _ = ServeAsync(pipe);
            }
        }

        async Task ServeAsync(NamedPipeServerStream pipe)
        {
            await using (pipe)
            {
                try
                {
                    var reader = new StreamReader(pipe, Utf8);
                    var writer = new StreamWriter(pipe, Utf8) { AutoFlush = true, NewLine = "\n" };
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    if (await reader.ReadLineAsync(timeout.Token) is not { } line) return;
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var (tool, repo) = (root.GetProperty("tool").GetString() ?? "", root.GetProperty("repo").GetString() ?? "");
                    if (tool == Attach)
                    {
                        // Held until the terminal's bridge exits (or the tray does): the terminal is open meanwhile.
                        _presence?.Invoke(repo, true);
                        try { while (await reader.ReadLineAsync(_cts.Token) != null) { } }
                        finally { _presence?.Invoke(repo, false); }
                        return;
                    }
                    var text = await _handle(tool, root.GetProperty("args").Clone(), repo);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { text }));
                }
                catch (Exception e) when (e is IOException or JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException)
                {
                    Program.Log(e);
                }
            }
        }

        public void Dispose() => _cts.Cancel();
    }

    /// Not an MCP tool: the bridge's own presence connection.
    const string Attach = "attach";

    /// Bridge side: one connection held for as long as the terminal is open, made again when the tray
    /// restarts. Without a tray it tries every few seconds.
    internal static async Task HoldAsync(string repo, string? pipeName = null, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(500, ct);
                var writer = new StreamWriter(pipe, Utf8) { AutoFlush = true, NewLine = "\n" };
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { tool = Attach, args = new { }, repo }));
                await new StreamReader(pipe, Utf8).ReadLineAsync(ct); // returns when the tray goes away
            }
            catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { }
        }
    }

    /// Client side of one tool call; null when the tray is not running.
    public static async Task<string?> CallAsync(string tool, JsonElement args, string repo, string? pipeName = null)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(3000); }
        catch (Exception e) when (e is TimeoutException or IOException) { return null; }
        var writer = new StreamWriter(pipe, Utf8) { AutoFlush = true, NewLine = "\n" };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { tool, args, repo }));
        // No timeout: starting waits for the owner's answer in a dialog.
        var line = await new StreamReader(pipe, Utf8).ReadLineAsync();
        return line == null ? null : JsonDocument.Parse(line).RootElement.GetProperty("text").GetString();
    }

    // -- the MCP server the agents terminal starts ------------------------------

    internal const string Instructions =
        """
        This terminal is the owner's line to Patch Goblins agents. Once the owner starts them for this repository, Patch Goblins works through its actionable open GitHub issues while this terminal is open (closing it pauses them; reopening it continues), without supervision: each item gets its own headless Claude Code session in its own git worktree, on one of the owner's other Claude accounts. Patch Goblins verifies the work, pushes a branch, opens a pull request, follows up on failing checks, conflicts and review comments, and moves on. It never merges and pauses only for real decisions, missing permissions or exhausted accounts.

        Talk to the owner naturally and keep the machinery out of view: report outcomes (what is being worked on, which pull requests are open, what needs their decision) rather than workers, accounts, attempts, sessions or worktrees, unless they ask for details (then use detail=true or agents_details).
        - Use agents_status to answer any question about progress.
        - Use agents_control only when the owner asks to start, pause, resume or stop. Starting shows the owner a native Patch Goblins confirmation dialog the first time; tell them to look for it. When starting, pass the command that must pass before a pull request opens as `verify` if the repository has a clear one (from its CI config, README or CLAUDE.md); ask the owner if unsure.
        - Use agents_queue when the owner reorders, skips or restores issues, adds a request ("add": title and text), or answers a question an item is waiting on ("answer": item and text). An item held before its push (review concerns, or a change to .github/) is pushed as is by "answer" with no text, only when the owner says so; with text it goes back for that change.
        - Use agents_memory to list, add or forget shared notes when the owner asks you to remember or forget something: their notes bind every agent, here or (with global) in every repository.
        - Use agents_goal when the owner states an objective bigger than one change (set), and to list goals, answer a goal's question, ask for a new plan, drop one, or confirm one done. Goals come first when you report progress. Checks the owner gives (`checks`) are confirmed in a native Patch Goblins dialog; tell them to look for it. The Director may plan steps in the owner's other repositories that agents work on; those run there while that repository's agents terminal is open.
        - Use agents_doctor when the owner asks whether this repository is ready for agents, before the first start, or when work keeps failing: it reports READY, LIMITED or BLOCKED with evidence and fixes, read-only. bootstrap adds a preview of missing instructions, policy and issue template; write them only when the owner asks.
        - Items are named #<issue> for issues and w<n> for the owner's own requests; goals g<n>.
        You can still do ordinary work in this repository yourself when the owner asks for it directly.
        """;

    static readonly string[] Versions = ["2024-11-05", "2025-03-26", "2025-06-18"];

    static JsonArray Tools() =>
    [
        Tool("agents_status", "What Patch Goblins agents are doing in this repository: work in progress, open pull requests, questions waiting for the owner, what is next. Set detail for workers, accounts and attempts.",
            new JsonObject { ["detail"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include the machinery (only when the owner asks)." } }),
        Tool("agents_control", "Start, pause, resume or stop continuous agent work on this repository. Only when the owner asks.",
            new JsonObject
            {
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("start", "pause", "resume", "stop") },
                ["verify"] = new JsonObject { ["type"] = "string", ["description"] = "Command that must pass in a worktree before a pull request opens, e.g. `dotnet test` (start/resume only). " + CheckShell.Describe(CheckShell.Runner) },
                ["base"] = new JsonObject { ["type"] = "string", ["description"] = "Base branch; defaults to the remote's default branch." },
            }, "action"),
        Tool("agents_queue", "Change what agents work on: prioritize issues (in order), skip or restore an issue, add an owner request, answer an item's question, or retry an item.",
            new JsonObject
            {
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("prioritize", "skip", "restore", "add", "answer", "retry") },
                ["issues"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = "prioritize: issue numbers, most important first." },
                ["item"] = new JsonObject { ["type"] = "string", ["description"] = "skip/restore/answer/retry: #12 or w3." },
                ["title"] = new JsonObject { ["type"] = "string", ["description"] = "add: short title." },
                ["text"] = new JsonObject { ["type"] = "string", ["description"] = "add: the request; answer: the owner's answer or guidance (omit to approve a held push)." },
            }, "action"),
        Tool("agents_details", "Recent activity and run facts for one item (only when the owner asks for details).",
            new JsonObject { ["item"] = new JsonObject { ["type"] = "string", ["description"] = "#12 or w3" } }, "item"),
        Tool("agents_memory", "The agents' shared memory: notes that go into every agent's prompt. List them, add one (the owner's decision, binding), or forget one by its number or text.",
            new JsonObject
            {
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("list", "add", "forget") },
                ["text"] = new JsonObject { ["type"] = "string", ["description"] = "add: the note; forget: its number (g1 for shared) or text it contains." },
                ["global"] = new JsonObject { ["type"] = "boolean", ["description"] = "Note or forget for every repository instead of this one." },
            }, "action"),
        Tool("agents_goal", "The owner's goals: objectives bigger than one change. Patch Goblins' Director plans each into steps that agents turn into pull requests, plans again as they land, and calls a goal achieved only on evidence: the owner's checks passing on the base branch, or merged work. Set one when the owner states such an objective, list them, answer a goal's question, ask for a new plan, drop one, or confirm one done.",
            new JsonObject
            {
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("set", "list", "answer", "replan", "drop", "done") },
                ["goal"] = new JsonObject { ["type"] = "string", ["description"] = "answer/replan/drop/done: g1." },
                ["title"] = new JsonObject { ["type"] = "string", ["description"] = "set: short title." },
                ["text"] = new JsonObject { ["type"] = "string", ["description"] = "set: the objective and what done looks like; answer: the owner's answer or guidance." },
                ["checks"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "set/answer/replan: commands that must pass on the base branch for the goal to count as achieved; they replace the goal's checks, and the owner confirms them in a Patch Goblins dialog. " + CheckShell.Describe(CheckShell.Runner) + " Patch Goblins refuses a check this shell cannot run." },
                ["issue"] = new JsonObject { ["type"] = "integer", ["description"] = "set: an issue of this repository to adopt as the goal (its title and body)." },
            }, "action"),
        Tool("agents_doctor", "Read-only readiness check of this repository for agents: base branch and pins, unpushed or overlapping work, stale instructions, verification gaps, risky workflows, committed secrets, production side effects, issue dependencies and what agents achieved here. READY, LIMITED or BLOCKED, with evidence and fixes. Changes nothing.",
            new JsonObject
            {
                ["base"] = new JsonObject { ["type"] = "string", ["description"] = "Base branch to assess; defaults to the agents' base, the policy's, or the remote default." },
                ["bootstrap"] = new JsonObject { ["type"] = "boolean", ["description"] = "Add a preview of the policy, instructions and issue template that are missing (nothing is written)." },
            }),
    ];

    static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
        },
    };

    /// `patch-goblins.exe agents-mcp --repo <path>`: newline-delimited JSON-RPC on stdin/stdout until EOF.
    public static int ServeMcp(string[] args)
    {
        var at = Array.IndexOf(args, "--repo");
        var repo = at >= 0 && at + 1 < args.Length ? args[at + 1] : Environment.CurrentDirectory;
        using var input = new StreamReader(Console.OpenStandardInput(), Utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true, NewLine = "\n" };
        _ = Task.Run(() => HoldAsync(repo)); // ends with this process, when Claude Code ends the terminal's session
        while (input.ReadLine() is { } line)
            if (Respond(line, repo) is { } reply) output.WriteLine(reply);
        return 0;
    }

    /// The reply to one JSON-RPC message, or null for notifications and noise.
    internal static string? Respond(string line, string repo)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(line); }
        catch (JsonException) { return null; }
        if (msg is not JsonObject o || o["id"] is not { } id) return null;
        var method = o["method"]?.GetValue<string>();
        JsonNode? result = null;
        JsonObject? error = null;
        switch (method)
        {
            case "initialize":
                var asked = o["params"]?["protocolVersion"]?.GetValue<string>();
                result = new JsonObject
                {
                    ["protocolVersion"] = Versions.Contains(asked) ? asked : Versions[^1],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "patch-goblins", ["version"] = typeof(AgentsLink).Assembly.GetName().Version?.ToString(3) ?? "0" },
                    ["instructions"] = Instructions,
                };
                break;
            case "ping":
                result = new JsonObject();
                break;
            case "tools/list":
                result = new JsonObject { ["tools"] = Tools() };
                break;
            case "tools/call":
                var name = o["params"]?["name"]?.GetValue<string>() ?? "";
                var arguments = JsonSerializer.SerializeToElement(o["params"]?["arguments"] ?? new JsonObject());
                string? text;
                try { text = CallAsync(name, arguments, repo).GetAwaiter().GetResult(); }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { text = null; }
                result = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = text ?? "Patch Goblins agents cannot be reached: Patch Goblins is not running, or was restarted since this terminal opened. Ask the owner to start Patch Goblins, or to reopen this terminal from its Agents button.",
                    }),
                    ["isError"] = text == null,
                };
                break;
            default:
                error = new JsonObject { ["code"] = -32601, ["message"] = $"method not found: {method}" };
                break;
        }
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        if (error != null) reply["error"] = error;
        else reply["result"] = result;
        return reply.ToJsonString();
    }
}
