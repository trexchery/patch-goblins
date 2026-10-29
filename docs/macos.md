# Patch Goblins on macOS

Patch Goblins combines the existing Python account engine and macOS menu bar with the same C# Supervisor used by the Windows application. Windows Forms stays in the Windows project. The macOS backend links the existing Director, routing, worktree, issue, goal, verification, approval, and GitHub code directly; there is no second implementation of those policies.

The application targets Apple Silicon and macOS 12 or later. Claude Code, Git, and the GitHub CLI are external tools. Sign in to GitHub using `gh auth login` and enroll Claude accounts using the account controls. An installed application bundles its Python runtime and a self-contained .NET backend; it does not require a system Python or .NET installation.

## Install

From a source checkout on an Apple Silicon Mac, run:

```bash
bash macos/install.sh
```

The installer uses Homebrew to obtain missing build tools, builds the app, checks the archive checksum and app signature, and installs it under `~/Applications`. It preserves an earlier installation with a timestamped name. `PATCH_GOBLINS_INSTALL_DIR` selects another destination.

To install a downloaded CI archive, first extract the GitHub Actions artifact so that the application zip and its `.sha256` file are beside each other, then run:

```bash
bash macos/install.sh /absolute/path/patch-goblins-macos-arm64.zip
```

The [macOS application workflow](https://github.com/trexchery/patch-goblins/actions/workflows/macos.yml) publishes the `patch-goblins-macos-arm64` artifact after its build job succeeds. Its contents are an application zip, a disk image, and checksums. The workflow artifact is available to users with access to the repository and expires after 30 days. An absent artifact or a failed build is not evidence of a deliverable.

Builds use ad hoc signing. Distribution with an Apple Developer ID and notarization requires the maintainer's signing identity and Apple credentials. Gatekeeper approval may therefore be required for a downloaded development build; the installer does not disable Gatekeeper or clear quarantine attributes.

## Use

Launch **Patch Goblins** from Applications. The menu bar retains the existing account and quota controls and adds access to the goblins' work dashboard and agents terminals. **Launch at Login** registers the application using the existing macOS LaunchAgent integration.

Open an agents terminal for a repository to connect a regular Claude Code session to Patch Goblins' existing MCP tools. Starting agents for a repository displays a native approval showing the repository, base branch, and verification commands. The same approval boundary applies when new goal checks would run unattended.

The work menu also exposes manual Prime, the general agent, account aliases and folder assignments, agent settings, and continuing interrupted work between providers. Codex and Grok have their own quota/account submenus, isolated official login flows, account switches, removal and terminals. These controls call the existing provider and handoff engines.

Agents work only while a repository's agents terminal is connected. Closing its final terminal pauses its work. Reopening it reconnects to the running Supervisor. The dashboard also exposes pause/resume, goals, requests, details, and questions waiting for the owner. A protected push remains held until the owner explicitly answers its approval.

The standalone backend also provides commands for diagnostics and terminal integration:

```bash
"$HOME/Applications/Patch Goblins.app/Contents/Resources/backend/patch-goblins-backend" --help
"$HOME/Applications/Patch Goblins.app/Contents/Resources/backend/patch-goblins-backend" status
"$HOME/Applications/Patch Goblins.app/Contents/Resources/backend/patch-goblins-backend" doctor /absolute/path/to/repository
"$HOME/Applications/Patch Goblins.app/Contents/Resources/backend/patch-goblins-backend" agents-terminal --repo /absolute/path/to/repository
```

The application must already be running for status and MCP control calls. The doctor reads the repository and GitHub; its readiness findings distinguish blockers, limitations, and verified evidence.

## State and recovery

Desktop settings, agent state, logs, process records, and worktrees live under `~/Library/Application Support/patch-goblins`. The existing account engine keeps its store under `~/.claude-swap-backup` and retains its macOS Keychain integration. These locations remain outside Git. `PATCH_GOBLINS_DATA` selects a separate desktop instance for development or synthetic testing.

Worker processes run in their own POSIX process groups with file-backed input and output. They can outlive a backend crash; persisted process identities let the Supervisor reattach. Verification processes are tied to their backend parent and terminate if it dies, so a restart verifies again. State writes retain the existing atomic replacement behavior. Corrupt state is preserved for inspection by the existing store loader.

Closing the application normally asks its Supervisor to stop and record work. Killing the application is a crash, with recovery handled at the next start. Repositories without a reconnected agents terminal stop after the existing relink grace period.

The terminal keeps the goblin theme, session commands, status line, and mascot preload. A private temporary copy gives the mascot a path without spaces because the normal macOS Application Support path contains a space. The status line uses a Bash 3.2 compatible reader and clock command on macOS.

## Security boundaries

- Account credentials stay in the existing local account engine and macOS Keychain integration. The UI/backend protocol carries account status and orchestration requests, not credential exports.
- Dormant Codex/Grok credentials use verified macOS Keychain entries. Their files hold only references. Legacy base64 files migrate only after a verified Keychain write; unavailable Keychain access fails closed. Large payloads are split into bounded entries so the existing `security` wrapper keeps secret data on stdin. Windows retains DPAPI sealing.
- The backend uses a single-thread synchronization context, a private instance lock, and the existing per-user named-pipe transport.
- Workers inherit the existing environment restrictions: no usable GitHub token, an empty GitHub configuration, disabled Git credential helpers, blocked network Git protocols, and denied push/GitHub tools. The Supervisor performs authorized GitHub operations after verification and review.
- The base branch's policy applies to a change; a worker cannot relax it by editing its worktree copy. Sensitive files, changed policy checks, pinned bases, and held pushes retain the shared gates.
- Workers run as the signed-in user. These controls prevent mistakes and preserve the existing trust model; they are not an operating-system sandbox against arbitrary hostile code. No new privilege boundary is claimed.
- Logs and owner-facing diagnostics use the shared secret redactor. Synthetic credentials and mocked GitHub responses are used in tests.

## Known gaps against the Windows tray

- Auto-switch uses an exclusive Keychain login and skips accounts held by another session. User terminals, including tabs and workflow sessions, remain open; a session at its limit may need attention from its owner. Patch Goblins does not close or automatically migrate those sessions.
- Headless runs owned by the Supervisor may yield their account when safe; unknown headless runs and user terminals keep their account.

## Build and verification

The reproducible build entry point is:

```bash
bash macos/build-macos.sh
```

It uses the locked Python dependency set, publishes `osx-arm64`, creates the standalone py2app bundle, runs executable checks, signs the app ad hoc, and writes the application zip, disk image, and checksums under `dist/`.

The native workflow has separate orchestration and packaging jobs. Verification includes the existing synthetic Python suite, reused C# routing/stall tests, real-Git worktree and fake-GitHub orchestration tests, POSIX process identity and cleanup tests, backend executable/IPC/approval tests, and application executable smoke checks. The process-host tests kill an actual parent process to distinguish surviving workers from verification that must terminate.

Actual pass counts and build outcomes belong in the draft PR's verification report, tied to the tested commit and workflow run. A configured workflow, source inspection, a Linux test pass, and a successful macOS executable build establish different facts. None substitutes for interactive testing of the macOS menu bar, Keychain consent, Terminal behavior, launch at login, or a real Claude account session.
