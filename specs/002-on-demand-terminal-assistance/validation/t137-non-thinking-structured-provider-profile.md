# T137 Non-Thinking Structured Provider Profile

**Date**: 2026-09-02
**Status**: Accepted/completed in real Windows Terminal + Windows PowerShell 5.1

## Production change

Before T137, both Chat Completions adapters serialized `model`, two `messages`, and
`temperature: 0`. They omitted `thinking`, `reasoning_effort`, `max_tokens`, `response_format`, and
`stream`. DeepSeek therefore selected default high-effort thinking, and the assistance adapter relied
only on prompt text to request JSON.

After T137:

- translation serializes `model`, `messages`, `temperature: 0`, and
  `thinking: {"type":"disabled"}`;
- assistance serializes those fields plus
  `response_format: {"type":"json_object"}`;
- `reasoning_effort`, `max_tokens`, and `stream` remain absent.

The shared request DTO owns both typed nested objects. No request JSON is assembled through string
concatenation. Translation and assistance receive the same configured model and use the same
`SafeChatCompletionTransport`. Translation remains a plain-text response contract; all three
assistance request kinds retain their existing explicit JSON prompt instruction and JSON schema
parsing.

## Why both controls changed

T136 isolated the two faults. Disabling thinking removed the observed latency/timeouts, but the
prompt-only JSON contract was malformed in 8 of 10 non-thinking primary calls. API JSON Output
removed malformed output in every tested structured call, while JSON Output with thinking still
timed out in all three medium isolation calls. T137 applies both because each repairs a separately
identified production contract defect, not as an unvalidated sampling optimization.

## Preserved boundaries

- configured timeout remains 10 seconds;
- production assistance `HttpClient.Timeout` remains infinite and transport `CancelAfter` remains
  the timeout authority;
- no retry and no streaming;
- caller cancellation remains distinct;
- `temperature: 0` remains explicit;
- no `max_tokens` value was added;
- outer/inner/empty/missing/finish-length/schema/response-size defensive classifications remain;
- strict-previous, eligibility, numeric/no-English filtering, capture, spinner, TTY, and diagnostics
  privacy paths are unchanged.

## Deterministic tests

- `StructuredProductionRequestProfile_SerializesNonThinkingJsonOutputContract` captures the final HTTP
  request body for LastTranslation, QuestionOnly, and QuestionWithPreviousCommand. Each has exactly
  five top-level fields: `model`, `messages`, `temperature`, `thinking`, and `response_format`; the
  nested values are exactly `disabled` and `json_object`.
- `TranslateAsync_MapsRequestAndResponseAndUsesCredentialHeader` captures the live-translation wire
  body. It has exactly four top-level fields, includes `thinking.type=disabled`, and omits
  `response_format` because this is a plain-text response contract.
- Both tests prove model propagation and `temperature=0`, and prove `reasoning_effort`, `max_tokens`,
  and `stream` remain absent. Existing T135 tests prove each network/HTTP/timeout/malformed failure is
  attempted exactly once, caller cancellation remains distinct, and diagnostics remain content-free.
- Targeted provider profile/reliability/transport regression: 26 passed, 0 failed; final exact-profile
  smoke after expanding coverage to all three assistance kinds: 2 passed, 0 failed.
- Complete Release project matrix: Core 149 passed; CLI 247 passed; Windows 144 passed and 3 explicit
  installed/TTY acceptance tests skipped by their environment gates. Aggregate: 540 passed, 3
  skipped, 0 failed when projects were run without cross-project load.
- Two parallel solution attempts exposed only unrelated pre-existing timing flakes: one Core
  cancellation-observation race and two PowerShell/ConPTY nine-second prompt waits on the first run,
  then one of the same prompt waits on the second. All three passed immediately in isolation, and the
  complete standalone Windows project passed. No terminal timing architecture was changed.
- Release build performed by the artifact publisher succeeded with 0 warnings and 0 errors.

## Live provider smoke

The T136 harness ran only Profile E (`thinking=disabled`, API JSON Output, `max_tokens` omitted), one
request per fixed input:

| Input | HTTP headers | Complete | Result | Reasoning | Timeout/malformed |
|---|---:|---:|---|---|---|
| Short, 48 B | 263 ms | 837 ms | success | absent | none |
| Medium, 900 B | 116 ms | 1,927 ms | success | absent | none |

Both requests used `deepseek-v4-flash`, returned HTTP 200 with `finish_reason=stop`, produced valid
translation/recommendation JSON, and completed far below the former medium current-profile timeout.

## Manual acceptance artifact

- Artifact name: `t137-non-thinking-structured-20260902`
- Artifact executable: `artifacts/publish/t137-non-thinking-structured-20260902/tt.exe`
- Artifact SHA256: `021B8D1EBB68E036BD41F49D32E69787F97EB5472E7AD5163669A51E32400368`
- Installed executable:
  `%LOCALAPPDATA%\TerminalTranslator\Versions\021b8d1ebb68e036bd41f49d32e69787f97eb5472e7ad5163669a51e32400368\tt.exe`
- Installed SHA256: `021B8D1EBB68E036BD41F49D32E69787F97EB5472E7AD5163669A51E32400368`

The published and installed hashes match. Human acceptance used this exact artifact and hash.

## Human acceptance result

Real Windows Terminal + Windows PowerShell 5.1 acceptance passed:

- Native stdout structured `tt last`: provider success, 971 ms response completion, valid
  translation and suggestion.
- Native stderr structured `tt last`: 827-byte selected output, provider success, 1,553 ms response
  completion, valid structured response, and no timeout.
- Managed `Write-Output` structured `tt last`: provider success and 773 ms response completion.
- Numeric-only output: `englishEligible=false`; provider was not invoked.
- Live translation: `tt on` enabled translation, English output was translated correctly in the
  companion pane, `tt off` stopped translation, and re-enabling worked normally.
- No JSON leakage, provider timeout, TTY regression, or session regression was observed.

The accepted commands were run with no intervening `$LASTEXITCODE` command between each tested
command and `tt last`:

```powershell
$env:TT_PROVIDER_DIAGNOSTICS = '1'

cmd /d /c "echo The package installation completed successfully. & exit 5"
tt last

cmd /d /c "echo The application failed because the configuration file is missing. 1>&2 & exit 7"
tt last

Write-Output "The package installation completed successfully."
tt last

Write-Output 12345
tt last

Remove-Item Env:TT_PROVIDER_DIAGNOSTICS
```

The installed result confirms the T136/T137 conclusion: default high-effort thinking was the primary
provider latency/timeout source, while explicit non-thinking plus API JSON Output fixes the
structured assistance path.

## Non-blocking observation

One live translation of the short status text `Translation enabled.` was interpreted as an
instruction instead of translated literally. This is recorded as a possible future prompt/translation
quality follow-up only. It did not block T137 acceptance and no production behavior was changed in
this closeout.
