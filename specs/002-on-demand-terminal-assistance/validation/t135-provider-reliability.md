# T135 — Assistance Provider Reliability

> Status: **accepted / complete**. Automated gates and real Windows PowerShell 5.1 human acceptance
> passed with follow-up artifact SHA256
> `75B0A96949C7E285E4194F57CDB8730DA64756C21AD326745DBE3DD2B988AB10`.

## Root cause and failure boundary

The capture and provider paths were not one failure domain. Both field invocations reached the
assistance failure renderer, but the old coordinator retained only two provider outcomes:
`Timeout` and every other `TranslationProviderException`. Consequently the first historical
`request failed` event cannot be assigned retrospectively to its exact network, HTTP,
authentication/rate-limit, or response-parse exception. That loss of classification and the lack of
content-free request diagnostics is the confirmed code defect.

The seven stages are now evidenced as follows:

1. Previous-command capture: the transcript envelope parser retains everything after the submitted
   command echo and before the structural footer. The T135 regression round-trips `T134 final smoke.`
   unchanged. It also round-trips the complete supplied stderr presentation, including `stderr
   smoke`, source location, reproduced command, `CategoryInfo`, `RemoteException`, and
   `FullyQualifiedErrorId : NativeCommandError`.
2. Candidate extraction/normalization: strict retrieval returns the retained output unchanged. The
   AI selector sends both small smoke cases in full (`Complete`), with no HEAD/TAIL reduction.
3. Request construction: `ChatCompletionAssistanceProvider` serializes `SelectedOutput`, command
   context, and native exit facts. Existing provider contract tests inspect this wire body; T135 adds
   the exact capture-to-selection regression.
4. HTTP/provider request: `SafeChatCompletionTransport` is the only network leaf. It records status
   and response-header/completion timings without body or request content.
5. Timeout/cancellation: the ten-second linked transport `CancelAfter` begins immediately before
   `HttpClient.SendAsync`. Production `HttpClient.Timeout` is infinite. Caller cancellation is
   propagated and labeled separately; no session/disposal token exists in the one-shot `tt last`
   composition.
6. Response parsing: outer chat-completion JSON and inner assistance JSON failures are classified as
   malformed response rather than network/HTTP failure.
7. Rendering: timeout, network, HTTP, and invalid-response outcomes have distinct short messages;
   the legacy generic message remains only for unclassified/local provider errors.

## Original messages

- `[tt] Assistance provider request failed.` was emitted by the renderer for
  `AssistanceFailureKind.ProviderError`. Before T135 this included all non-timeout
  `TranslationProviderException` codes and therefore did not preserve the true exception type.
- `[tt] Assistance provider timed out.` was emitted only after
  `SafeChatCompletionTransport` caught `OperationCanceledException` while the caller token was not
  canceled and converted it to `TranslationErrorCode.Timeout`. In the production path the effective
  ten-second source was the linked transport `CancellationTokenSource.CancelAfter` (the default
  `HttpClient.Timeout` was longer but implicit). T135 removes that second implicit timeout authority.

## DeepSeek evidence

The installed configuration inspected without reading or printing the credential value was:

- endpoint: `https://api.deepseek.com/chat/completions`
- model: `deepseek-v4-flash`
- timeout: 10 seconds (default)
- credential environment variable present: yes

A content-safe synthetic probe received response headers after 334 ms and completed after 6,676 ms
with HTTP 200, valid outer JSON, and valid inner assistance JSON. A following probe did not complete
within the probe runner's 30-second observation boundary. This proves substantial endpoint latency
variation, but it does not prove that the original generic failure was retryable. Automated tests do
not use DeepSeek.

## Reliability and UX decision

No retry was added. Every deterministic failure test asserts exactly one HTTP attempt. In particular,
timeout retry would risk duplicate billable work after an ambiguous POST completion and would either
extend total latency or split a budget that a valid 6.676-second response already consumes.

Normal messages remain brief:

- timeout: `[tt] Assistance provider timed out.`
- network: `[tt] Assistance provider network request failed.`
- HTTP: `[tt] Assistance provider returned an HTTP error.`
- malformed response: `[tt] Assistance provider returned an invalid response.`

Setting `TT_PROVIDER_DIAGNOSTICS=1` emits one JSON diagnostic line to stderr per actual assistance
request. It contains endpoint/model, UTC start, status, response-header/completion milliseconds,
exception type, cancellation reason, and timeout source. It excludes captured/question text,
response bodies, authorization headers, API keys, and credential values, and is not persisted by TT.

## T134 isolation

No T134 PowerShell profile, PSReadLine classifier, native application-I/O mode, console-handle, or
TTY-sensitive application code was changed. T135 changes only provider exception metadata,
assistance mapping/rendering, HTTP timeout composition, tests, and documentation.

## Installed acceptance blocker — 2026-09-02

Blocked artifact:

- artifact: `t135-provider-reliability-20260902-203130`
- SHA256: `D96FFA7A1DEF2DD400524FFED88856B162578BF4E8AF79CBDD3A98E2E0F04AEA`
- publish automation: CLI 245/245, Core 148/148, Windows regression 37/37, installed native stress PASS
- acceptance result: not accepted

The failing recipe ran `$LASTEXITCODE` as a separate PowerShell command before `tt last`. The real
committed generation was inspected with content hashes and metadata rather than inferred from the
renderer:

| Sequence | Structural result |
|---|---|
| 11 | Native stdout: 48 exact UTF-8 bytes, 43 ASCII letters, no unexpected controls, native exit 5 |
| 12 | `$LASTEXITCODE`: one byte, exact value `5`, managed success |
| 13 | contextual `tt last` |
| 15 | Native stderr/error record: 827 bytes, 446 ASCII letters, no unexpected controls, native exit 7 |
| 16 | `$LASTEXITCODE`: one byte, exact value `7`, managed success |
| 17 | contextual `tt last` |

All manifest content hashes matched. The English native records existed after envelope extraction and
normalization. Strict selection correctly chose sequence 12/16 because contextual self-exclusion
skips `tt last` records, not ordinary intervening commands. Eligibility then correctly rejected the
numeric output. There was no provider call and no provider diagnostic.

This also explains the apparent T134/T135 difference: the original T134 field reproduction executed
the native command immediately followed by `tt last`; the T135 acceptance instructions accidentally
inserted a new ordinary command between them. T135 coordinator failure mapping did not cause the
result, and the eligibility implementation was unchanged.

The original T135 round-trip test constructed/finalized one transcript and immediately selected that
record. It correctly covered envelope extraction but did not model the later, separate
`$LASTEXITCODE` history entry. Follow-up coverage now includes both strict sequence semantics and an
installed-loader ConPTY path for direct native stdout, native stderr, managed English, and numeric
noise.

With `TT_PROVIDER_DIAGNOSTICS=1`, the follow-up build also emits `[tt:pipeline]` aggregate-only lines
before provider dispatch. They include retrieval kind, UTF-8 byte count, ASCII-letter/control counts,
eligibility, and selection lengths, but no command/output/provider content or credentials.

## Accepted follow-up artifact

- artifact: `t135-provider-reliability-followup-20260902-205452`
- published executable SHA256: `75B0A96949C7E285E4194F57CDB8730DA64756C21AD326745DBE3DD2B988AB10`
- installed executable SHA256: `75B0A96949C7E285E4194F57CDB8730DA64756C21AD326745DBE3DD2B988AB10`
- installed target: `%LOCALAPPDATA%\TerminalTranslator\Versions\75b0a96949c7e285e4194f57cdb8730da64756c21ad326745dbe3dd2b988ab10\tt.exe`
- Release solution: 542 total, 539 passed, 0 failed, 3 environment-gated skipped
- publish CLI: 246/246
- publish Core: 149/149
- publish Windows regression subset: 37/37
- installed-loader suite: native 500-cycle stress PASS; T135 direct stdout/stderr/managed/noise boundary PASS
- installed Codex TTY gate: 1/1 PASS against this installed hash
- build/publish: zero warnings, zero errors, PASS

Human acceptance passed in a newly opened Windows PowerShell 5.1 session against this exact hash:

- native stdout: retrieval succeeded, English eligibility passed, selection was supported, and the
  provider was reached; both a correctly classified malformed response and a later success were
  observed;
- native stderr: retrieval succeeded, English eligibility passed, selection was supported, and the
  provider was reached; timeout classification reported `cancellationReason=provider-timeout` and
  `timeoutSource=transport-cancel-after`;
- managed `Write-Output`: provider success and correct assistance output;
- numeric noise: English eligibility was false and the provider was not invoked; and
- diagnostics exposed no captured content, API key, Authorization header, or provider response body.

The acceptance commands invoked `tt last` immediately after each command under test; printing
`$LASTEXITCODE` first intentionally changes the strict previous command. T135 is accepted and
complete.
