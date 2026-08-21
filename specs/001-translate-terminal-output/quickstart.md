# Quickstart Validation Guide

This guide is intended for the implemented phase-one feature. It validates behavior rather than
serving as implementation code.

## Prerequisites

- Windows 11 x64.
- Windows Terminal with the `wt.exe` execution alias enabled.
- Windows PowerShell 5.1 available as `powershell.exe`.
- .NET 10 SDK at the latest serviced patch.
- Git and Python available for representative CLI tests.
- A test account and key for a chat-completion-compatible HTTPS translation endpoint. Do not use
  production credentials or sensitive terminal content.

The current development machine must show a .NET 10 SDK under:

```powershell
dotnet --list-sdks
```

## Build and Automated Validation

From the repository root:

```powershell
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
```

Expected:

- Unit and contract tests run without a network connection or live provider.
- Core tests cover VT chunk boundaries, classification, secret detection, queue limits, cancellation,
  ordering, and generation rejection.
- Provider tests use a fake HttpMessageHandler and confirm zero calls before consent or for suspected
  secrets.
- Windows integration tests cover ConPTY byte/render equivalence, Ctrl+C, resize, Unicode, companion
  disconnect, and child exit-code propagation.

## Publish the Local Executable

```powershell
./scripts/publish.ps1
$env:Path = "$PWD\artifacts\publish\win-x64;$env:Path"
```

Expected: `artifacts/publish/win-x64/tt.exe` exists and `tt --help` lists `configure`, `start`, `on`,
`off`, and `status`.

## Configure a Test Provider

Set a credential only in the current process environment:

```powershell
$env:TT_PROVIDER_TEST_KEY = "<test-key>"
tt configure --endpoint "https://provider.example/v1/chat/completions" --model "test-model" --api-key-env "TT_PROVIDER_TEST_KEY" --timeout-ms 1500
```

Replace the endpoint and model with the test provider values. Expected:

- This command explicitly selects a 1.5-second test timeout. Omitting `--timeout-ms` uses the
  current production default of 10 seconds.
- Output shows endpoint host, model, environment-variable name, and settings path.
- Output and settings never contain the value of `TT_PROVIDER_TEST_KEY`.
- An HTTP endpoint is rejected unless it is loopback and the explicit test-only
  `--allow-loopback-http` option is supplied.

## Start the Dedicated Session

```powershell
tt start --working-directory "$PWD"
```

Expected:

- A new Windows Terminal window contains exactly two panes.
- The program pane is focused and runs an interactive Windows PowerShell session.
- The companion pane shows `translation: disabled`.
- No provider request occurs during session start.

In the program pane:

```powershell
tt status
```

Expected: active session, disabled translation, configured provider host/model, and zero or bounded
queue counts. No terminal text or key value is displayed.

## Consent and Basic Translation

In the program pane:

```powershell
tt on
```

Expected:

- The program pane identifies the external host, model, current-session scope, non-persistence, and
  whole-segment secret skipping.
- Empty or negative confirmation leaves translation disabled and causes zero provider requests.
- An affirmative confirmation enables only this session and current provider fingerprint.

After affirmative consent:

```powershell
Write-Output "The operation completed successfully."
Write-Error "The requested file could not be found."
git definitely-not-a-command
```

Expected:

- Original text, ordering, color, cursor behavior, and PowerShell error behavior remain in the
  program pane.
- Associated Simplified Chinese translations appear only in the companion pane.
- Commands, paths, and technical tokens remain recognizable.
- Low-value version/path-only output does not create unnecessary translation.

## Interactive Fidelity

Run:

```powershell
python -c "answer=input('Continue with the operation? [y/N] '); print('Selected:', answer)"
```

Enter `n`.

Expected:

- The prompt translation appears in the companion pane within the performance target.
- The `n` reaches Python, never the companion.
- The program pane prints `Selected: n` and exits normally.

Also validate representative `codex`, `npm`, and interactive Git commands. Compare terminal
screenshots or a VT test harness against translation-disabled runs for wrapping, resize, alternate
screen, Ctrl+C, paste, Unicode/IME input, and exit codes.

## Privacy Skip

Use an artificial test value, never a real credential:

```powershell
Write-Output "Authorization: Bearer tt_test_secret_1234567890"
```

Expected:

- The entire candidate is not translated.
- The companion shows a generic privacy-skip notice with no source excerpt.
- A provider spy or local stub records zero requests containing the candidate.
- No diagnostic output reproduces the artificial secret.

The automated test suite must also cover assignment forms, JWT-shaped values, private-key blocks,
credential URIs, split/multiline patterns, detector exceptions, and benign identifiers.

## Disable and Late-Result Suppression

The repository's deterministic stub is process-local to the automated test assembly. Validate its
success and failure modes without starting a public network listener:

```powershell
dotnet test tests/TerminalTranslator.Cli.Tests/TerminalTranslator.Cli.Tests.csproj `
  --configuration Release --no-build --no-restore `
  --filter "FullyQualifiedName~StubTranslationServerTests"
```

For the manual two-pane scenario, configure a separately controlled chat-completion-compatible test
provider that can delay a response, then run:

```powershell
Write-Output "This response is intentionally delayed."
tt off
Write-Output "This text must not be translated."
tt status
```

Expected:

- `tt off` completes within 1 second.
- New text after disable is not analyzed or transmitted.
- The delayed result never appears after disable.
- Windows PowerShell continues normally.

Re-run `tt on`; consent is requested again for the new session generation.

## Provider Failure and Overload

The local deterministic stub covers timeout, HTTP 401/403, HTTP 408/429/5xx, redirect, invalid JSON,
empty and oversized responses without public-network access. Run the stub test command above, or
run the complete offline matrix with `./scripts/validate.ps1`. For manual pane observations, use a
separately controlled test endpoint; do not use production credentials or sensitive terminal text.

Expected:

- Companion status uses only normalized error codes.
- Original program input, output, and exit status remain unchanged.
- There are no automatic retries.

Then emit more than 64 eligible candidates rapidly.

Expected:

- Retained candidate text never exceeds 256 KiB.
- Program output never waits for translation.
- High-priority prompts/errors are preserved according to the queue contract.
- Drop notices are aggregated to at most one per 5 seconds and contain no source text.

## Companion and Session Failure

Close only the companion pane, then continue using the program pane.

Expected: Windows PowerShell continues; translation becomes unavailable without blocking output.

Exit Windows PowerShell normally, then repeat with forced host/child termination.

Expected:

- The original child exit code is preserved when available.
- Final ConPTY output drains before handle closure.
- In-flight provider requests are canceled and late results are rejected.
- No source or translation history file exists after either normal or abnormal termination.
- The local settings directory contains only non-sensitive provider preferences.

## Acceptance Mapping

| Validation area | Requirements / outcomes |
|-----------------|-------------------------|
| Basic translation | FR-002 through FR-005, SC-001, SC-002, SC-007 |
| Interactive fidelity | FR-004, FR-006 through FR-008, SC-003 |
| Enable, consent, disable | FR-009 through FR-011, FR-017, SC-005, SC-008 |
| Secret skipping | FR-012, SC-010 |
| Failure and overload | FR-013, FR-014, SC-004 |
| Dedicated panes | FR-001, FR-015, SC-006 |
| Transient lifecycle | FR-018, SC-009 |
