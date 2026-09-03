# T130 PowerShell session lifecycle and isolation acceptance

**Date**: 2026-08-30
**PowerShell**: Windows PowerShell 5.1.26100.9168 Desktop
**Windows**: `Environment.OSVersion` 10.0.26200.0; PowerShell BuildVersion 10.0.26100.9168
**Windows Terminal installed**: 1.24.11911.0
**Interactive topology used by this run**: real Windows PowerShell 5.1 processes attached to an
interactive ConPTY, plus deterministic production-loader integration tests. Direct GUI window-close
automation in Windows Terminal was not used and is called out as LIMITED below.

## Published build

- Artifact: `D:\Projects\Terminal Translator\artifacts\publish\t130-session-lifecycle-isolation-acceptance\tt.exe`
- SHA256: `CFAA32F8C9798A526EDB2E56D2AEBF35D30E0BBC5243AF21374D3EBFD6D45849`
- LastWriteTime: `2026-08-30T19:32:11.3536669+08:00`
- Loader: `C:\Users\gutia\AppData\Local\TerminalTranslator\PowerShell\TerminalTranslator.Profile.ps1`
- Loader executable: `D:\Projects\Terminal Translator\artifacts\publish\t130-session-lifecycle-isolation-acceptance\tt.exe`
- PathsEqual: `True`
- T129 remains at its original path and retained SHA256
  `56F8CDC2B6299E9092EDBAFED05F98628B5C56AE747B44947549E544400F2AA8`.

## Acceptance matrix

| Area | Result | Evidence and current contract |
|---|---|---|
| A. `Clear-Host` | PASS | Output after a clear was the only selected output. When `Clear-Host` itself was strict previous, `tt last` returned `Previous command has no translatable output.` and did not replay the earlier command. Clear/prompt VT presentation was absent from retained output. |
| B. Ctrl+C, PowerShell | PASS after fix | A marker-producing long pipeline was interrupted from a real PS5.1 terminal. Its stable record retained partial output, `PowerShellSucceeded=False`, and `WasInterrupted=True`. The next foreground command captured normally. |
| B. Ctrl+C, native | PASS after fix | `ping.exe -t 127.0.0.1` was interrupted. The record retained terminal output, `PowerShellSucceeded=False`, native exit `-1073741510`, and `WasInterrupted=True`; the next command recovered normally. |
| C. Provider work during shutdown | LIMITED | Ctrl+C during an active `tt last` stopped the spinner/provider wait, returned a clean prompt, and a following `exit` completed in about 0.4 s. The exact session directory was deleted and no session watcher remained. A literal Windows Terminal GUI close while provider work is active was not automated; this remains a final manual smoke. |
| D. Rapid sequential commands | PASS | Four commands submitted without sleeps produced four ordered epochs; strict-last selected command four. A no-output command blocked fallback. Alternating PowerShell/native/success/failure records preserved exact sequence; native exit `0`/`5` did not leak into later PowerShell records. |
| E. `tt off` / `tt on` | LIMITED by existing contract | In an ordinary Feature 002 shell these are Feature 001 live-session controls and return `No live translation session.` They do not disable/re-enable capture. Two off/on cycles preserved one capture session, one `PowerShell.Exiting` subscription, and one watcher. Capture preference lifecycle remains `tt configure --capture disabled/enabled`; existing isolated lifecycle tests cover loaded-session disable, cleanup, idempotent installation, and fresh-session re-enable. |
| F. Final `exit` | PASS | Tested several independent shells. Exit returned promptly; each exact session directory disappeared; its cleanup watcher exited; other live shells remained unaffected. |
| G. Multiple PowerShell sessions | PASS | A and B had distinct random IDs. B translated only beta while A translated alpha then gamma. B exit did not affect A, and A captured a new post-B-exit command. No newest-file or cross-session selection occurred. |
| H. Custom prompt | PASS for supported loader-time prompt; LIMITED for runtime replacement | A path-and-VT-color custom prompt defined before loading the managed integration was preserved and excluded from output. Executing `function prompt { "CUSTOM-TT> " }` after integration replaces the TT wrapper itself; capture then cannot observe later boundaries. Runtime prompt replacement is outside the loader-time composition contract and is recorded as a limitation rather than prompting a capture redesign in T130. |
| I. Background / asynchronous output | LIMITED by V1 contract, contamination checks PASS | `Start-Job` output was not surfaced until explicit `Receive-Job`; that receive command then owned it normally. A child released by a named event wrote after prompt return and before the next command; the next record contained foreground output only. V1 has no background provenance tracking: output that arrives after the next submitted echo may be observed in that next interval and is not promised as previous-command output. |

No final matrix item is FAIL. The limitations are explicit existing contract/topology boundaries.

## Confirmed regression and minimal fix

### Exact pre-fix reproduction

```powershell
1..100 | ForEach-Object {
    Write-Output "PowerShell interruption output $_"
    Start-Sleep -Milliseconds 250
}
# Press Ctrl+C after output appears.
tt last
```

The transcript and boundary finalized reliably and later commands recovered, but retained metadata
contained `wasInterrupted:false`. The same defect reproduced with `ping.exe -t 127.0.0.1`.

### Root cause

Windows PowerShell 5.1 correctly exposed `Get-History -Count 1`.ExecutionStatus as `Stopped` for
both cases. The managed profile did not forward that fact, and `CaptureBoundaryProcessor` constructed
every production `CommandBoundaryEvidence` with `WasInterrupted: false`.

### Fix

The prompt wrapper now snapshots `ExecutionStatus == Stopped` into a `was-interrupted` boundary
field. The maintenance bridge parses it, and the production boundary processor passes it through to
the validated/persisted command boundary. Stable transcript acquisition, native-exit attribution,
provider code, spinner timings, privacy behavior, and retained-record selection were not changed.

## New regression tests

- `ManagedLoader_CtrlCMarksPowerShellAndNativeCommandsInterruptedAndRecoversNextEpoch`
  uses output markers as deterministic barriers, sends Ctrl+C through ConPTY, covers both PowerShell
  and native commands, and proves the following epoch is clean.
- `ProductionFinalization_PersistsReliableInterruptionThroughStrictPreviousRetrieval`
  proves partial output and the interruption flag survive publication and strict retrieval.

## Automated results

- Core: 147/147 PASS
- Windows: 126/126 PASS
- CLI: 237/237 PASS
- Total: 510/510 PASS
- Release build: 0 warnings, 0 errors
- Publish-gate subset: CLI 237/237, Core 147/147, capture/exit-attribution 24/24 PASS

One existing Feature 001 test,
`RunnableUserStory1AcceptanceTests.RealConPtyControlAndProductionProvider_ReachesCompanionThroughEventPipe`,
timed out once during the first publish attempt after passing in the preceding full CLI run. It then
passed three isolated repetitions, passed the next complete 237-test CLI run, and the publish
succeeded. This is recorded as an observed intermittent test timeout; no T130 code path or assertion
failed. A separate initial solution-parallel run encountered 63 uniform Win32 5 child-process launch
failures; all three projects passed when run serially, so those were host process-concurrency failures,
not product regressions.

The live DeepSeek endpoint also intermittently returned the product's normalized provider-failure
message during manual calls. Capture selection evidence was therefore additionally verified from
the committed session records; no raw capture was written to ordinary diagnostics.

## Short manual Windows Terminal smoke

Open two new Windows PowerShell 5.1 tabs after confirming the loader path above.

```powershell
# Tab A
Write-Output "Session A completed the alpha operation."
tt last

# Tab B
Write-Output "Session B completed the beta operation."
tt last

# Either tab: strict Clear-Host and Ctrl+C
Write-Output "This older output must not replay."
Clear-Host
tt last
ping.exe -t 127.0.0.1   # press Ctrl+C after replies appear
tt last
Write-Output "Capture recovered after interruption."
tt last
exit
```

Expected: A never shows B; B never shows A; `Clear-Host` reports no translatable output; the
interrupted native record is labeled interrupted when provider assistance succeeds; the recovery
command translates normally; the tab exits without a lingering spinner or hang.
