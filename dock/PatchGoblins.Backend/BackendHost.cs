using System.Globalization;
using System.Text.Json;

namespace PatchGoblins;

/// JSON lines are private stdio between the native menu and the reused Supervisor.
/// Approvals use separate request ids so the input reader never blocks awaiting a decision.
internal sealed class BackendHost
{
    readonly Engine engine = Engine.Locate();
    readonly DockSettings settings = DockSettings.Load();
    readonly Dictionary<string, TaskCompletionSource<bool>> approvals = [];
    readonly CancellationTokenSource stopping = new();
    readonly List<Task> requests = [];
    Supervisor supervisor = null!;
    bool refreshing;
    Snapshot? accounts;

    public async Task RunAsync()
    {
        var cli = Cli.Locate() ?? throw new EngineException("Git is required. Install Git and the GitHub CLI, then restart Patch Goblins.");
        supervisor = new Supervisor(engine, cli, new AgentStore(AgentStore.DefaultRoot), new AgentsConfig());
        ApplySettings();
        supervisor.Authorize = (repo, verify, branch) => AskAsync($"Start agents on {repo.Leaf}?",
            $"Repository: {repo.Path}\nBase: {branch}\nVerification: {verify ?? "No verification command; relies on CI."}\n\n" +
            "Agents use spare Claude accounts in separate worktrees, verify and review each change, then push a branch and open a pull request. " +
            "They never merge, deploy or close issues. Work pauses when its agents terminal closes.");
        supervisor.ConfirmChecks = (repo, checks) => AskAsync($"Run goal checks in {repo.Leaf}?",
            $"These commands run as you in a fresh checkout of {repo.Base}:\n\n" + string.Join("\n", checks));
        supervisor.Notify = (title, body) => Write(new { @event = "notify", title, body });
        supervisor.Changed += () =>
        {
            foreach (var repo in supervisor.Repos)
                AgentsTerminal.SetState(AgentStore.DefaultRoot, repo.Path, AgentsTerminal.StatusOf(repo, AgentsTerminal.WebOf(repo.Path)));
            Write(new { @event = "changed" });
        };
        supervisor.RefreshWanted += () => _ = RefreshAsync();
        using var link = new AgentsLink.Server(HandleToolAsync, presence: (repo, open) =>
        {
            if (open) supervisor.Attach(repo); else supervisor.Detach(repo);
        });
        supervisor.Start();
        Write(new { @event = "ready", schemaVersion = 1 });
        var poll = PollAsync();
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync(stopping.Token);
                if (line == null) break;
                // A call may await a native approval; continue reading its answer.
                requests.RemoveAll(t => t.IsCompleted);
                requests.Add(DispatchAsync(line));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            stopping.Cancel();
            foreach (var answer in approvals.Values) answer.TrySetResult(false);
            await supervisor.StopAllAsync();
            supervisor.Dispose();
            try { await poll; } catch (OperationCanceledException) { }
        }
    }

    async Task DispatchAsync(string line)
    {
        JsonElement? id = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var value)) id = value.Clone();
            var method = root.GetProperty("method").GetString();
            var args = root.TryGetProperty("params", out var p) ? p : JsonSerializer.SerializeToElement(new { });
            object? result = method switch
            {
                "status" => Status(),
                "call" => await supervisor.HandleAsync(args.GetProperty("tool").GetString()!,
                    args.TryGetProperty("args", out var a) ? a : JsonSerializer.SerializeToElement(new { }),
                    Path.GetFullPath(args.GetProperty("repo").GetString()!)),
                "approval" => Answer(args),
                "refresh" => await RefreshAsync(),
                "settings" => SaveSettings(args),
                "attach" => Attach(args, true),
                "detach" => Attach(args, false),
                "quit" => Quit(),
                _ => throw new EngineException("Unknown backend method: " + method),
            };
            Write(new { id, result });
        }
        catch (Exception e)
        {
            Program.Log(e);
            Write(new { id, error = new { message = Engine.Redact(e.Message) } });
        }
    }

    async Task<string> HandleToolAsync(string tool, JsonElement args, string repo) => tool switch
    {
        "desktop_status" => JsonSerializer.Serialize(Status(), Json.Options),
        "desktop_settings" => JsonSerializer.Serialize(SaveSettings(args), Json.Options),
        "desktop_refresh" => JsonSerializer.Serialize(await RefreshAsync(), Json.Options),
        _ => await supervisor.HandleAsync(tool, args, repo),
    };

    object Status() => new { schemaVersion = 1, repos = supervisor.Repos, config = settings,
        working = supervisor.Working, waiting = supervisor.Waiting, accounts };

    object Attach(JsonElement args, bool open)
    {
        var repo = Path.GetFullPath(args.GetProperty("repo").GetString()!);
        if (open) supervisor.Attach(repo); else supervisor.Detach(repo);
        return new { attached = open };
    }

    object Answer(JsonElement args)
    {
        var id = args.GetProperty("id").ToString();
        var accepted = args.GetProperty("approved").ValueKind == JsonValueKind.True;
        return new { answered = approvals.TryGetValue(id, out var pending) && pending.TrySetResult(accepted) };
    }

    async Task<bool> AskAsync(string title, string body)
    {
        // Only a response to a pending native prompt can authorize a start or new checks.
        var id = Guid.NewGuid().ToString("N");
        var pending = new TaskCompletionSource<bool>();
        approvals.Add(id, pending);
        Write(new { @event = "approval", id, title, body });
        try { return await pending.Task.WaitAsync(TimeSpan.FromMinutes(10), stopping.Token); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { return false; }
        finally { approvals.Remove(id); }
    }

    object SaveSettings(JsonElement args)
    {
        var values = args.TryGetProperty("values", out var v) ? v : args;
        foreach (var property in values.EnumerateObject())
        {
            switch (property.Name)
            {
                case "agentIssues": settings.AgentIssues = Math.Clamp(property.Value.GetInt32(), 0, 32); break;
                case "agentOpenPrs": settings.AgentOpenPrs = Math.Clamp(property.Value.GetInt32(), 1, 100); break;
                case "agentDraftPrs": settings.AgentDraftPrs = property.Value.GetBoolean(); break;
                case "agentUltracode": settings.AgentUltracode = property.Value.GetBoolean(); break;
                case "askApproval": settings.AskApproval = property.Value.GetBoolean(); break;
                case "preserveRemoteControl": settings.PreserveRemoteControl = property.Value.GetBoolean(); break;
                case "quotaNotifications": settings.QuotaNotifications = property.Value.GetBoolean(); break;
                case "refreshSeconds": settings.RefreshSeconds = Math.Clamp(property.Value.GetInt32(), 60, 3600); break;
                default: throw new EngineException("Unsupported desktop setting: " + property.Name);
            }
        }
        settings.Save();
        ApplySettings();
        supervisor.Poke();
        return settings;
    }

    void ApplySettings()
    {
        supervisor.Config.MaxConcurrent = settings.AgentIssues;
        supervisor.Config.MaxOpenPrs = settings.AgentOpenPrs;
        supervisor.Config.DraftPrs = settings.AgentDraftPrs;
        supervisor.Config.Ultracode = settings.AgentUltracode;
        supervisor.Config.AskApproval = settings.AskApproval;
    }

    async Task<bool> RefreshAsync()
    {
        if (refreshing) return false;
        refreshing = true;
        try
        {
            var config = await engine.ConfigAsync();
            if (double.TryParse(config.GetValueOrDefault("autoswitch.threshold"), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
                supervisor.Config.Threshold = threshold;
            supervisor.Config.ConsumeFirst = config.GetValueOrDefault("autoswitch.strategy") == "consume-first";
            accounts = await engine.ListAsync();
            var dirs = ResumeStopped.ConfigDirs(accounts.ActiveAccountNumber);
            var live = await Task.Run(() => ResumeStopped.LiveProfiles(dirs));
            // Unreadable session state fails closed: never contend with the owner's solo account.
            var ownBusy = true;
            try
            {
                var windows = await Task.Run(() => ResumeStopped.Scan([(ResumeStopped.DefaultDir, null)], DateTime.UtcNow, includeBusy: true));
                ownBusy = ResumeStopped.LiveCounts(ResumeStopped.DefaultDir).Records > windows.Count || windows.Any(w => w.Status != "agents" && !w.Idle);
            }
            catch (Exception e) { Program.Log(e); }
            supervisor.UpdateAccounts(accounts.Accounts, ProjectMap.Load(), live, ownBusy);
            return true;
        }
        catch (Exception e)
        {
            Program.Log(e);
            Write(new { @event = "notify", title = "Account refresh failed", body = Engine.Redact(e.Message) });
            return false;
        }
        finally { refreshing = false; }
    }

    async Task PollAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            await RefreshAsync();
            await Task.Delay(TimeSpan.FromSeconds(settings.RefreshSeconds), stopping.Token);
        }
    }

    object Quit() { stopping.Cancel(); return new { quitting = true }; }
    static void Write(object value) => Console.WriteLine(Engine.Redact(JsonSerializer.Serialize(value, Json.Options)));
}
