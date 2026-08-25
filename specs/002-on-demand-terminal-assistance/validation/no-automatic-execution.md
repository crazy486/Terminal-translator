# Gate F — No automatic execution audit

**Result:** PASS  
**Recorded:** 2026-08-26  
**Branch / HEAD:** `002-on-demand-terminal-assistance` / `414ba31171931f1743dc721ed7bf9cd8b69a2df7`  
**Audit scope:** HEAD plus current Feature 002 production worktree. No production or test code was
changed by this gate.

## Model-response production call graph

All three published command paths terminate in the inline text writer:

```text
tt last
ChatCompletionAssistanceProvider.CompleteAsync
  -> JSON translation + recommendation
  -> AssistanceResult
  -> LastAssistanceCoordinator -> LastAssistanceOutcome
  -> LastCommand
  -> InlineAssistanceRenderer.Render
  -> TextWriter.WriteLine (Console.Out in production)

tt ask
ChatCompletionAssistanceProvider.CompleteAsync
  -> JSON answer
  -> AssistanceResult.CreateAnswer
  -> AskAssistanceCoordinator -> AskAssistanceOutcome
  -> AskCommand
  -> InlineAssistanceRenderer.RenderAnswer
  -> TextWriter.WriteLine (Console.Out in production)

tt ask last
ChatCompletionAssistanceProvider.CompleteAsync
  -> JSON answer
  -> AssistanceResult.CreateAnswer
  -> AskLastAssistanceCoordinator -> AskAssistanceOutcome
  -> AskCommand last action
  -> InlineAssistanceRenderer.RenderAnswer
  -> TextWriter.WriteLine (Console.Out in production)
```

The parsed response fields (`Translation`, `Recommendation`, and `Answer`) are immutable strings.
Coordinators place them in outcomes without interpreting commands, code blocks, scripts, paths, or
suggested file actions. The renderer writes those strings verbatim as display data. It owns no
process, PowerShell, command-dispatch, confirmation, action callback, or filesystem API.

## Static sink searches

The assistance-reachable search used:

```powershell
rg -n -i --glob '*.cs' 'Process\.Start|new\s+ProcessStartInfo|ProcessStartInfo\s*\{|UseShellExecute|powershell(?:\.exe)?|pwsh(?:\.exe)?|System\.Management\.Automation|cmd\.exe|Invoke-Expression|\biex\b|File\.(?:Write|Delete|Move|Copy|Create|Append|Set)|Directory\.(?:Create|Delete|Move)|generated.*(?:execute|run|apply)' src/TerminalTranslator.Core/Assistance src/TerminalTranslator.Cli/Commands/LastCommand.cs src/TerminalTranslator.Cli/Commands/AskCommand.cs src/TerminalTranslator.Cli/Commands/InlineAssistanceRenderer.cs src/TerminalTranslator.Cli/Commands/OnDemandAssistanceRuntimeComposition.cs src/TerminalTranslator.Cli/Providers/ChatCompletionAssistanceProvider.cs src/TerminalTranslator.Cli/Providers/SafeChatCompletionTransport.cs
```

No execution or filesystem-mutation sink was found. The only delegate invocation observed in the
broader assistance search is `contextualExecutorFactory?.Invoke()` in `AskCommand`; it constructs the
`tt ask last` coordinator delegate and does not execute model data.

The repository-wide false-positive search used:

```powershell
rg -n -i --glob '*.cs' --glob '*.ps1' 'Process\.Start|new\s+ProcessStartInfo|UseShellExecute|powershell\.exe|pwsh\.exe|cmd\.exe|System\.Management\.Automation|Start-Process|Invoke-Expression|\biex\b' src
```

It found only:

- Feature 001 Windows Terminal/hosted PowerShell startup in `WindowsTerminalLauncher` and
  `ConPtySession`;
- `Start-Process tt __capture cleanup/watch` in the PowerShell loader. These fixed arguments start
  content-free lifecycle helpers and cannot contain or consume a provider response;
- process `StartTime` reads used to validate capture owner identity.

None is reachable from `AssistanceResult`, and none accepts model-derived arguments.

## Behavioral evidence

Clean project-scoped commands:

```powershell
dotnet test tests/TerminalTranslator.Core.Tests/TerminalTranslator.Core.Tests.csproj --configuration Release --no-restore -p:UseAppHost=false --filter "FullyQualifiedName~LastAssistanceJourneyTests"

dotnet test tests/TerminalTranslator.Cli.Tests/TerminalTranslator.Cli.Tests.csproj --configuration Release --no-restore -p:UseAppHost=false --filter "FullyQualifiedName~InlineAssistanceRendererTests|FullyQualifiedName~StatelessAskAcceptanceTests|FullyQualifiedName~AskLastAcceptanceTests|FullyQualifiedName~OnDemandProviderContractTests|FullyQualifiedName~ProductionCommandCompositionTests"
```

| Project | Passed | Failed | Skipped | Exit code |
|---|---:|---:|---:|---:|
| Core focused suite | 2 | 0 | 0 | 0 |
| CLI focused suite | 17 | 0 | 0 | 0 |
| **Total** | **19** | **0** | **0** | **0** |

Coverage includes provider result parsing, all three coordinators/published composition paths,
`tt last` rendering, `tt ask`, and `tt ask last`. `StatelessAskAcceptanceTests` supplies the
synthetic model answer `Second independent answer: Remove-Item sample` and asserts the exact string
is present in the captured writer. No execution facility is present in the fixture or production
path; the command-shaped text remains display-only. Provider contract tests likewise carry
PowerShell-shaped recommendation text as result data.

For transparency, an initial solution-scoped filtered run matched the same 19 tests and all 19
passed, but Testing.Platform returned exit code 8 because the unrelated Windows test assembly had
zero matching tests. The two project-scoped commands above remove that empty-assembly ambiguity.
An earlier attempt with unsupported legacy `--logger` syntax executed zero tests and is not counted.

## Authorization semantics

`MatchingConsentAssertion` and `AuthorizedAssistanceRequest` authorize only a precisely scoped
external transmission after the exact selected payload passes `AssistancePrivacyGate`. They are
inputs to `IAssistanceProvider.CompleteAsync`; they do not contain an execution permission, command
confirmation, executable callback, or action interface.

Therefore:

```text
matching provider consent
  + exact privacy pass
  + provider success
  = authority to transmit and display the response
  != authority to execute, confirm, apply, or mutate anything from the response
```

Capture permission is separately disclosed as not being provider consent, and provider consent is
not command-execution consent. AI-produced recommendations, shell/PowerShell commands, code blocks,
scripts, and suggested file actions remain untrusted display strings.

## Conclusion

**Gate F = PASS.** Every provider/model response reachable from `tt last`, `tt ask`, and
`tt ask last` terminates at `TextWriter`/console output. No model-response data flow reaches process,
PowerShell, shell dispatch, confirmation, filesystem mutation, or generated-script execution APIs.
