# T138 Live / Last Architecture Convergence Audit

**Date:** 2026-09-03
**Branch:** `003-live-architecture-hardening`
**Baseline:** `phase2-tt-last-baseline` / `bef8f7a52e7e1013771eff271fd8d820a30cce00`
**Status:** Accepted/completed in real Windows Terminal + Windows PowerShell 5.1

T135, T136, and T137 evidence and acceptance hashes were treated as immutable inputs. This audit did
not modify those tasks or reopen their accepted state.

## Audit methodology

The audit started at the production command composition roots and followed concrete calls, tokens,
ownership, serialization, and disposal. Tests were used only after reading the implementations. The
review covered `StartCommand`/`HostCommand` and `CommandFactory`/`OnDemandAssistanceRuntimeComposition`,
then the Core, Windows, provider, IPC, capture, and renderer layers reached from those roots.

## Current architecture

### Live production pipeline

1. `CommandFactory` (`src/TerminalTranslator.Cli/Commands/CommandFactory.cs`) routes `tt start` to
   `StartCommand`.
2. `StartCommand` (`src/TerminalTranslator.Cli/Commands/StartCommand.cs`) loads the shared
   `ProviderSettingsStore`, verifies the credential source, creates the session id/nonce, and asks
   `WindowsTerminalLauncher` to create the two-pane session. Translation starts disabled.
3. `WindowsTerminalLauncher` (`src/TerminalTranslator.Windows/Terminal/WindowsTerminalLauncher.cs`)
   launches `__host` in the program pane and `__companion` in the translation pane, and propagates
   the authenticated session environment and hosted-session guard.
4. `HostCommand` (`src/TerminalTranslator.Cli/Commands/HostCommand.cs`) authenticates the host,
   reloads the same provider settings, creates the long-lived production `HttpClient`,
   `TranslationSession`, event/control pipe servers, provider, pipeline, and ConPTY runtime. It owns
   and disposes a client it created; an injected client remains caller-owned.
5. `ConPtySession` (`src/TerminalTranslator.Windows/ConPty/ConPtySession.cs`) owns the real
   PowerShell process, pseudoconsole, and input/output pipe handles. `ConsoleInputRelay` preserves
   interactive input and feeds `SubmittedCommandTracker`; `PseudoConsoleResizeMonitor` propagates
   terminal size changes.
6. `ConsoleOutputRelay` (`src/TerminalTranslator.Windows/Console/ConsoleOutputRelay.cs`) first writes
   and flushes every raw ConPTY byte to the program pane, then makes a non-blocking side offer to
   analysis only while translation is enabled. Translation failure cannot block or mutate the
   primary terminal stream.
7. `ProductionTranslationPipeline`
   (`src/TerminalTranslator.Cli/Commands/ProductionTranslationPipeline.cs`) owns the bounded raw
   analysis channel, `VtTextExtractor`, discontinuity handling, idle flush, session-generation
   capture, PowerShell prompt/echo/input-ownership filtering through `SubmittedCommandTracker`, and
   segment grouping.
8. `EnglishCandidateClassifier` (`src/TerminalTranslator.Core/Parsing/EnglishCandidateClassifier.cs`)
   applies live segment-level eligibility and priority. `SecretDetector`
   (`src/TerminalTranslator.Core/Privacy/SecretDetector.cs`) performs the pre-enqueue privacy gate.
9. `TranslationWorkQueue` (`src/TerminalTranslator.Core/Translation/TranslationWorkQueue.cs`) owns
   high/normal bounds, total UTF-8 text budget, redraw replacement, priority, and 1.5-second expiry.
   `TranslationWorker` drains it serially, isolates provider failures, clears pending work on disable,
   and remains alive for later translations.
10. `TranslationCoordinator` (`src/TerminalTranslator.Core/Translation/TranslationCoordinator.cs`)
    repeats the privacy check defensively, verifies generation/fingerprint authorization, links the
    worker/shutdown token with `TranslationSession.TranslationCancellationToken`, calls the provider,
    validates the result, rechecks authorization, tracks the translation, and suppresses stale
    publication.
11. `ChatCompletionTranslationProvider`
    (`src/TerminalTranslator.Cli/Providers/ChatCompletionTranslationProvider.cs`) performs the final
    authorization and secret checks immediately before DTO creation/serialization, requests plain
    translated text, records content-free provider diagnostics, and applies the live 16 KiB content
    bound.
12. `SafeChatCompletionTransport`
    (`src/TerminalTranslator.Cli/Providers/SafeChatCompletionTransport.cs`) resolves the credential,
    serializes with `ProviderJsonContext`, adds the per-request Authorization header, owns the linked
    `CancelAfter` deadline, uses `ResponseHeadersRead`, maps network/HTTP/timeout failures, reads at
    most 32 KiB, and parses the outer chat-completion JSON. It never retries.
13. `EventPipeServer` (`src/TerminalTranslator.Windows/Ipc/EventPipeServer.cs`) accepts only the
    authenticated companion, queues bounded event messages, applies a 250 ms write deadline, and
    drops/disconnects safely rather than blocking translation or the program pane.
14. `CompanionCommand` and `CompanionRenderer`
    (`src/TerminalTranslator.Cli/Commands/CompanionCommand.cs`, `CompanionRenderer.cs`) read translation,
    state, and aggregated status events and render them only in the companion pane.

Cancellation enters at the `System.CommandLine` action token. `HostCommand.runtimeCancellation`
links it to host IPC/output lifetime; `interactiveCancellation` additionally owns input and resize.
`tt off` increments `TranslationSession.Generation`, cancels its current translation CTS, and asks
`TranslationWorker` to clear queued work. Normal exit and fault teardown call
`TranslationSession.BeginStopping`, which increments generation and cancels in-flight translation
before raw-output drain and final state publication. Pipeline disposal cancels the analysis worker
and translation worker; transport observes their linked token.

### Last production pipeline

1. The installed `TerminalTranslator.Profile.ps1`
   (`src/TerminalTranslator.Windows/PowerShell/TerminalTranslator.Profile.ps1`) integrates with a
   normal, non-hosted Windows PowerShell session. Its prompt boundary logic completes transcript
   producer work, captures command/termination facts, and invokes the hidden capture bridge. The
   hosted live ConPTY guard prevents this integration from installing a second prompt/transcript in
   `tt start`.
2. `CaptureIntegrationCommand` (`src/TerminalTranslator.Cli/Commands/CaptureIntegrationCommand.cs`)
   routes initialize/boundary/recovery/cleanup operations.
3. `CaptureBoundaryProcessor`, `CapturedCommandFinalizer`, and `RetainedCaptureStore`
   (`src/TerminalTranslator.Windows/Capture/`) validate owner/session proof and transcript envelopes,
   finalize completed commands, enforce bounded retention, and atomically publish the committed
   generation.
4. Before normal command parsing, `CommandFactory` asks `ContextualInvocationMarker`
   (`src/TerminalTranslator.Windows/Capture/ContextualInvocationMarker.cs`) to mark `tt last` as a
   contextual invocation so it cannot later become the selected user command.
5. `LastCommand` (`src/TerminalTranslator.Cli/Commands/LastCommand.cs`) starts the one-shot operation
   with the command action cancellation token and supplies the delayed spinner only around provider
   activity.
6. `OnDemandAssistanceRuntimeComposition.CreateProduction`
   (`src/TerminalTranslator.Cli/Commands/OnDemandAssistanceRuntimeComposition.cs`) owns the one-shot
   composition: shared settings store, process-lifetime `HttpClient`, safe diagnostic sinks,
   `SecretDetector`, `AssistancePrivacyGate`, consent-grant store, and lazy contextual retriever.
7. `ProductionPreviousCommandRetriever`
   (`src/TerminalTranslator.Cli/Commands/ProductionPreviousCommandRetriever.cs`) validates capture
   preference, session/nonce, direct parent owner, owner manifest, health, and committed records.
   `PreviousCommandRetriever` plus `ContextualCommandSelection.SelectStrictTarget`
   (`src/TerminalTranslator.Core/Capture/`) select exactly the strict previous non-contextual
   completed command.
8. `LastAssistanceCoordinator` (`src/TerminalTranslator.Core/Assistance/LastAssistanceCoordinator.cs`)
   maps capture outcomes, applies `WholeOutputEligibility`, builds the exact last-translation request,
   and calls `AiRequestSelector`/`AiInputBudgetPolicy` for bounded complete or ordered head/tail input.
9. `AssistanceConsentPrompt` and `ProviderConsentGrantStore`
   (`src/TerminalTranslator.Cli/Commands/AssistanceConsentPrompt.cs`,
   `src/TerminalTranslator.Cli/Configuration/ProviderConsentGrantStore.cs`) authorize the disclosed
   assistance scope against the normalized provider consent fingerprint.
10. `AssistancePrivacyGate` (`src/TerminalTranslator.Core/Assistance/AssistancePrivacyGate.cs`) screens
    the exact serialized variable payload. `AuthorizedAssistanceRequest.AssertValid` recomputes its
    payload fingerprint at the provider boundary, preventing post-authorization mutation.
11. `ChatCompletionAssistanceProvider`
    (`src/TerminalTranslator.Cli/Providers/ChatCompletionAssistanceProvider.cs`) creates the structured
    request, calls the same `SafeChatCompletionTransport`, records content-free diagnostics, rejects
    finish-length/missing/empty content, parses the inner assistance JSON schema, and applies its
    16 KiB result reserve.
12. `InlineAssistanceRenderer` (`src/TerminalTranslator.Cli/Commands/InlineAssistanceRenderer.cs`)
    writes the one-shot translation/recommendation or assistance-specific error to the current shell.

The `LastCommand` action token flows through retrieval, file reads, authorization/consent, spinner,
provider, shared transport, and response handling. There is no live session generation or detached
queue because the caller synchronously awaits this one-shot operation.

## Convergence matrix

| Concern | Live | Last | Current state | Recommended |
|---|---|---|---|---|
| Capture | Continuous ConPTY byte stream | Bounded completed-command transcript retention | Intentional separation | Keep separate ownership and stores |
| Session ownership | Authenticated host session id/nonce and `TranslationSession` | Normal shell capture owner proof/session | Semantically different | Keep separate |
| English eligibility/classification | Segment/block `EnglishCandidateClassifier` | Whole-output `WholeOutputEligibility`, then AI selection | Intentional separation | Preserve product semantics |
| Privacy / `SecretDetector` | Pre-enqueue, coordinator defense, provider-boundary check | Exact selected payload screened by `AssistancePrivacyGate` | Shared primitive; semantically different placement | Keep fail-closed gates |
| Consent / authorization | Disabled by default; `tt on`; session generation + provider fingerprint | Explicit invocation; persisted provider/scope/policy consent | Intentional separation | Do not unify UX |
| Provider settings | `ProviderSettings` | `ProviderSettings` | Shared | Retain |
| Endpoint | `settings.Endpoint` | `settings.Endpoint` | Shared | Retain |
| Model | `settings.Model` | `settings.Model` | Shared | Retain |
| API key lookup | `settings.ApiKeyEnvironmentVariable`, resolved per transport request | Same | Shared | Retain; never persist value |
| Provider fingerprint | Live endpoint/model/target fingerprint | Consent destination/model/credential source/scope/policy fingerprint | Semantically different | Keep separate authorization scopes |
| Settings persistence | `ProviderSettingsStore` | `ProviderSettingsStore` | Shared | Retain source of truth |
| Endpoint/loopback policy | `ProviderSettings.Create` validation | Same | Shared | Retain HTTPS/explicit loopback test policy |
| Source/target language | Fixed validated `en` / `zh-Hans`; live request carries both | Same settings; assistance prompt contract targets Simplified Chinese | Shared config, different contract expression | No wrapper needed |
| `HttpClient` construction | Long-lived host client; redirects/cookies off; timeout infinite after T138 | One-shot composition client; same policies | Converged; lifecycle intentionally differs | Keep policies aligned |
| Redirect policy | `AllowAutoRedirect=false` | `AllowAutoRedirect=false` | Shared policy, duplicated composition syntax | Retain; no factory justified yet |
| Cookie policy | `UseCookies=false`; transport removes caller Cookie headers | Same | Shared policy | Retain |
| Transport | `SafeChatCompletionTransport` | `SafeChatCompletionTransport` | Shared | Do not create another transport |
| Timeout authority | Transport linked CTS + `CancelAfter`; production client infinite | Same | Shared after T138 reliability fix | Keep transport as sole configured deadline |
| Caller cancellation | Propagates as `OperationCanceledException`; diagnostic says caller cancellation | Same | Shared | Retain distinct from timeout |
| Session-generation cancellation | Session CTS linked in coordinator; stale result suppressed | Not applicable | Live-specific | Retain |
| Provider error taxonomy | `TranslationErrorCode` + `TranslationProviderFailureDetails` | Same | Shared | Retain |
| Diagnostics | Generic safe `[tt:provider]` after T138; live UX status remains separate | Generic safe `[tt:provider]` plus last-only `[tt:pipeline]` | Shared provider diagnostics; intentional pipeline split | Do not add last pipeline diagnostics to live |
| Request DTO | `ChatCompletionRequestDto` | Same | Shared | Retain typed DTO |
| JSON serialization | Source-generated `ProviderJsonContext` | Same | Shared | Retain |
| Request profile | model, messages, temperature 0, thinking disabled; plain text | Same plus `response_format=json_object` | Intentional response-contract difference | Never add JSON Output to live |
| Omitted generation fields | no reasoning effort, max tokens, stream | Same | Shared | Retain |
| Transport response bound | 32 KiB outer HTTP body | Same | Shared | Retain |
| Application response bound | 16 KiB translated text; repeated defensively in coordinator | 16 KiB structured result reserve | Semantically different; harmless defense duplication | Keep independent contracts |
| Response parsing | Outer DTO then plain message content | Outer DTO then inner structured JSON/schema | Intentional separation | Never leak assistance schema into live |
| Provider lifecycle | One provider for long-running host | Deferred provider for one CLI invocation | Intentional lifecycle difference | No singleton/DI container |
| `HttpClient` lifecycle | Host disposes only the client it creates | Process-lifetime client in one-shot CLI composition | Harmless implementation difference | Observe; no phase-3 blocker |
| Retry | None | None | Shared | Do not add retry |
| Streaming | Omitted/disabled | Omitted/disabled | Shared | Do not add streaming |
| Queue/scheduling | Bounded priority queue, expiry, redraw replacement | Caller-awaited one-shot + spinner | Intentional separation | Do not cross-wire |
| Stale request prevention | Generation checks before call, after result, before tracked publish | Strict previous selection + authorized exact payload fingerprint | Semantically different | Retain both |
| Rendering | Authenticated companion pane | Current shell inline renderer | Intentional separation | Do not unify |
| Teardown | Session stop/fault, CTS cancellation, pipeline/worker/ConPTY/IPC disposal | Command cancellation/process completion | Semantically different | Retain |
| Privacy guarantees | Suspected secret blocked before enqueue/provider serialization; diagnostics content-free | Exact outbound payload blocked before provider; diagnostics content-free | Shared invariant | Preserve with contract tests |

## Confirmed shared infrastructure

- `ProviderSettings` and `ProviderSettingsStore` are the single provider configuration source for
  both paths. Endpoint, model, credential environment-variable name, timeout, fixed languages, and
  endpoint security validation are not parsed independently by either provider.
- Both adapters use the same `ChatCompletionRequestDto`, source-generated `ProviderJsonContext`,
  credential resolution, Authorization construction, outer response DTO, 32 KiB body reader,
  network/HTTP/timeout mapping, and no-retry behavior in `SafeChatCompletionTransport`.
- Both production HTTP handlers disable redirect following and cookies. After T138 both clients set
  `HttpClient.Timeout = Timeout.InfiniteTimeSpan`.
- Both adapters now use `IProviderRequestDiagnosticSink`,
  `ProviderRequestDiagnosticFactory`, and the opt-in `JsonProviderRequestDiagnosticSink`.
- Both privacy paths use `SecretDetector`, but retain the correct product-specific authorization
  boundary and outbound-payload construction.

## Architecture drift found

### Correctness drift

No current output, capture, selection, request-profile, parsing, rendering, or session-generation
correctness drift was found.

### Reliability drift

Before T138, the live `HostCommand` production client did not override the framework default
`HttpClient.Timeout`. The provider timeout is at most 10 seconds, so the shared transport normally
won the race, but live did not strictly satisfy the T135 single-authority invariant. This was a real
architecture-policy drift, not an observed user timeout regression.

### Diagnostics drift

Before T138, only `ChatCompletionAssistanceProvider` emitted the generic safe provider diagnostic.
Live's `providerErrorSink -> StatusAggregator` exposed aggregated UX state but did not provide HTTP
status, header/completion latency, failure source, exception type, or timeout source. The existing
diagnostic DTO/factory/sink is provider-generic and content-free, so reuse was justified.

### Harmless implementation differences

- Live explicitly owns/disposes its long-running client; the last composition keeps its client for
  the lifetime of the one-shot CLI process. Unifying this with a global singleton or DI container
  would add lifecycle risk without a product benefit.
- Handler construction syntax remains present in two composition roots. With only two sites and
  different lifecycle owners, a new factory was not introduced merely to remove a few lines.
- The live provider and coordinator both enforce the same 16 KiB translated-text ceiling. This is a
  harmless defense at the adapter and publication boundary, not conflicting policy.
- Pre-serialization authorization/privacy rejections do not emit a provider-request diagnostic,
  because no provider request occurred. Existing privacy/status handling remains responsible for
  those local decisions.

## Changes made

1. `src/TerminalTranslator.Cli/Commands/HostCommand.cs`
   - Why: restore the T135 single timeout authority in the real live composition.
   - Before: redirect and cookies were disabled, but `HttpClient.Timeout` retained its independent
     framework default.
   - After: the owned production client explicitly uses `Timeout.InfiniteTimeSpan`; only
     `SafeChatCompletionTransport` applies the configured provider deadline.
2. `src/TerminalTranslator.Cli/Commands/HostCommand.cs` and
   `src/TerminalTranslator.Cli/Providers/ChatCompletionTranslationProvider.cs`
   - Why: converge safe low-level provider instrumentation without changing companion UX.
   - Before: live emitted only aggregated provider status events.
   - After: the host creates the same environment-gated JSON diagnostic sink used by assistance and
     injects it into the live adapter. Success, transport failures, caller cancellation, and
     live-applicable malformed content carry safe metadata. Normal output is unchanged; diagnostics
     are still opt-in and go to host stderr, not the companion event protocol.
3. `tests/TerminalTranslator.Cli.Tests/Contract/TranslationProviderDiagnosticsTests.cs`
   - Adds deterministic live success, transport-timeout-source, caller-cancellation, malformed
     plain-text subtype, no-retry, and content-free diagnostic coverage.

No capture, queue, renderer, ConPTY, assistance schema, timeout value, sampling value, model,
streaming, retry, or token-limit behavior changed.

## Intentionally separate areas

- **Capture:** live owns transient continuous ConPTY bytes; last owns bounded, completed, validated
  command records. Combining these would destroy their different reliability and privacy models.
- **Classification semantics:** live decides whether small terminal segments should enter a latency-
  sensitive queue; last decides whether a complete previous-command output is meaningful and then
  selects bounded AI input. Shared `SecretDetector` does not imply shared eligibility semantics.
- **Queue:** live needs priority, redraw replacement, expiry, overload handling, and generation
  invalidation. Last is a single explicit request awaited by its caller and uses a spinner only while
  the provider runs.
- **Response contract:** live returns plain translated text. Last requires structured translation and
  recommendation JSON. Consequently only assistance sends `response_format=json_object`.
- **Rendering:** live writes authenticated events to the companion pane; last writes an inline result
  to the invoking shell. Their error language and layout remain separate.
- **Interaction lifecycle:** live is disabled-by-default session state controlled by `tt on/off`;
  last is an explicit one-shot invocation with persisted provider/scope consent. These are not
  interchangeable authorization UXs.

## T135 reliability parity

- **Timeout:** shared transport creates a linked CTS and calls `CancelAfter(min(request deadline,
  configured timeout))`. Both production clients are now infinite-timeout. A provider deadline maps
  to `TranslationErrorCode.Timeout`, cancellation reason `provider-timeout`, and timeout source
  `transport-cancel-after`. A handler-originated `TimeoutException` remains safely distinguishable as
  `http-client-or-handler`.
- **Network:** send failures (`HttpRequestException`) and response-stream read failures
  (`HttpRequestException`/`IOException`) map to `Unavailable` with source `Network`.
- **HTTP:** both use the same mapping: 401/403 authentication, 429 rate limit, 408 and 5xx unavailable,
  and other unexpected non-success statuses invalid response; the diagnostic source distinguishes
  authentication/rate-limit/general HTTP status.
- **Caller cancellation:** caller cancellation bypasses the timeout catch filter and propagates as an
  `OperationCanceledException`; it is diagnosed as `caller-cancellation`, never provider timeout.
- **Response size:** both share the 32 KiB transport body limit. Their independent 16 KiB application
  limits match their different plain-text versus structured-result contracts and do not conflict.
- **Retry:** both perform exactly one send. No retry was added.

## T136/T137 serialized request profiles

Existing deterministic HTTP body tests still prove the final wire shape, not merely DTO shape.

- Live has exactly `model`, `messages`, `temperature`, and `thinking`; temperature is `0`, thinking
  type is `disabled`, and `response_format`, `reasoning_effort`, `max_tokens`, and `stream` are absent.
- Assistance has `model`, `messages`, `temperature`, `thinking`, and `response_format`; temperature is
  `0`, thinking type is `disabled`, response format type is `json_object`, and `reasoning_effort`,
  `max_tokens`, and `stream` are absent for all assistance request kinds.

T138 did not change the shared DTO or sampling/generation profile.

## Timeout authority

After the change, **both live and last strictly satisfy single provider timeout authority** in their
production composition. `SafeChatCompletionTransport` is the sole configured provider deadline.
`HttpClient.Timeout` is infinite on both production clients. Worker/session/command cancellation is
an independent caller/lifecycle authority, not a second provider timeout.

## Diagnostics parity

After the change, `TT_PROVIDER_DIAGNOSTICS=1` covers actual provider requests in both live and last
through the same `[tt:provider]` JSON sink. Live records success, network/HTTP/timeout/outer-JSON
failures, caller cancellation, and plain-text missing/empty/oversized content. It does not manufacture
assistance-only `inner-json-invalid` or `schema-invalid` subtypes. `[tt:pipeline]` remains last-only.

The sink contains no fields for source terminal text, normalized segment, command, prompt, question,
translation, response body, API key, Authorization header, or credential value. It strips endpoint
query and fragment. Diagnostics remain disabled by default and are not sent over the companion pipe.

## Live error UX

`SafeChatCompletionTransport`/provider throws `TranslationProviderException`; `TranslationWorker`
catches it and calls `providerErrorSink`; `StatusAggregator` maps and rate-aggregates the code;
`EventPipeServer` sends a status event; `CompanionRenderer` displays
`status: provider-error (<code>)`.

- Timeout: `timeout`.
- Network, DNS, connection, response-stream, 408, or 5xx failure: `unavailable`.
- Authentication: `authentication`.
- Rate limit: `rate-limited`.
- Malformed/invalid provider response: `invalid-response`.
- Normal caller/session-generation cancellation: coordinator/worker cancellation handling suppresses
  publication and does not report provider unavailable or timeout.

The worker catches a provider failure per item and continues its outer loop, so the session remains
usable and a later translation can succeed. Provider failures do not terminate the raw output relay
or write to the program pane. Only explicitly enabled provider diagnostics write safe JSON to host
stderr. Status events contain no source or response content.

## Cancellation / lifecycle

- `tt off` increments the generation, clears consent and retained transient session items, cancels
  `TranslationSession.TranslationCancellationToken`, clears the work queue, and cancels the worker's
  current generation. A cancel-aware transport stops; a cancellation-ignoring provider task is
  detached with a fault-observing continuation and its result cannot publish.
- Queue expiry occurs inside `TranslationWorkQueue` before dequeue; expired items never call the
  provider.
- `TranslationCoordinator` validates authorization before the provider, after provider completion,
  and again through `TryTrackTranslation` before publication. The event write also receives the
  linked generation token. Old generations cannot publish after disable/re-enable.
- On normal shell/ConPTY exit, `SessionTeardown.BeginStopping` cancels generation work before final
  raw drain. Pipeline disposal then cancels and awaits its analysis/translation workers. The real
  HTTP transport observes cancellation, so no production provider request remains orphaned.
- On Ctrl+C/host action cancellation, ConPTY disposal terminates the child if necessary, pipeline
  disposal cancels provider work, and the host performs fault teardown. PowerShell/ConPTY exit and
  abnormal host faults follow the same generation invalidation.
- Worker shutdown does not wait indefinitely on a provider that ignores cancellation: coordinator
  `WaitAsync(linkedToken)` returns and observes the detached task. This avoids stuck teardown and
  unobserved exceptions while stale-publication checks protect the event path.
- Companion disconnect only removes that authenticated event connection; bounded event writes never
  block the program pane or provider lifecycle. Host disposal cancels IPC loops and owns all created
  ConPTY, pipe, session, and HTTP resources.
- Transport catch filters distinguish caller/generation/shutdown cancellation from its deadline, so
  cancellation is not relabeled as timeout or unavailable.

## Privacy

The audited invariant holds: **no suspected secret reaches provider serialization**.

Live screens candidate text before queueing and repeats screening at coordinator and final provider
boundary; generation/fingerprint authorization is also checked immediately before payload creation.
Last screens the exact selected outbound serialization through `AssistancePrivacyGate` and verifies
the authorized payload fingerprint at the provider boundary. Both fail closed. The shared provider
diagnostic record has no content-bearing field, and the JSON sink sanitizes endpoint query/fragment.

## Tests

### Targeted

- CLI provider/transport/profile/reliability contracts: **41 passed, 0 failed, 0 skipped**.
- Core session lifecycle/session/queue contracts: **18 passed, 0 failed, 0 skipped**.
- CLI live teardown/timing/runtime journeys: **23 passed, 0 failed, 0 skipped**.
- Aggregate targeted: **82 passed, 0 failed, 0 skipped**.

An initial command included the legacy VSTest `--logger` option. Microsoft.Testing.Platform rejected
the option and ran zero tests. It is recorded as an invalid invocation, not a test result; all targeted
commands were immediately rerun with supported arguments and passed as listed above.

### Full Release project matrix

- Core: **149 passed, 0 failed, 0 skipped**.
- CLI: **251 passed, 0 failed, 0 skipped**.
- Windows: **144 passed, 0 failed, 3 skipped**.
- Aggregate: **544 passed, 0 failed, 3 environment-gated skips**.

The three preserved skips require `TT_RUN_INSTALLED_CAPTURE_ACCEPTANCE=1` (two tests) or
`TT_RUN_CODEX_TTY_ACCEPTANCE=1` (one test). There were no flaky failures or isolated reruns.

### Build and publish

- `dotnet build TerminalTranslator.sln -c Release --no-restore`: PASS, **0 warnings, 0 errors**.
- Unique self-contained single-file win-x64 publish and safe `--help` smoke: PASS, **0 warnings,
  0 errors**.

## Human acceptance

Accepted/completed against the fixed T138 artifact in real Windows Terminal + Windows PowerShell
5.1.

- Artifact: `artifacts/publish/t138-live-architecture-convergence-20260903/tt.exe`
- Size: `74,776,217` bytes
- SHA256: `3C8B23C23A775DB5F3D5F3C0217BA4C9161C1FB49A8F5641BC3EE2F93527B677`

The user confirmed the following acceptance results:

- With diagnostics disabled, live translation worked normally and emitted no provider diagnostic
  output by default.
- `tt on` enabled translation, and normal English output was translated correctly in the companion
  pane.
- `tt off` prevented subsequent output from being translated.
- A later `tt on` restored normal companion translation.
- `TT_PROVIDER_DIAGNOSTICS=1` was verified separately on this exact artifact. Live provider
  diagnostics were emitted with endpoint, model, HTTP status, header latency, and completion latency.
  Inspection confirmed that no source terminal text, translated content, response body, API key,
  Authorization header, or credential value was exposed.
- Codex CLI `v0.152.1` launched successfully inside the live ConPTY session. No
  `stdout is not a terminal` regression occurred, and normal Codex TUI interaction remained usable
  after diagnostics were disabled.
- Final `tt last` regression checks using synthetic English command output passed on the fixed T138
  artifact. The phase-2 strict-previous capture/selection path still produced its structured
  translation and recommendation result; no `tt last` capture, authorization, provider-profile, or
  inline-rendering regression was observed.

All required human acceptance gates passed. The artifact hash above is the accepted T138 hash.

## Known non-blocking observations

- The last one-shot composition relies on process lifetime for its internally created `HttpClient`
  rather than exposing explicit asynchronous composition disposal. No request outlives the awaited
  command, and this is not a phase-3 blocker; revisit only if the CLI gains a reusable in-process host.
- Full-screen Codex/TUI output can produce noisy or duplicated companion translations because
  incremental VT redraw content is currently translated as transient text. This is live-specific
  capture/VT/UX quality work, not a T138 provider-architecture blocker.
- Terminal Translator status text such as `Translation enabled.` can still reach the live provider
  and may receive an inappropriate instruction-like translation. This is live ownership/filtering or
  translation-quality work, not a T138 architecture blocker.

Both observations are intentionally deferred to a subsequent live-specific UX/TUI stabilization
task. T138 makes no attempted fix and does not reopen T137.

## Final recommendation

With automated and real-terminal acceptance complete for the fixed artifact hash, live + last
provider architecture is sufficiently converged and stable for live-specific UX/quality improvements
on the 003 branch. The shared low-level settings, security policy, transport, timeout, taxonomy,
serialization, and safe diagnostics are aligned while product semantics remain separate.

No architecture blocker is currently known that must be resolved before
`phase3-live-v2-baseline`. T138 is accepted/completed; the two recorded live UX observations are
explicitly non-blocking follow-up work.
