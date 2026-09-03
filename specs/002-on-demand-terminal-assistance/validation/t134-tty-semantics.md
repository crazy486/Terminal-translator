# T134 TTY semantics validation

Date: 2026-09-01
Platform: Windows 11, Windows Terminal/ConPTY, Windows PowerShell 5.1.26100.9168, PSReadLine 2.0.0

## Outcome

PASS. TTY-sensitive native applications inherit genuine console stdin/stdout/stderr handles, while
ordinary capture-safe native commands retain T133's joined application-I/O reader and reliable
retained output. Provider configuration and strict previous-command selection semantics were not
changed.

## Root cause

T133 enabled `ConsoleVisibility.AlwaysCaptureApplicationIO` for the entire transcript interval.
PowerShell therefore created native children with captured stdout/stderr pipes before an interactive
application could inspect its handles. Codex correctly reported stdin as a terminal but stdout and
stderr as non-terminals. Changing the property manually at the prompt was ineffective because the
managed command lifecycle selected the mode again before process creation.

## Implemented architecture

The profile now composes PSReadLine's existing `AddToHistoryHandler`. PSReadLine calls this hook after
accepting the command line and before `ReadLine` returns it to PowerShell for execution, which is the
deterministic selection point before native process creation.

The selector itself is a small in-process managed delegate compiled when the profile loads. This is
intentional: both changing Enter to `ValidateAndAcceptLine` and invoking a PowerShell scriptblock from
the accepted-history callback caused the existing single-Ctrl+C regression to fail. The managed
delegate does not replace Enter, validation bindings, or the prior history handler.

The delegate parses the accepted PowerShell AST and selects one of two modes:

- direct capture-safe native commands enable T133's joined stdout/stderr reader;
- known TUI/pager/remote-shell/interactive-runtime commands, conditional runtimes without explicit
  non-interactive arguments, and indirect/unknown invocations preserve real console handles.

`codex` is permanently classified TTY-sensitive. Additional names can be supplied through
`TT_TTY_SENSITIVE_APPLICATIONS`. `cmd /c`, PowerShell command/file modes, and interpreter eval modes
remain capture-safe. A TTY-sensitive command anywhere in the accepted AST wins over capture-safe
commands.

PSReadLine 2.0 invokes its history handler during the one-time history-file load. The delegate reads
PSReadLine's initialization state and skips those callbacks. It decodes the last persisted command
once and reapplies the last accepted classification after transcript rotation, preserving correctness
when PSReadLine suppresses the callback for an immediately repeated duplicate command. Prompt-time
diagnostics publish content-free classification booleans only.

## Targeted validation

| Gate | Result |
|---|---|
| Profile parser | PASS |
| Permanent `tty` probe through a `codex` command shim | PASS: stdin/stdout/stderr all `true` |
| Existing single-Ctrl+C PowerShell/native interruption and recovery | PASS; six consecutive managed-selector runs |
| Ordinary native matrix | PASS: stdout, stderr, exit 0/5/7, long output, and three identical consecutive commands |
| Existing interactive first/reentrant prompt probe | PASS |
| Loader generation/contracts | PASS: 5/5 |
| Real installed `codex doctor --json` | PASS: stdin/stdout/stderr terminal fields all `true` |
| Real installed `codex` TUI | PASS: `OpenAI Codex` rendered, no `stdout is not a terminal`, Ctrl+C returned to PowerShell |
| Installed provider-free native acceptance | PASS: redirection, stdout/stderr, nonzero exits, retained records, and transitions |

The installed provider-free acceptance proves that the command
`cmd /d /c "echo T134_NATIVE & exit 5"`-class path produces a retained record containing ordinary
native output and exit 5. CLI integration tests separately prove that `tt last` consumes the strict
retained record through the unchanged provider/selection seam; no live provider transmission was
needed for this validation.

Native redirection remained file-only: the installed redirection case wrote the marker to its target
file, excluded PowerShell `Out-File` plumbing from retained output, and retained correct command/exit
metadata.

## Final bounded stress

The exact installed managed-selector artifact completed one final bounded 100-cycle provider-free
stress run:

- result: PASS in 1m43s;
- diagnostic: `D:\Projects\Terminal Translator\artifacts\diagnostics\t133-capture-stress-20260901-121230-f67d6c40ce58456b8523404dcc5c6903.jsonl`;
- 105 accepted-command classifications;
- 103 joined-reader choices and 2 console-preserving/non-native choices;
- 106 completion observations and 106 boundary starts;
- 0 timeout/failed/unstable stages.

No further long stress run was performed.

## Regression suite

- Core: 147 passed.
- CLI: 237 passed, including production `tt last` composition/retrieval coverage.
- Windows main serialized gate: 142 passed, 2 installed-product tests skipped by their explicit
  environment gates because they were run separately above.
- One unrelated scheduler-sensitive cancellation-detachment test was run as its own serialized gate:
  1 passed. In combined project ordering it can miss its 12-17 ms timing assertion; it is unrelated
  to PowerShell capture and was not changed by T134.
- Aggregate: 527 passed, 2 explicitly gated/skipped, 0 unresolved T134 failures.

## Immutable artifact identity

- Artifact: `D:\Projects\Terminal Translator\artifacts\publish\t134-tty-managed-selector-20260901\tt.exe`
- Size: 74,730,137 bytes
- SHA-256: `58F9C76682FABCE014AA013B5367B15675E937F2F1C7B381A496074EE0DC476F`
- Installed executable: `C:\Users\gutia\AppData\Local\TerminalTranslator\Versions\58f9c76682fabce014aa013b5367b15675e937f2f1c7b381a496074ee0dc476f\tt.exe`
- Installed size/hash: exact match
- Installed PowerShell loader: exact repository profile with only `__TT_EXECUTABLE_PATH__` replaced
