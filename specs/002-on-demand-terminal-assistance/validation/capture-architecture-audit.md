# Gate E — Capture architecture audit

**Result:** PASS  
**Recorded:** 2026-08-26  
**Branch:** `002-on-demand-terminal-assistance`  
**Audited HEAD:** `414ba31171931f1743dc721ed7bf9cd8b69a2df7`  
**Audit scope:** HEAD plus the current Feature 002 production worktree. The worktree is intentionally
dirty with the implementation already covered by Gates A–C; this audit did not change production
or test code.

## Accepted boundary

The accepted ADR permits one Feature 002 production content source: bounded PowerShell Transcript
plus session/command metadata. It rejects Console Buffer / `CONOUT$` and all other capture-source
fallbacks. `OversizedCommandRetention` is a retained representation policy applied after capture;
it is not another source.

## Reproducible searches

Run from the repository root:

```powershell
rg -n -i --glob '*.cs' --glob '*.ps1' 'CONOUT\$|ReadConsoleOutput(?:Character|W)?|ConsoleBuffer|console buffer capture|exportBuffer|terminal scraping|screen scraping|viewport capture|alternate capture source|Out-Default|Tee-Object|\bTee\b' src

rg -n -i --glob '*.cs' --glob '*.ps1' 'ConPTY|ConPty|ConsoleInputRelay|ConsoleOutputRelay|VT extraction|VtOutput|CompanionPane|CompanionRenderer' src

rg -n -i --glob '*.cs' --glob '*.ps1' 'mtime|LastWriteTime|CreationTime|latest[- ]file|most recent|newest|fallback|fall back|search backward|older command|previous.*previous|OrderByDescending' src/TerminalTranslator.Core/Assistance src/TerminalTranslator.Core/Capture src/TerminalTranslator.Windows/Capture src/TerminalTranslator.Windows/PowerShell src/TerminalTranslator.Cli/Commands/LastCommand.cs src/TerminalTranslator.Cli/Commands/AskCommand.cs src/TerminalTranslator.Cli/Commands/OnDemandAssistanceRuntimeComposition.cs src/TerminalTranslator.Cli/Commands/ProductionPreviousCommandRetriever.cs

rg -n --glob '*.cs' --glob '*.ps1' 'Start-Transcript|Stop-Transcript|StableTranscriptSnapshot|CaptureBoundaryProcessor|CapturedCommandFinalizer|OversizedCommandRetention|RetainedCaptureStore|PreviousCommandRetriever|TT_CAPTURE_SESSION|SessionIdentity|SessionNonce|CommandSequence' src/TerminalTranslator.Core src/TerminalTranslator.Windows src/TerminalTranslator.Cli
```

## Results

The rejected-mechanism search returned no production `src/` matches. Feature 002 contains no
`CONOUT$`, `ReadConsoleOutput*`, Console Buffer reader, Windows Terminal buffer/export call,
viewport/screen scraper, `Out-Default`, Tee, alternate content source, or new ConPTY capture source.

The recency search found no mtime/creation-time/latest-file/current-session heuristic and no
`OrderByDescending` selection. `ProductionPreviousCommandRetriever` constructs exactly one session
directory from the environment-provided GUID, then validates nonce, current-user SID, direct parent
PID, process-start identity, and integration version before loading that session's single committed
manifest. Missing, unhealthy, inconsistent, or corrupt state fails closed.

`ContextualCommandSelection.SelectStrictTarget` may skip records explicitly classified as the
contextual commands `tt last` or `tt ask last`. This is the specified self-pollution rule, not an
output/recency fallback: an ordinary empty-output command remains the strict target, and corrupt,
unhealthy, wrong-session, or failed-latest-finalization state cannot select an older useful record.
Question-only `tt ask` is not skipped.

## Actual Feature 002 production call graph

```text
PowerShell CurrentUser/CurrentHost profile TT-owned block
  -> managed TerminalTranslator.Profile.ps1 loader
  -> CaptureSessionBootstrap creates GUID + nonce + SID/PID/start/version proof
  -> Start-Transcript into per-command staging-N.txt
  -> wrapped prompt snapshots history/exit facts and calls Stop-Transcript
  -> __capture boundary -> CaptureMaintenanceBridge
  -> CaptureBoundaryProcessor validates direct owner, session, nonce, sequence, staging path
  -> StableTranscriptSnapshot obtains a stopped, length/hash-stable snapshot
  -> transcript command echo/output extraction
  -> CommandBoundaryValidator
  -> trusted CapturedCommand -> CapturedCommandFinalizer
  -> CaptureRetentionPolicy -> RetainedCaptureStore.PublishAsync
  -> atomic committed manifest and retained record files
  -> ProductionPreviousCommandRetriever validates the same current-session proof
  -> RetainedCommandRecordCodec -> PreviousCommandRetriever
  -> tt last / tt ask last coordinator
```

Relevant production paths:

- `src/TerminalTranslator.Windows/PowerShell/TerminalTranslator.Profile.ps1`
- `src/TerminalTranslator.Windows/PowerShell/CaptureSessionBootstrap.cs`
- `src/TerminalTranslator.Cli/Commands/CaptureMaintenanceBridge.cs`
- `src/TerminalTranslator.Windows/Capture/CaptureBoundaryProcessor.cs`
- `src/TerminalTranslator.Windows/Capture/StableTranscriptSnapshot.cs`
- `src/TerminalTranslator.Windows/Capture/CapturedCommandFinalizer.cs`
- `src/TerminalTranslator.Core/Capture/CaptureRetentionPolicy.cs`
- `src/TerminalTranslator.Core/Capture/OversizedCommandRetention.cs`
- `src/TerminalTranslator.Windows/Capture/RetainedCaptureStore.cs`
- `src/TerminalTranslator.Cli/Commands/ProductionPreviousCommandRetriever.cs`
- `src/TerminalTranslator.Core/Capture/PreviousCommandRetriever.cs`

## Oversized-command path

The oversized path does not branch before capture or validation. It is:

```text
same stopped Transcript snapshot
  -> same validated CapturedCommand candidate
  -> same CapturedCommandFinalizer
  -> CaptureRetentionPolicy reports candidate cannot fit
  -> OversizedCommandRetention creates UTF-8-safe HEAD + TAIL LocalHeadTail representation
  -> metadata and complete committed-generation overhead are remeasured
  -> same CaptureRetentionPolicy
  -> same RetainedCaptureStore.PublishAsync
  -> same ProductionPreviousCommandRetriever / PreviousCommandRetriever
```

`OversizedCommandRetention` preserves command/session/boundary identity and original byte count. It
changes only the retained representation and returns to ordinary candidate publication; there is no
secondary content acquisition path or publication shortcut.

## Feature 001 false-positive exclusions

The ConPTY search finds the established Feature 001 live architecture under
`TerminalTranslator.Windows/ConPty`, `ConsoleInputRelay`, `ConsoleOutputRelay`, `HostCommand`, VT
extraction, IPC, and companion rendering. Its reachable root is the hosted `tt start`/host pipeline.

Feature 002's roots are `TerminalTranslator.Profile.ps1`, `__capture`, and
`OnDemandAssistanceRuntimeComposition`. They do not reference `ConPtySession`, either console relay,
the live translation pipeline, IPC, VT extraction, or `CompanionRenderer`. The profile loader also
returns immediately when `TT_HOSTED_SESSION_ID` is present, and bootstrap independently reports
`HostedFeature001Excluded`. Therefore the repository's legal Feature 001 ConPTY code is not a Gate E
failure and is unreachable from Feature 002 capture composition.

## Conclusion

**Gate E = PASS.** Feature 002 production capture uses only PowerShell Transcript plus strict
session/command metadata. There is no Console Buffer fallback, alternate terminal source, recency
lookup fallback, or Feature 002 ConPTY source. The oversized policy remains downstream of the same
Transcript capture and publishes through the same retained store.
