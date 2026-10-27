using System.Text.Json;

namespace PatchGoblins;

internal sealed partial class BackendHost
{
    static string Provider(JsonElement args)
    {
        var provider = args.GetProperty("provider").GetString() ?? "";
        if (!ProviderCli.All.Contains(provider)) throw new EngineException("Choose codex or grok.");
        return provider;
    }

    async Task<SwitchResult> ProviderSwitchAsync(JsonElement args)
    {
        var provider = Provider(args);
        var result = await engine.SwitchAsync(args.GetProperty("number").GetInt32(), provider);
        providers[provider] = await engine.ListAsync(provider: provider);
        return result;
    }

    async Task<string> ProviderCommandAsync(JsonElement args)
    {
        var provider = Provider(args);
        var command = args.GetProperty("args").EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
        if (command is not (["list"] or ["add-current"] or ["remove", _]))
            throw new EngineException("The menu can list, add the current login or remove a provider account.");
        if (command is ["remove", var number] && (!int.TryParse(number, out var n) || n < 1))
            throw new EngineException("An account number is required.");
        var result = await engine.CommandAsync([provider, .. command, "--json"]);
        providers[provider] = await engine.ListAsync(provider: provider);
        return result;
    }

    object NewAgent()
    {
        Directory.CreateDirectory(Launch.GeneralFolder);
        engine.LaunchConsole("Claude · new agent", Launch.GeneralFolder, Launch.Run(null, settings.PreserveRemoteControl));
        return new { opened = true, folder = Launch.GeneralFolder };
    }

    async Task<object> HandoffAsync(JsonElement args)
    {
        var source = args.GetProperty("source").GetString() ?? "";
        var target = args.GetProperty("target").GetString() ?? "";
        if (source is not ("claude" or "codex") || !ProviderCli.Every.Contains(target) || source == target)
            throw new EngineException("Choose a Claude or Codex source and a different installed target.");
        var folder = Path.GetFullPath(args.GetProperty("folder").GetString()!);
        var executable = ProviderCli.FindAny(target) ?? throw new EngineException($"{ProviderCli.Title(target)} CLI not found.");
        var handoff = await engine.HandoffAsync(source, folder);
        var prompt = $"Read the hand-off notes in {handoff.Path} and continue the interrupted task.";
        var mapped = target == "claude" && settings.AutoProjectAccount
            ? ProjectMap.AccountFor(folder, accounts?.Accounts ?? [], ProjectMap.Load()) : null;
        var who = mapped ?? (target == "claude" ? accounts : providers.GetValueOrDefault(target))?.Accounts.FirstOrDefault(a => a.Active);
        var title = $"{ProviderCli.Title(target)} · continue" + (who == null ? "" : " · " + who.ShortName);
        if (target == "grok") Engine.LaunchConsoleWith(executable, title, folder);
        else if (target == "claude" && Launch.Continue(mapped, who, settings.PreserveRemoteControl, prompt) is { } run)
            engine.LaunchConsole(title, folder, run);
        else Engine.LaunchConsoleWith(executable, title, folder, prompt);
        return new { path = handoff.Path, prompt, pasteRequired = target == "grok", opened = true };
    }
}
