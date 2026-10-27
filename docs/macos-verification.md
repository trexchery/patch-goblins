# macOS verification evidence

The implementation was developed in the isolated `feat/macos-parity` worktree. Tests used synthetic accounts, local Git origins and mocked GitHub operations. The original checkout's uncommitted Windows work was left intact.

## DDC execution, 27 October 2026

All development test execution ran on the already available DDC box and Claude's automatically provisioned replacement, in isolated directories and homes. No additional box was rented by this task.

| Verification | Observed result |
| --- | --- |
| Updated full Python regression suite with the actual backend executable enabled | 2,530 passed, 17 skipped, 5 warnings; 19.30 seconds |
| Four separate backend test processes | 24 passed in each shard, 96 total; zero failures or skips |
| Current backend fixes, process/security regressions, upkeep and quota recovery | 37 passed; zero failures or skips |
| Union of backend TRX test identities | 99 distinct passing tests, including all 63 existing orchestration end-to-end tests; overlapping focused checks counted once |
| Final expanded menu/provider/settings controls | 32 passed; 0.80 seconds |
| Native CI sharder discovery against the current compiled project | 99 methods discovered; deterministic first shard contains 25 |

The full Python result includes the real JSON-lines backend protocol, private IPC peer, persisted settings, concurrent approval rejection and single-instance lock checks. The process regressions include actual parent crashes, worker survival, verification termination, stale recovery records, unrelated process protection, literal arguments, private metadata and executable worktree permissions.

The reused orchestration harness exercises real processes and Git worktrees with the existing fake credential engine and fake GitHub CLI: Director plans, routing, goals, issues, reviews, verification failures, PR creation and feedback, policy and sensitive-file holds, approval changes, quota recovery, pause/resume and restart recovery. These are synthetic orchestration tests, not inference with real subscriptions.

Raw logs, the four-shard manifest, TRX files and the deduplicated summary were copied to the ignored local `build/verification-ddc/` directory before the DDC lease expired.

## Native Apple Silicon execution

The first verified distributable was built from `909d898` by [workflow run 40565145486](https://github.com/trexchery/patch-goblins/actions/runs/40565145486), packaging job `122404552952`. Its [download artifact](https://github.com/trexchery/patch-goblins/actions/runs/40565145486/artifacts/12762936537) contains the application ZIP, DMG and checksums.

That macOS runner successfully performed:

- A self-contained `osx-arm64` .NET publication and standalone py2app application build.
- Ad hoc code signing and `codesign --verify --deep --strict`.
- Execution of both frozen credential-engine launchers and the backend executable.
- A frozen worker with real file input/output, durable exit status 7 and private recovery metadata.
- Startup of the actual rumps menu bar, connection to the Supervisor and a bundled-engine account snapshot, under an isolated home.

The subsequent native run [40566350463](https://github.com/trexchery/patch-goblins/actions/runs/40566350463) at `6327090` passed all five jobs: packaging plus all four orchestration shards. It also verified actual Terminal launching with an executable fake engine, quoted paths, generated MCP, goblin theme and status-line files, and actual executable/IPC/approval checks in each shard.

The current code snapshot is `92ef5ab`, which adds the final provider Keychain storage, expanded menu controls and shared manual Prime/handoff actions. Its [native run 40567645402](https://github.com/trexchery/patch-goblins/actions/runs/40567645402) has successfully built and smoke-tested the application; the [current download](https://github.com/trexchery/patch-goblins/actions/runs/40567645402/artifacts/12762814228) includes those changes. The four orchestration jobs were still running at the progress checkpoint. Final outcomes are recorded in [draft PR #16](https://github.com/trexchery/patch-goblins/pull/16).

## Verification limits

No real Claude subscription credentials were used. Interactive OAuth consent and a complete logout/login cycle for launch-at-login have not been exercised. Builds use ad hoc signing; Developer ID signing and Apple notarization require credentials that were not supplied. GitHub's existing Windows and macOS regression jobs remain in place; their current results are visible on the draft PR.
