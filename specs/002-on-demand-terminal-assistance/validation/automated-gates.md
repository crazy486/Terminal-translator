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
