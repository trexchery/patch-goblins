using System.Diagnostics;
using PatchGoblins;
using Xunit;
using Xunit.Abstractions;

namespace PatchGoblins.Tests;

/// Opt-in, against the real engine and Claude Code (it uses a little of a real account's quota):
///   PATCH_GOBLINS_LIVE_ENGINE=<path to cswap.exe>  PATCH_GOBLINS_LIVE_ACCOUNT=<number, not the signed-in one>
/// Skipped (passes trivially) otherwise, like the engine integration test.
public class AgentsLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ARealRunIsIsolatedResumableAndSealedAfterwards()
    {
        var engine = Environment.GetEnvironmentVariable("PATCH_GOBLINS_LIVE_ENGINE");
        var account = Environment.GetEnvironmentVariable("PATCH_GOBLINS_LIVE_ACCOUNT");
        if (engine is not { Length: > 0 } || account is not { Length: > 0 }) return;
        using var tmp = new TempDir();
        string F(string n) => Path.Combine(tmp.Path, n);
        var work = F("work");
        Directory.CreateDirectory(work);
        Process.Start(new ProcessStartInfo("git", "init --quiet") { WorkingDirectory = work, UseShellExecute = false })!.WaitForExit();

        var session = Guid.NewGuid().ToString();
        var first = await RunAsync(engine, account, session, resume: false, work, F, 1,
            "Reply with exactly this one line and nothing else:\nHANDOFF: DONE smoke test");
        Assert.Contains(first, e => e is InitEvent i && i.SessionId == session);
        Assert.Equal("smoke test", Supervisor.Marker(first.OfType<ResultEvent>().Last().Text, "DONE"));

        var second = await RunAsync(engine, account, session, resume: true, work, F, 2,
            "What did you reply last time? Answer with exactly one line: HANDOFF: DONE <your previous line's last two words>");
        Assert.Equal("smoke test", Supervisor.Marker(second.OfType<ResultEvent>().Last().Text, "DONE"));

        // The wrapper sealed the account's profile again: no plaintext login left behind.
        var sessions = Path.Combine(StoreSecurity.BackupRoot, "sessions");
        foreach (var profile in Directory.GetDirectories(sessions, account + "-*"))
        {
            output.WriteLine($"profile {Path.GetFileName(profile)}: records {Directory.GetFiles(Path.Combine(profile, "sessions"), "*.json").Length}");
            Assert.False(File.Exists(Path.Combine(profile, ".credentials.json")), "profile left unsealed");
        }
    }

    /// Routed runs end to end through `cswap run N --require-session`, as the supervisor starts them: the model, effort and
    /// ultracode it asks for are what runs (fresh and resumed), subagents and workflow agents stay on the run's model, only
    /// Haiku, Sonnet and Opus answer, completion is judged from the stream, and nothing (process, marker, lock, plaintext
    /// login) outlives the run. PATCH_GOBLINS_LIVE_ULTRACODE=1 adds the ultracode canary (one workflow agent).
    /// PATCH_GOBLINS_LIVE_OUT keeps the runs' files there.
    [Fact]
    public async Task RoutedRunsApplyTheirSettingsThroughTheWrapper()
    {
        var engine = Environment.GetEnvironmentVariable("PATCH_GOBLINS_LIVE_ENGINE");
        var account = Environment.GetEnvironmentVariable("PATCH_GOBLINS_LIVE_ACCOUNT");
        if (engine is not { Length: > 0 } || account is not { Length: > 0 }) return;
        using var tmp = new TempDir();
        var root = Environment.GetEnvironmentVariable("PATCH_GOBLINS_LIVE_OUT") is { Length: > 0 } keep ? Directory.CreateDirectory(Path.Combine(keep, DateTime.Now.ToString("HHmmss"))).FullName : tmp.Path;
        string F(string n) => Path.Combine(root, n);
        var work = F("work");
        Directory.CreateDirectory(work);
        Process.Start(new ProcessStartInfo("git", "init --quiet") { WorkingDirectory = work, UseShellExecute = false })!.WaitForExit();
        var profile = Directory.GetDirectories(Path.Combine(StoreSecurity.BackupRoot, "sessions"), account + "-*").Single();

        var session = Guid.NewGuid().ToString();
        var off = await RoutedAsync(engine, account, session, false, new RunSettings("haiku", "low", false), work, F, "a",
            "This is a harmless connectivity check. Call the Agent tool exactly once, with model \"opus\" and the prompt " +
            "\"Reply with the single word pong and nothing else.\" Use no other tool. Then reply with exactly one line:\nHANDOFF: DONE <the subagent's reply>");
        Assert.DoesNotContain("Workflow", off.Tools);
        Assert.Contains(off.Subagents, m => m.Contains("haiku")); // the subagent asked for Opus and ran on the run's model
        Assert.DoesNotContain(off.Subagents, m => !m.Contains("haiku"));
        // Isolation: the conversation lives in the account's profile, not the default login's.
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(profile, "projects"), session + ".jsonl", SearchOption.AllDirectories));

        var resumed = await RoutedAsync(engine, account, session, true, new RunSettings("sonnet", "medium", false), work, F, "b",
            "Reply with exactly the HANDOFF line you replied last time, and nothing else.");
        Assert.Contains("sonnet", resumed.Model);
        Assert.Equal(off.Done.Split('\n')[0], resumed.Done.Split('\n')[0]);

        if (Environment.GetEnvironmentVariable("PATCH_GOBLINS_LIVE_ULTRACODE") == "1")
        {
            var on = await RoutedAsync(engine, account, Guid.NewGuid().ToString(), false, new RunSettings("sonnet", "low", true), work, F, "c",
                "This is a harmless connectivity check of ultracode. Use the Workflow tool once, for a workflow of exactly one agent " +
                "whose options ask for model \"opus\" and whose prompt is \"Reply with the single word pong and nothing else.\"; it returns " +
                "that reply. Use no other tool besides what waiting for it needs. Then reply with exactly one line:\nHANDOFF: DONE <the workflow's result>");
            Assert.Contains("Workflow", on.Tools);
            Assert.Contains("sonnet", on.Model);
            Assert.DoesNotContain(on.Subagents, m => !m.Contains("sonnet"));
        }

        // Nothing outlives the runs: the wrapper's marker, a plaintext login, the engine's store lock.
        Assert.Empty(Directory.Exists(Path.Combine(profile, ".cswap-runs")) ? Directory.GetFiles(Path.Combine(profile, ".cswap-runs")) : []);
        Assert.False(File.Exists(Path.Combine(profile, ".credentials.json")), "profile left unsealed");
        using (var lockFile = new FileStream(Path.Combine(StoreSecurity.BackupRoot, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            lockFile.Lock(0, 1); // throws when a wrapper still holds the store lock
            lockFile.Unlock(0, 1);
        }
    }

    sealed record Routed(string Model, string[] Tools, string[] Subagents, string Done);

    async Task<Routed> RoutedAsync(string engine, string account, string session, bool resume, RunSettings settings, string work, Func<string, string> f, string n, string prompt)
    {
        File.WriteAllText(f($"prompt-{n}.md"), prompt);
        var (outPath, errPath) = (f($"out-{n}.jsonl"), f($"err-{n}.log"));
        var started = Stopwatch.StartNew();
        using var p = AgentProcess.Start(engine, Supervisor.WorkerArgs(int.Parse(account), session, resume, review: false, work, settings), work,
            $@"Local\patch-goblins-live-{Guid.NewGuid():N}", f($"prompt-{n}.md"), outPath, errPath, Supervisor.WorkerEnvironment(f("no-gh"), settings));
        // Completion as the supervisor judges it: the stream's final answer, nothing pending, quiet for FinishGrace.
        var config = new AgentsConfig();
        var (tail, activity) = (new StreamTail(outPath), new RunActivity());
        TimeSpan? answered = null;
        string? verdict = null;
        var exit = p.WaitForExitAsync();
        var (lastLen, lastCpu, lastProgress) = (0L, TimeSpan.Zero, started.Elapsed);
        while (!exit.IsCompleted && started.Elapsed < TimeSpan.FromMinutes(8) && verdict == null)
        {
            await Task.WhenAny(exit, Task.Delay(1000));
            activity.Add(tail.Read(), DateTimeOffset.Now);
            var (len, cpu) = (new FileInfo(outPath).Length, p.CpuTime);
            if (len != lastLen || cpu - lastCpu > TimeSpan.FromSeconds(2)) (lastLen, lastCpu, lastProgress) = (len, cpu, started.Elapsed);
            if (activity.Answered != null) answered ??= started.Elapsed;
            verdict = Supervisor.Verdict(activity, settings.Ultracode, started.Elapsed - lastProgress, DateTimeOffset.Now, config);
        }
        var leftovers = p.HasExited ? p.ProcessIds() : [];
        output.WriteLine($"run {n} ({settings}): exit {(p.HasExited ? p.ExitCode : null)} at {started.Elapsed.TotalSeconds:0.0}s, answered at {answered?.TotalSeconds:0.0}s, verdict {verdict ?? "none"}, left in job: [{string.Join(",", leftovers)}]");
        if (verdict == "finished") await p.StopAsync(config.StopGrace); // the supervisor closes it the same way
        else await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
        await p.ReapAsync();
        Assert.Empty(p.ProcessIds());
        Assert.NotEqual("stalled", verdict);

        var events = StreamTail.ReadAll(outPath);
        var result = Supervisor.FinalResult(events);
        output.WriteLine($"  result: {result?.Text} · turns {result?.Turns} · {result?.DurationMs} ms · tokens {result?.Tokens}");
        output.WriteLine("  stderr: " + Supervisor.TailOf(errPath, 500));
        Assert.NotNull(result);
        Assert.False(result!.IsError, result.Text);
        var done = Supervisor.Marker(result.Text, "DONE");
        Assert.NotNull(done);
        // What metrics.jsonl records: the model Claude reports, its time and tokens.
        var init = events.OfType<InitEvent>().Last();
        Assert.Equal(session, init.SessionId);
        Assert.NotNull(result.Tokens);
        Assert.True(events.OfType<ResultEvent>().Sum(r => r.DurationMs ?? 0) > 0);

        // Every model the stream names: the run's, each subagent's and workflow agent's, and the result's per-model usage.
        var (tools, subagents, models) = (new List<string>(), new List<string>(), new HashSet<string>());
        foreach (var line in File.ReadLines(outPath).Where(l => l.StartsWith('{') && !l.StartsWith("{\"type\":\"stream_event\"")))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var r = doc.RootElement;
            var type = r.GetProperty("type").GetString();
            if (type == "system" && r.TryGetProperty("subtype", out var st) && st.GetString() == "init")
            {
                tools.AddRange(r.GetProperty("tools").EnumerateArray().Select(t => t.GetString()!));
                output.WriteLine("  init: " + string.Join(", ", r.EnumerateObject().Where(o => o.Name is not ("tools" or "mcp_servers" or "slash_commands" or "skills" or "plugins" or "agents")).Select(o => $"{o.Name}={o.Value}")));
            }
            if (type == "assistant" && r.GetProperty("message").TryGetProperty("model", out var m) && m.GetString() is not (null or "<synthetic>"))
            {
                models.Add(m.GetString()!);
                if (r.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == System.Text.Json.JsonValueKind.String) subagents.Add(m.GetString()!);
            }
            if (type == "result" && r.TryGetProperty("modelUsage", out var mu)) foreach (var k in mu.EnumerateObject()) models.Add(k.Name);
        }
        output.WriteLine($"  models: {string.Join(", ", models)} · subagents: {string.Join(", ", subagents)} · Workflow tool: {tools.Contains("Workflow")}");
        Assert.Contains(settings.Model, init.Model);
        Assert.All(models, m => Assert.Matches("haiku|sonnet|opus", m));
        // The effort each request went out with, from the profile's transcripts (the run's own and its subagents').
        var efforts = Directory.GetFiles(Path.Combine(StoreSecurity.BackupRoot, "sessions"), "*.jsonl", SearchOption.AllDirectories)
            .Where(t => t.Contains(session) && File.GetLastWriteTimeUtc(t) > DateTime.UtcNow - started.Elapsed)
            .SelectMany(File.ReadLines).Where(l => l.Contains("\"type\":\"assistant\"") && l.Contains($"\"model\":\"{init.Model}\""))
            .SelectMany(l => System.Text.RegularExpressions.Regex.Matches(l, "\"effort\":\"(\\w+)\"").Select(m => m.Groups[1].Value)).Distinct().ToList();
        output.WriteLine($"  effort on {init.Model}: {string.Join(", ", efforts)}");
        Assert.Equal([settings.Effort], efforts);
        return new(init.Model!, [.. tools], [.. subagents], done!);
    }

    async Task<List<StreamEvent>> RunAsync(string engine, string account, string session, bool resume, string work, Func<string, string> f, int n, string prompt)
    {
        File.WriteAllText(f($"prompt-{n}.md"), prompt);
        var started = Stopwatch.StartNew();
        using var p = AgentProcess.Start(engine, Supervisor.WorkerArgs(int.Parse(account), session, resume, review: false, work), work,
            $@"Local\patch-goblins-live-{Guid.NewGuid():N}", f($"prompt-{n}.md"), f($"out-{n}.jsonl"), f($"err-{n}.log"), Supervisor.WorkerEnvironment(f("no-gh")));
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4));
        var events = StreamTail.ReadAll(f($"out-{n}.jsonl"));
        var result = events.OfType<ResultEvent>().LastOrDefault();
        output.WriteLine($"run {n}: exit {p.ExitCode} in {started.Elapsed.TotalSeconds:0.0}s, {events.Count} events, cost {result?.CostUsd}, result: {result?.Text}");
        output.WriteLine("stderr: " + Supervisor.TailOf(f($"err-{n}.log"), 500));
        Assert.NotNull(result);
        Assert.False(result!.IsError, result.Text);
        Assert.Equal(0, p.ExitCode);
        return events;
    }
}
