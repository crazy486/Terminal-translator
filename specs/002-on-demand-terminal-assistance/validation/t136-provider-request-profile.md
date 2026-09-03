# T136 Provider Request Profile Investigation

Date: 2026-09-02 (Asia/Shanghai)

T135 remains closed. Its accepted record and accepted artifact SHA256 were not modified.

## Current production request profile

The production Feature 002 assistance adapter creates this effective JSON shape:

```json
{
  "model": "<configured model>",
  "messages": [
    { "role": "system", "content": "<request-kind system prompt>" },
    { "role": "user", "content": "<serialized selected assistance input>" }
  ],
  "temperature": 0
}
```

The configured values during the investigation were endpoint
`https://api.deepseek.com/chat/completions`, model `deepseek-v4-flash`, and a ten-second request
timeout. `HttpClient.Timeout` remains infinite; transport `CancelAfter(10s)` remains the only
production timeout authority.

| Parameter | Production behavior | Consequence for DeepSeek V4 |
|---|---|---|
| `model` | Explicit configured value (`deepseek-v4-flash`) | Uses that model. |
| `messages` | Explicit system + user messages | No history, tools, or assistant messages. |
| `temperature` | Explicit `0` | DeepSeek documents it as ignored while thinking is enabled. |
| `thinking` | Omitted | Provider default applies: enabled. |
| `reasoning_effort` | Omitted | Provider default applies: high. |
| `top_p` | Omitted | Provider default applies (`1`); sampling controls are ignored in thinking mode. |
| `max_tokens` | Omitted | TT sets no generation-token bound. The 16 KiB `ResponseReserveBytes` is only a post-response application byte check, not `max_tokens`. Current API docs do not publish a V4 Chat Completions default on the parameter page; the model page advertises a 384K maximum output capability. |
| `response_format` | Omitted | Provider default `text`; TT only asks for JSON in the prompt and then parses `message.content`. This is not API JSON Output. |
| `stream` | Omitted | TT uses the non-streaming envelope and reads the complete response body. |
| `stop` | Omitted | No TT stop sequence. |
| Other compatible/DeepSeek fields | Omitted | No penalties, `n`, seed, logprobs, tools/tool choice, user ID, or DeepSeek-specific field is sent. |

Feature 001 `ChatCompletionTranslationProvider` uses the same DTO and therefore also sends only
`model`, `messages`, and `temperature: 0`. Its system prompt is 180 characters and its user message is
the raw selected live segment. It does not ask for JSON. Feature 002's LastTranslation system prompt
is 471 characters. The benchmark's 48-byte and 900-byte synthetic outputs produced user prompts of
285 and 1,137 characters, so total message content was 756 and 1,608 characters. Provider usage for
the current short profile reported 225 prompt tokens; the current medium requests timed out before
usage arrived. The corresponding non-thinking prompt counts were 146 and 323 tokens.

Prompt audit:

- The system prompt requests translation plus one short recommendation in a single response. This is
  more work than pure translation but is the required product behavior.
- It requests only two output fields and does not ask for chain-of-thought, analysis, or a long
  explanation.
- Command text, termination facts, ordered output, and local/AI completeness are supplied once. The
  command is explicitly context-only and must not be translated.
- The selected terminal output must necessarily be represented as a translation, but the prompt does
  not ask the model to repeat the command or original output.
- There is no material repeated instruction. Safety/token-preservation instructions add fixed cost,
  while medium input adds actual translation/completion work.

Official references consulted:

- DeepSeek Thinking Mode: <https://api-docs.deepseek.com/guides/thinking_mode/>
- DeepSeek Chat Completions: <https://api-docs.deepseek.com/api/create-chat-completion/>
- DeepSeek JSON Output: <https://api-docs.deepseek.com/guides/json_mode/>
- DeepSeek Models and Pricing: <https://api-docs.deepseek.com/quick_start/pricing/>

## MalformedResponse root boundary

Before T136, both response-envelope deserialization and assistance-content deserialization could
surface `System.Text.Json.JsonException`, while the diagnostic retained only `MalformedResponse`.
The historical T135 malformed call therefore cannot be classified retroactively from its saved
diagnostic alone.

The same-size controlled current-profile reproduction in this investigation was
`inner-json-invalid`: the HTTP response envelope parsed, `choices[0].message.content` existed, but
the content was not directly parseable as the assistance DTO. This is strong evidence for the
observed mechanism, but not proof about the exact historical response body.

T136 adds the following content-free diagnostic reasons while keeping the same user-facing
`MalformedResponse`/`InvalidResponse` mapping:

| Reason | Boundary |
|---|---|
| `outer-json-invalid` | Complete HTTP body cannot deserialize as the chat-completion envelope. |
| `inner-json-invalid` | Envelope and content exist, but content is not JSON (including fenced or prefixed JSON). |
| `empty-content` | Content is present but empty or whitespace. |
| `missing-content` | Choice, message, or content is absent/null. |
| `truncated/finish-length` | Provider reports `finish_reason=length`. |
| `schema-invalid` | Inner JSON parses but required assistance fields are absent/blank or exceed the response contract. |
| `response-too-large` | The bounded outer HTTP response exceeds 32 KiB. |

No reason records the response body, inner content, capture, prompt, question, command, credential,
or raw exception message. A separate nullable `reasoningContentPresent` boolean distinguishes the
safe field interaction where reasoning exists but final content is missing; reasoning text is never
recorded.

## Controlled benchmark

Method:

- Fixed endpoint/model; no provider or model switch.
- Fixed LastTranslation system prompt and exact production user-message framing.
- Synthetic output only: short 48 UTF-8 bytes; medium 900 UTF-8 bytes with package error, command
  location, CategoryInfo-like metadata, fully qualified error ID, and exit code.
- Serial requests only; profile/input order rotated between iterations.
- Ten-second `CancelAfter`; infinite `HttpClient.Timeout`; `ResponseHeadersRead` for separate header
  and complete timing.
- No retry and no response-body logging.
- A success required HTTP 200, a complete envelope, non-empty content, valid inner JSON, and required
  non-empty `translation`/`recommendation` fields. Follow-up isolation runs additionally required a
  CJK translation signal and preservation of `Contoso.Core` and `1603` for medium input.
- Median, p90, and max use completed request wall time; p90 is nearest-rank. Five observations per
  primary cell are too few for population-level tail claims.

Primary profiles:

- A: exact production request.
- B: A plus only `thinking: {"type":"disabled"}`.
- C: B plus `response_format:{"type":"json_object"}` and `max_tokens:1024`.

| Input | Profile | n | Header median (range), ms | Complete median / p90 / max, ms | Success | Timeout | Malformed |
|---|---|---:|---:|---:|---:|---:|---:|
| Short (48 B) | A current | 5 | 91 (84-222) | 3,648 / 6,491 / 6,491 | 4 | 0 | 1 |
| Short (48 B) | B non-thinking | 5 | 98 (85-161) | 653 / 1,044 / 1,044 | 2 | 0 | 3 |
| Short (48 B) | C minimal | 5 | 86 (76-103) | 743 / 993 / 993 | 5 | 0 | 0 |
| Medium (900 B) | A current | 5 | 93 (85-331) | 10,005 / 10,012 / 10,012 | 0 | 5 | 0 |
| Medium (900 B) | B non-thinking | 5 | 154 (83-194) | 1,902 / 2,305 / 2,305 | 0 | 0 | 5 |
| Medium (900 B) | C minimal | 5 | 106 (83-286) | 1,958 / 2,230 / 2,230 | 5 | 0 | 0 |

All 30 primary requests received HTTP 200 response headers. For current-profile successful short
responses, usage reported 225 prompt tokens, 364-935 completion tokens, and 328-882 reasoning tokens;
reasoning content was present in every completed current response. Non-thinking short/medium
responses reported 146/323 prompt tokens and 33-37/253-265 completion tokens, with no reasoning
content. JSON Output added about 20 prompt tokens and completed at 25 short or 238-241 medium tokens.

Isolation profiles (three requests per cell):

- D: A plus only API JSON Output.
- E: B plus API JSON Output, with `max_tokens` still omitted.
- C rerun: E plus `max_tokens:1024`.

| Input | Profile | Complete median / p90 / max, ms | Success | Timeout | Malformed/quality failure |
|---|---|---:|---:|---:|---:|
| Short | D current + JSON Output | 3,644 / 8,816 / 8,816 | 3 | 0 | 0 |
| Medium | D current + JSON Output | 10,005 / 10,009 / 10,009 | 0 | 3 | 0 |
| Short | E non-thinking + JSON Output | 1,028 / 1,085 / 1,085 | 3 | 0 | 0 |
| Medium | E non-thinking + JSON Output | 1,820 / 1,936 / 1,936 | 3 | 0 | 0 |
| Short | C with 1,024-token bound | 863 / 960 / 960 | 3 | 0 | 0 |
| Medium | C with 1,024-token bound | 2,123 / 2,162 / 2,162 | 3 | 0 | 0 |

## Conclusions

1. Default thinking is in use. TT omits both controls, so DeepSeek V4 enables thinking at high effort.
2. It is the dominant measured timeout source. Header latency stayed around 0.1-0.3 seconds, while
   default-thinking body generation consumed seconds and timed out every medium request. Disabling
   thinking removed all 10 primary timeouts and all 6 isolation timeouts.
3. Current JSON handling is not a true JSON Output contract and is unreliable. Prompt-only JSON
   produced 8 inner parse failures across B's 10 requests. API JSON Output removed malformed results
   in all tested D/E/C requests, while default thinking still timed out medium D requests.
4. TT has no current `max_tokens` value. A 1,024-token bound made no demonstrated latency improvement
   versus otherwise identical E because these responses naturally stopped around 25/238-246 tokens.
   The omission permits more generation than this benchmark needs, but 1,024 is not yet proven safe
   for TT's full 8 KiB variable-input contract, so it is follow-up evidence rather than a production
   recommendation.
5. The most likely client-experience difference is request profile: a mature translation client is
   likely selecting non-thinking and a structured-output or robust parsing contract, while TT
   silently selected default high-effort thinking and waited for the entire non-streaming response.
   This statement about the other client's exact implementation is an inference; its wire request
   was not available for inspection.
6. There is strong evidence for the cause but not enough evidence for a safe single-variable
   production request change. Thinking-only made latency good but left prompt-only JSON malformed in
   8/10 requests. JSON-only fixed completed response shape but retained 3/3 medium timeouts. Combining
   both would violate T136's instruction not to mix optimizations in one production change.

## Changes

- Added a reusable, non-production serial benchmark under `research/provider-profile-benchmark/`.
- Added content-free malformed-response subtypes to provider failure details and opt-in diagnostics.
- Added `finish_reason` and `reasoning_content` envelope fields needed to classify truncated and
  missing-content outcomes; only finish classification and a reasoning-presence boolean are logged.
- Did not change the production request DTO, timeout, retry behavior, model, endpoint, streaming,
  capture, eligibility, strict-previous semantics, or T135 accepted record.

## Deterministic tests

- `CurrentProductionRequestProfile_SerializesOnlyDocumentedExplicitParameters` proves the exact three
  top-level production fields and every relevant omission.
- `MalformedResponses_ReportContentFreeRootBoundaryAndSubtypeWithoutRetry` covers outer invalid JSON,
  inner invalid JSON, missing content, empty content, finish-length truncation, schema failure, and
  fenced JSON while proving one HTTP attempt and content-free diagnostics.
- Existing T135 reliability tests continue to cover network, HTTP, transport timeout, handler timeout,
  caller cancellation, single attempt, opt-in diagnostics, and pipeline diagnostic privacy.

Verification results:

- T136 targeted contract tests: 10 passed, 0 failed, 0 skipped.
- One complete serial project matrix passed before the final aggregate-only reasoning-presence field:
  Core 149/149, Windows 144 passed plus 3 explicit installed-product acceptance skips, CLI 247/247
  (540 passed, 3 skipped, 0 failed overall). The final field was then covered by the targeted suite
  and a zero-warning full solution build.
- Parallel/full reruns exposed unrelated pre-existing timing flakes in three different Windows
  PowerShell/ConPTY/pipe tests. Each failed once at its own short wait boundary and passed immediately
  in isolation (1/1 each); no capture, ConPTY, IPC, or timeout code was changed for T136.
- Final `dotnet build TerminalTranslator.sln --configuration Release --no-restore
  -p:UseAppHost=false`: succeeded with 0 warnings and 0 errors.
- Final benchmark project build: succeeded with 0 warnings and 0 errors.
