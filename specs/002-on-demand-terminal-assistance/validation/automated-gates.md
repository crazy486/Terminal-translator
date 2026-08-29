# Gate A — Release automated suite

**Result:** PASS  
**Recorded:** 2026-08-25T15:52:02Z  
**Branch:** `002-on-demand-terminal-assistance`  
**HEAD:** `414ba31171931f1743dc721ed7bf9cd8b69a2df7`

## Command

```powershell
dotnet test TerminalTranslator.sln --configuration Release --no-restore -p:UseAppHost=false
```

## Actual result

| Metric | Actual |
|---|---:|
| Total | 461 |
| Passed | 461 |
| Failed | 0 |
| Skipped | 0 |
| Duration | 1m 36.112s |
| Process exit code | 0 |

Assembly results:

| Assembly | Result | Duration |
|---|---|---:|
| `TerminalTranslator.Core.Tests.dll` | Passed | 1.302s |
| `TerminalTranslator.Windows.Tests.dll` | Passed | 18.223s |
| `TerminalTranslator.Cli.Tests.dll` | Passed | 1m 35.841s |

This rerun includes the new production-root command composition tests, published-like committed-store
retrieval test, production finalizer LocalHeadTail round-trip, essential-metadata-overflow fail-closed
case, and disk-to-retriever-to-AI-selector-to-renderer dual-truncation journey.

## Flake observation

Gate A passed without an isolated rerun. The prior T077 profile-lifecycle transient exception
difference did not recur. The `StubTranslationServer` cancellation/dispose race did not recur.

## 2026-08-27 real-WT bootstrap remediation refresh

**Current remediation result:** BLOCKED by validation host process policy; not a Gate A pass.

The required command discovered 465 tests and completed 411 passed / 54 failed. Every failure had
the same environmental signature: direct `powershell.exe` startup returned access denied or ConPTY
reported Win32 5 before the scenario could execute. Core passed in full; there were no product
assertion failures. Before the host began rejecting nested PowerShell creation, the new
loader/bootstrap focused suite passed 8/8. Process-free bootstrap, identity, storage/ACL,
transcript-controller, health, profile, configure, bridge, and content-free-failure suites later
passed 26/26. Gate A must be rerun to 465/465 before manual acceptance resumes. The historical
461/461 evidence above applies only to the pre-remediation binary.

## 2026-08-27 Feature 001 / Feature 002 coexistence remediation

**Current result:** PASS.

After 360 was disabled, process creation returned to normal and the remaining 29 CLI failures were
traced to `RealConPtyAnalysisRegressionTests` launching generic `ConPtySession.Start`, inheriting the
real user profile, and therefore omitting the production `TT_HOSTED_SESSION_ID` exclusion marker.
The runner now uses the production hosted PowerShell path with a controlled temporary profile. A new
coexistence regression loads the managed Feature 002 profile block with capture enabled and proves
that the hosted guard prevents bootstrap, session-directory creation, transcript creation, warning
output, and provider contamination while the shell remains interactive.

Required full Gate A command result:

| Metric | Actual |
|---|---:|
| Total | 466 |
| Passed | 466 |
| Failed | 0 |
| Skipped | 0 |
| Duration | 1m 31.211s |
| Process exit code | 0 |

Assembly durations were Core 1.067s, Windows 15.900s, and CLI 1m 30.996s. The increase from 465 to
466 is the new deterministic coexistence regression. No test was skipped.
