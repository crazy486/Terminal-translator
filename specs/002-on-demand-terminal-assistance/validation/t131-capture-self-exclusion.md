# T131 native capture stabilization and contextual self-exclusion

**Date**: 2026-08-30
**PowerShell stress host**: Windows PowerShell 5.1.26100.9168 Desktop, interactive ConPTY
**Published artifact**: `D:\Projects\Terminal Translator\artifacts\publish\t131-capture-self-exclusion-acceptance\tt.exe`

## Exact root causes

### A. Failed native stdout could disappear

`Stop-Transcript` did not constitute producer completion in Windows PowerShell 5.1. Transcript
pipeline completion moves buffered lines into `TranscriptionOption.OutputBeingLogged`, then dispatches
`FlushContentToDisk` on a background task. During the gap before that task opened/appended the file,
the staging file was exclusively readable. `StableTranscriptSnapshot` therefore could observe the
same incomplete length and SHA256 twice and publish it before the writer appended native stdout.

The fix captures the active `TranscriptionOption`, invokes transcript pipeline completion and
`FlushContentToDisk` synchronously before `Stop-Transcript`, then waits with a 750 ms bound until
both `OutputToLog` and `OutputBeingLogged` are empty. The boundary bridge forwards explicit
`transcript-drained=true`; finalization rejects the candidate without that producer-lifecycle
evidence. Stable snapshot acquisition additionally requires producer completion before and after
each exclusive read. No fixed stabilization sleep was added.

### B. `tt last` self-output could become the next target

Contextual classification parsed only normalized command text equal to `tt last` or beginning with
`tt ask last`. PowerShell history records `& $tt last` and `& 'C:\...\tt.exe' last` literally, so
those records were published as ordinary commands. Provider failure text was consequently eligible
on the next invocation.

The production CLI now identifies contextual intent from its parsed process arguments before any
success, provider, privacy, timeout, cancellation, or error path. It writes a content-free marker
bound to the validated capture session, direct PowerShell owner, and current sequence. Boundary
finalization consumes that marker into `IsContextualAssistanceCommand`; selection uses the persisted
classification. Command-path spelling, variable invocation, provider outcome, and rendered output
are no longer part of the identity decision. Existing string recognition remains only as a
backward-compatible fallback for older/unmarked records.

## Regression and stress evidence

- Deterministic snapshot test holds producer completion false across multiple identical immediate
  observations, appends the delayed native drain, then releases the barrier and proves only the
  complete bytes are returned.
- Production-loader stress executes 30 repetitions of
  `cmd /d /c "echo The native command reported failure. & exit 5"`. Every finalized/published record
  contains the expected stdout and native exit 5.
- The same stress matrix covers stdout + exit 0, implicit exit 0, stderr + exit 7, and a longer
  native stdout payload. All 34 cases passed.
- Session-marker tests cover `& $tt last`, a full executable-path form, success, provider failure,
  timeout, no-English, privacy skip, cancellation, following real-command selection, and production
  retained-record serialization.
- Full T127–T130 regression solution: 519/519 PASS.

## Automated totals and publish gate

- Core: 147/147 PASS
- CLI: 237/237 PASS
- Windows: 135/135 PASS
- Total: 519/519 PASS
- Release build: 0 warnings, 0 errors
- Manual-publish subset: CLI 237/237, Core 147/147, Windows capture/boundary 25/25 PASS

## Published build

- Executable: `D:\Projects\Terminal Translator\artifacts\publish\t131-capture-self-exclusion-acceptance\tt.exe`
- Size: 74,688,665 bytes
- LastWriteTime: `2026-08-30T20:39:35.1773673+08:00`
- SHA256: `4A0CEE5213BBF2381F536A3B6E17D30276C52A7EB30835C547A251736C267243`
- Loader: `C:\Users\gutia\AppData\Local\TerminalTranslator\PowerShell\TerminalTranslator.Profile.ps1`
- Loader executable: same artifact path
- PathsEqual: `True`

## Shortest manual acceptance

Open a fresh Windows PowerShell 5.1 tab after the loader update:

```powershell
$tt = 'D:\Projects\Terminal Translator\artifacts\publish\t131-capture-self-exclusion-acceptance\tt.exe'
cmd /d /c "echo The native command reported failure. & exit 5"
& $tt last
& $tt last
```

The first `last` must target the native stdout with native exit 5. Regardless of provider success or
failure, the second `last` must still target that native command and must never translate a prior
`[tt] ...` result. Repeat once with the literal full path:

```powershell
& 'D:\Projects\Terminal Translator\artifacts\publish\t131-capture-self-exclusion-acceptance\tt.exe' last
```
