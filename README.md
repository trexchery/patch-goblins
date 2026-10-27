<p align="center">
  <img src="assets/readme/hero.svg" width="100%" alt="Patch Goblins: your Claude accounts, one coding team. A pixel-art goblin beside four team members: you, two goblins building pull requests, and one resting until its quota resets.">
</p>

<p align="center">
  <a href="https://github.com/trexchery/patch-goblins/releases/latest"><img src="https://img.shields.io/github/v/release/trexchery/patch-goblins?style=flat-square&color=56a83e&label=download" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows%2011-x64-2e6b30?style=flat-square" alt="Windows 11 x64">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-2e6b30?style=flat-square" alt="MIT license"></a>
  <img src="https://img.shields.io/badge/telemetry-none-2e6b30?style=flat-square" alt="No telemetry">
</p>

<p align="center">
  <b>Stop rationing your week.</b> When one Claude account hits its limit, the others sit idle.<br>
  Patch Goblins sends work to whichever account has room: goblins build, test, and review your issues, then open pull requests you merge.
</p>

<p align="center">
  <img src="assets/readme/proof.png" width="100%" alt="Left: the Patch Goblins agents terminal, Claude Code with a green pixel goblin in place of its mascot and a Patch Goblins status line. Right: the tray flyout listing five accounts with 5-hour and weekly quota meters; two are working on pull requests, one is resting near its limit.">
</p>

## How it works

<p align="center">
  <img src="assets/readme/workflow.svg" width="100%" alt="Five steps: you set a goal or point at issues; the Director plans steps and picks a model and effort; goblins build, each in its own worktree on a spare account; your tests run, then a fresh read-only review; a pull request is opened and you merge.">
</p>

Type a goal into the agents terminal, like `goal: the app has a settings page; npm test must pass`, or say *"work through the open issues"*. That is the whole interface.

## Why it's different

- **🟢 Spare quota becomes finished work.** Every run is routed to the account with the most room in that model's own weekly window. Goblins step aside while you're using an account, rest near their limits, and pick up again when a window resets.
- **🧪 Nothing reaches you unchecked.** Each change runs your verification command, then gets a fresh read-only review. Failures go back to the same session, and the model or effort is raised only when it has to be.
- **🔒 Goblins can't push.** Their environment has no GitHub login. Patch Goblins pushes only after the checks pass, and holds anything that touches `.github/`. Goblins never merge, deploy, or close issues.
- **🪟 Built for Windows.** A native tray app with no WSL, Docker, or browser cookies. Logins stay on your PC, encrypted with DPAPI.

## How it compares

|  | Patch Goblins | [claude-swap](https://github.com/realiti4/claude-swap) | [Claude Squad](https://github.com/smtg-ai/claude-squad) | [Conductor](https://www.conductor.build/) |
|---|:---:|:---:|:---:|:---:|
| Several Claude accounts, quota meters | ✅ | ✅ | – | – |
| Parallel agents in worktrees | ✅ | – | ✅ | ✅ |
| Work routed by each account's quota | ✅ | – | – | – |
| Tests + review before every PR | ✅ | – | – | manual |
| Native Windows | ✅ tray | CLI | – (tmux) | – (macOS) |

Patch Goblins is built on claude-swap's credential and quota engine. Both are local, with no proxy and no cloud.

## Quick start

**Apple Silicon macOS:** see [installation, native menu bar, and verification](docs/macos.md). From a source checkout, `bash macos/install.sh` builds and installs the app. Development downloads are attached to the [macOS workflow](https://github.com/trexchery/patch-goblins/actions/workflows/macos.yml) after its packaging job succeeds.

> Needs Windows 11 x64, [Claude Code](https://docs.anthropic.com/en/docs/claude-code), [Git for Windows](https://git-scm.com/download/win) and the [GitHub CLI](https://cli.github.com/) (`gh auth login`).

1. **Download** `patch-goblins-win-x64.zip` from the [latest release](https://github.com/trexchery/patch-goblins/releases/latest), extract it, and run `patch-goblins.exe`.
2. **Add your team.** Open the tray icon and use **+** to sign in each Claude account through the official CLI.
3. **Click Agents** and pick a repository. A goblin-green Claude Code terminal opens there.
4. **Give it a goal**, then confirm the dialog. It shows the target branch and the exact verification command.

Goblins work only while that terminal is open. Close it and they pause; reopen it and they continue. Nothing runs behind your back.

## What's inside

<table>
<tr>
<td width="33%" valign="top">

**👥 Team**

- 5-hour and weekly meters for every account
- one-click switching that never closes a session
- pin a terminal or a folder to an account
- Codex and Grok Build meters as visitors

</td>
<td width="33%" valign="top">

**🛠️ Work**

- issues or goals, ordered by dependency
- Haiku, Sonnet, or Opus, picked per step
- one worktree and branch per step
- resumes after crashes and restarts
- goals can span several repositories

</td>
<td width="33%" valign="top">

**🔋 Upkeep**

- auto-switch near a limit (opt-in)
- Auto-Prime starts fresh 5-hour windows early
- quota notifications
- resume sessions a limit stopped

</td>
</tr>
</table>

Full details, limits and costs: **[docs/agents.md](docs/agents.md)**.

> [!IMPORTANT]
> **Read Anthropic's terms first.** The [consumer terms](https://www.anthropic.com/legal/consumer-terms) (§3) restrict automated or scripted access to subscriptions, and Pro/Max limits assume ordinary individual use. Running agents or Auto-Prime on your accounts is your call and your risk. Agents skip API-key accounts, and stop before a subscription with extra usage turned on reaches its switch threshold.

<details>
<summary><b>Good to know</b></summary>

- **You approve.** A change is held for you when two review passes still find problems. Questions get posted on the issue.
- **Only your issues.** Issues from strangers are never picked up. Only comments from you or the repository's collaborators count.
- **One account works too.** Agents then share it: they queue while your own terminals are busy and never close a session of yours.
- **The goblin terminal** is a session-only theme. A small preload script swaps Claude Code's mascot glyphs for the goblin on their way to the screen. Nothing of Claude's is modified or installed.
- **Unsigned releases**, so SmartScreen may warn. A `.sha256` checksum ships with every download. See [SECURITY.md](SECURITY.md).
- **Upgrading** from handoff, Autodock, or ClautoSwap: quit the old tray and run the new exe once. Settings, agents, and logins carry over.

</details>

<details>
<summary><b>Build from source</b></summary>

Requires [uv](https://docs.astral.sh/uv/), the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), and PowerShell 7.

```powershell
git clone https://github.com/trexchery/patch-goblins.git
cd patch-goblins
pwsh dock\build.ps1
```

README art: `python dock/tools/make-readme-art.py` (from the same sprite as the app icon).

</details>

<p align="center"><img src="assets/goblin.png" width="48" alt="Patch Goblins goblin"></p>
<p align="center"><sub>Built on <a href="https://github.com/realiti4/claude-swap">claude-swap</a> by Onur Cetinkol · Patch Goblins by trexchery · <a href="LICENSE">MIT</a></sub></p>
<p align="center"><sub>Independent project, not affiliated with Anthropic, OpenAI, or xAI.</sub></p>
