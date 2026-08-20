# Implementation Plan: Windows Terminal Output Translation

**Branch**: `001-translate-terminal-output` | **Date**: 2026-08-21 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-translate-terminal-output/spec.md`

**Note**: This plan ends after Phase 1 design. Task decomposition is produced separately by
`$speckit-tasks`.

## Summary

Build a Windows-only CLI that starts a dedicated Windows Terminal tab with a program pane and a
translation companion pane. A ConPTY relay hosts Windows PowerShell and forwards its UTF-8/VT output
unchanged to the program pane while offering a non-blocking copy to a bounded analysis pipeline.
Eligible, non-sensitive English segments are sent only after per-session consent through a
provider-neutral translation interface; Chinese results and status events travel over same-user
named pipes to the companion pane. All terminal content is transient.

## Technical Context

**Language/Version**: C# 14 on .NET 10 LTS (`net10.0`)

**Primary Dependencies**: .NET shared framework; System.CommandLine 2.0.x; Windows
`CreatePseudoConsole` / `ResizePseudoConsole`; Windows Terminal `wt.exe`; Windows PowerShell;
built-in HttpClient, System.Text.Json, System.Threading.Channels, and named pipes

**Storage**: No terminal-content storage or database. Optional non-sensitive provider preferences
use one local JSON settings file; provider credentials are read from a named environment variable.

**Testing**: MSTest.Sdk 4.x on Microsoft.Testing.Platform; deterministic unit tests, provider
contract tests with fake HttpMessageHandler, same-user IPC tests, and Windows ConPTY integration tests

**Target Platform**: Windows 11 x64 with Windows Terminal and Windows PowerShell 5.1; ConPTY runtime
guard requires Windows 10 version 1809 or newer

**Project Type**: Windows CLI with a platform-specific terminal host and companion process

**Performance Goals**: At least 95% of eligible prompts up to 300 English characters begin showing
translation within 2 seconds when the provider is available; translation adds no blocking wait to
the raw output path; disablement stops new translation work within 1 second

**Constraints**: Original program bytes, layout, cursor behavior, input, and exit code remain
unchanged; no secret-bearing segment is transmitted; no terminal content persists; translation
failures never stop the shell; one self-contained `win-x64` executable; no trimming or Native AOT
in phase one

**Scale/Scope**: One local user, one active translation session, exactly two panes, one PowerShell
process tree, at most 64 queued candidates and 256 KiB retained candidate text; common codex, git,
npm, and python interactions

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

### Pre-Research Gate

| Principle | Status | Evidence |
|-----------|--------|----------|
| CLI-First | PASS | All public controls and translation display remain inside Windows Terminal. |
| Privacy-First | PASS | Translation starts disabled, requires per-session consent, skips suspected secrets, and persists no terminal content. |
| Minimal Scope | PASS | No GUI, account, database, cloud sync, telemetry, or resident background service is introduced. |
| Provider-Agnostic Design | PASS | Core owns provider-neutral request, result, and error contracts; HTTP mapping stays in an adapter. |
| Safe Execution | PASS | The feature translates text only and never generates, alters, confirms, or executes commands. |
| Maintainability | PASS | Core, Windows integration, CLI composition, and tests have explicit dependency boundaries. |
| Testability | PASS | ConPTY, provider, process, clock, and IPC boundaries are substitutable; core tests require no network or interactive shell. |

The design has no constitution violations. System.CommandLine is the only production package outside
the shared framework and is justified by the public/internal command surface, validation, help, and
parser test requirements.

### Data-Flow and Safety Gate

```text
user input -> program-pane relay -> ConPTY -> Windows PowerShell
                                      |
ConPTY output -> raw byte forward ----+----> original program pane
              -> non-blocking copy -> VT extractor -> secret gate -> classifier
                                   -> bounded queue -> provider adapter
                                   -> same-user event pipe -> companion pane
```

- The raw input/output path never waits for translation, IPC, or provider work.
- The secret gate runs before enqueue and again at the provider serialization boundary.
- Provider credentials remain outside settings and IPC messages.
- Session generation and cancellation reject late results after disable or teardown.
- Diagnostic events contain reason codes and counts only, never source or translated text.

### Post-Design Gate

PASS. The Phase 1 data model makes every content-bearing entity session-scoped; the contracts
separate public CLI, local IPC, and provider boundaries; the quickstart validates consent, secret
skipping, non-persistence, overload degradation, interactive fidelity, and unchanged exit behavior.
No new infrastructure or exception to the constitution is required.

## Project Structure

### Documentation (this feature)

```text
specs/001-translate-terminal-output/
|-- plan.md
|-- research.md
|-- data-model.md
|-- quickstart.md
|-- contracts/
|   |-- cli.md
|   |-- session-ipc.md
|   `-- translation-provider.md
|-- checklists/
|   `-- requirements.md
`-- tasks.md                 # Created later by $speckit-tasks
```

### Source Code (repository root)

```text
TerminalTranslator.sln
src/
|-- TerminalTranslator.Core/
|   |-- Models/
|   |-- Parsing/
|   |-- Privacy/
|   |-- Sessions/
|   `-- Translation/
|-- TerminalTranslator.Windows/
|   |-- ConPty/
|   |-- Console/
|   |-- Ipc/
|   `-- Terminal/
`-- TerminalTranslator.Cli/
    |-- Commands/
    |-- Configuration/
    |-- Providers/
    `-- Program.cs

tests/
|-- TerminalTranslator.Core.Tests/
|   |-- Unit/
|   `-- Contract/
|-- TerminalTranslator.Windows.Tests/
|   `-- Integration/
`-- TerminalTranslator.Cli.Tests/
    |-- Contract/
    `-- Integration/
```

**Structure Decision**: Use three production projects in one solution. `Core` has no Windows or
provider dependency. `Windows` owns ConPTY, console modes, Windows Terminal launch, and named-pipe
security. `Cli` is the executable composition root and contains thin commands, settings, and the
first HTTP provider adapter. This is the smallest structure that enforces the constitution's core,
provider, and shell-integration test boundaries without adding services or a plugin system.
