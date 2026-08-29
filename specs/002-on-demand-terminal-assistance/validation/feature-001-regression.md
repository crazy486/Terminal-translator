# Feature 001 explicit regression matrix

**Final result:** PASS  
**Branch / HEAD:** `002-on-demand-terminal-assistance` / `414ba31171931f1743dc721ed7bf9cd8b69a2df7`

## Command

```powershell
dotnet test TerminalTranslator.sln --configuration Release --no-restore -p:UseAppHost=false --filter "FullyQualifiedName~CliContractTests|FullyQualifiedName~RunnableUserStory1AcceptanceTests|FullyQualifiedName~UserStory1AcceptanceTests|FullyQualifiedName~UserStory2AcceptanceTests|FullyQualifiedName~UserStory3AcceptanceTests|FullyQualifiedName~UserStory4AcceptanceTests|FullyQualifiedName~ProductionClassifierOwnershipRegressionTests|FullyQualifiedName~ProductionPipelineRawLossRegressionTests|FullyQualifiedName~ProductionPipelineTeardownRegressionTests|FullyQualifiedName~ProductionRuntimeJourneyTests|FullyQualifiedName~EnglishCandidateClassifierTests|FullyQualifiedName~SessionLifecycleTests|FullyQualifiedName~ConPtyRelayTests|FullyQualifiedName~SessionPipeSecurityTests|FullyQualifiedName~SessionStartupTests|FullyQualifiedName~TranslationProviderContractTests|FullyQualifiedName~ProviderFailureContractTests|FullyQualifiedName~FailureIsolationTests|FullyQualifiedName~CompanionRendererContractTests"
```

## Final clean rerun

| Metric | Actual |
|---|---:|
| Total | 160 |
| Passed | 160 |
| Failed | 0 |
| Skipped | 0 |
| Duration | 22.354s |
| Process exit code | 0 |

Assembly durations were Core 1.311s, Windows 12.455s, and CLI 22.073s.

## First-run timing observation

The first matrix run was 159/160: `FailureIsolationTests.ProviderIgnoringCancellation_DisableDetachesOldWorkAndSuppressesLateResult`
observed `oldWork.IsCompleted == false` after a single `Task.Yield` (23ms). The unchanged Feature 001
test passed immediately in an isolated 1/1 rerun and the complete identical matrix then passed
160/160. This was recorded as a scheduling-sensitive test observation, not hidden as a clean first
attempt. It is unrelated to T077 and `StubTranslationServer`, neither of which failed in Gate A.

## Coverage result

`tt start`, `tt on`, `tt off`, `tt status`, live classifier ownership, raw-loss/teardown, ConPTY
relay, IPC security, session startup, provider safe transport, failure isolation, and the production
runtime journey all passed. Feature 002 on-demand composition remains outside
`ProductionTranslationPipeline`, `TranslationWorkQueue`, `TranslationWorker`, ConPTY, IPC, and the
companion renderer.

## 2026-08-27 bootstrap remediation refresh

**Current remediation result:** BLOCKED by the same validation-host Win32 5 process-start policy as
Gate A. The unchanged 160-test matrix completed 147 passed / 13 failed; all 13 failures occurred
before their PowerShell/ConPTY scenario started (`powershell.exe` access denied or ConPTY Win32 5).
No Feature 001 product assertion failed. The historical 160/160 result above applies only to the
pre-remediation binary and must be rerun cleanly for the new artifact.

## 2026-08-27 coexistence remediation rerun

**Current remediation result:** PASS.

The unchanged formal Feature 001 matrix completed 160 passed / 0 failed / 0 skipped in 22.202s.
This includes `tt start/on/off/status`, live classifier ownership, raw-loss/teardown, ConPTY relay,
IPC security, session startup, provider contracts, failure isolation, companion rendering, and the
production runtime journey. The separate RealConPty analysis class completed 34/34, including the
new managed-profile hosted-exclusion regression and both actual CLI assembly fixture cases.
