# macOS delivery checkpoint — 27 October 2026, 19:26 UTC

## Saved state

- Repository: `trexchery/patch-goblins`.
- Isolated branch: `feat/macos-parity`.
- Worktree: `C:/Users/Nicolas/.codex/worktrees/macos-parity/autodock-restore`.
- Implementation snapshot: `92ef5abe766eb34d68b857957e4a17fce5f92da9`, already pushed.
- Draft PR: <https://github.com/trexchery/patch-goblins/pull/16>.
- Original checkout: `C:/Users/Nicolas/autodock-restore`; its pre-existing uncommitted changes were not modified.
- User's latest instruction: continue working; save progress and use `[skip ci]` on subsequent pushes. Do not bypass that instruction with manual CI dispatches. Previously started CI may finish.

## Implemented

The macOS application reuses the existing C# orchestration sources in a .NET backend, the existing Python credential and quota engines, rumps, Textual, Keychain and launch-agent code. Implemented surfaces include Claude/Codex/Grok accounts and quotas, isolated enrollment, routing, Director, autonomous agents, worktrees, goals/issues, PRs, verification and policy gates, owner approvals, branded terminals, aliases and folder assignments, provider handoffs, manual/automatic Prime, notifications, settings, pause/resume and recovery. Windows Forms stays in the Windows project.

Codex/Grok dormant credentials use strict verified, chunked Keychain storage on macOS. Reference files contain no credential payload. Legacy migration and unavailable Keychain access fail closed; chunks are size-checked to keep all secret writes on stdin. Windows DPAPI behavior remains intact.

## Genuine verification evidence

- DDC full updated Python suite: **2,530 passed, 17 skipped**, including four actual backend executable/IPC/approval tests and the new provider Keychain tests.
- DDC final menu/provider convenience check: **32 passed**.
- DDC backend TRX union: **99 distinct passing tests**, including all **63 existing end-to-end orchestration tests**. Four complete shards passed 24 tests each; a later 37-case focused run covers final security/upkeep fixes and additional cases without counting overlaps twice.
- Native macOS run `40566350463` at `6327090`: **all five jobs passed**, including four orchestration shards, actual backend host tests, codesign verification, frozen worker, native menu/backend/engine startup and real Terminal/MCP/theme smoke.
- Native run `40567645402` at `92ef5ab`: **all five jobs passed**, including all 99 unique backend cases, all 63 E2E cases, four executable/IPC/approval cases on every shard, packaging and all executable/native/Terminal smoke checks.
- Current verified build download: <https://github.com/trexchery/patch-goblins/actions/runs/40567645402/artifacts/12762814228>. It contains the `.app` ZIP, DMG and SHA-256 files, including the provider storage/control changes.

Raw DDC logs, TRX reports, the shard manifest and the deduplicated summary are preserved locally under the ignored `build/verification-ddc/` directory. Final native reports are under `build/verification-macos-92ef5ab/` and `build/native-package-92ef5ab.log`. The final ZIP/DMG and checksums are downloaded to `dist/macos-92ef5ab/`. Their exact bytes passed SHA-256 verification on the replacement DDC box. These generated artifacts are intentionally outside the source commit.

## Remaining execution work

1. Let existing regression CI run `40567645507` finish; only its Windows job remains active. Linux and macOS Python jobs passed.
2. Update the PR with the Windows outcome when available. If another source fix is necessary, test it on the currently available DDC box, save it with `[skip ci]`, and report that no new native run was triggered.
3. Keep claims bounded: no real subscription credentials/inference were used. Interactive OAuth consent and a full logout/login transition remain untested. Developer ID signing/notarization credentials were not supplied; builds are ad hoc signed.

Claude's replacement DDC lease was already detected and reused after the old lease ended. If that lease ends while more tests are required, check for the next existing ready lease about once per minute; do not rent a separate box. The ignored helpers `build/ddc-sync.py` and `build/ddc-evidence.py` refresh source/evidence without printing credentials.
