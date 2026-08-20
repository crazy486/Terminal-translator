# Data Model: Windows Terminal Output Translation

## Modeling Rules

- All content-bearing entities are owned by exactly one Translation Session.
- Original text and translated text exist in memory only and become unreachable at session end.
- Provider credentials are never entity fields; configuration stores only an environment-variable
  name whose value is resolved at request time.
- Sequence numbers are monotonic within a session and have no meaning across sessions.
- IPC and provider DTOs are boundary representations, not persisted records.

## Translation Session

Represents the complete user-started lifetime of one program pane and one companion pane.

| Field | Type | Rules |
|-------|------|-------|
| SessionId | 128-bit random identifier | Unique for the process lifetime; safe for pipe naming; not derived from user data |
| Generation | Non-negative integer | Increments on every enable/disable transition; late work with an older generation is rejected |
| State | SessionState | Follows the state machine below |
| ProgramPane | PaneDescriptor | Exactly one; role is Program |
| CompanionPane | PaneDescriptor | Exactly one; role is Companion |
| Provider | ProviderConfiguration | Required before enable; contains no credential value |
| Consent | ConsentGrant or null | Must match session, generation, and provider fingerprint |
| StartedAt | UTC timestamp | Set once |
| EndedAt | UTC timestamp or null | Set once at terminal state |
| Cancellation | Session cancellation owner | Cancels translation work, not raw ConPTY draining |

### SessionState

```text
Created -> Starting -> Disabled -> Enabling -> Enabled
                         ^          |          |
                         |----------+----------|
                         |        disable
                         |
Disabled/Enabled -> Stopping -> Ended
        any state -> Faulted
```

- Translation is disabled by default.
- `Enabling -> Enabled` requires valid provider configuration and explicit consent.
- `Enabled -> Disabled` increments Generation, cancels requests, clears queues, and suppresses late
  results without stopping Windows PowerShell.
- `Stopping` stops new analysis, drains raw ConPTY output, clears transient content, and releases
  pipes and pseudoconsole handles.
- A companion or provider failure does not move the session to Faulted; only inability to preserve
  the program I/O session is fatal.

## Pane Descriptor

Identifies one Windows Terminal pane role without attempting to automate or scrape the pane.

| Field | Type | Rules |
|-------|------|-------|
| Role | Program or Companion | One of each per session |
| Title | Short text | Contains no terminal content |
| ProcessRole | Host or Companion | Determines internal CLI command |
| Connected | Boolean | Companion disconnect never blocks the Program role |

## Provider Configuration

Non-sensitive preferences needed to construct the phase-one external adapter.

| Field | Type | Rules |
|-------|------|-------|
| Adapter | Identifier | Phase-one supported value is the documented chat-completion HTTP adapter |
| Endpoint | HTTPS URI | HTTPS required except loopback endpoints used by tests |
| Model | Non-empty string | Maximum 200 characters |
| ApiKeyEnvironmentVariable | Environment-variable name | Stores the name only, never the value |
| RequestTimeout | Duration | Greater than zero; default 1.5 seconds; maximum 10 seconds |
| SourceLanguage | Language tag | Fixed to `en` in phase one |
| TargetLanguage | Language tag | Fixed to `zh-Hans` in phase one |

The provider fingerprint is a stable hash of Adapter, Endpoint authority/path, Model, and target
language. It excludes credentials and terminal content.

## Consent Grant

Proves that the user explicitly authorized transmission for the current session.

| Field | Type | Rules |
|-------|------|-------|
| SessionId | Identifier | Must equal owning session |
| Generation | Integer | Must equal current enabled generation |
| ProviderFingerprint | Hash | Must match current provider configuration |
| Scope | Enum | Fixed to eligible current-session segments only |
| GrantedAt | UTC timestamp | Created only after an affirmative interactive response |

Consent is in memory only. Provider change, disablement, or session end invalidates it.

## Output Segment

A transient normalized candidate derived from a copy of ConPTY output. Raw rendering bytes are
streamed directly and are not stored in this entity.

| Field | Type | Rules |
|-------|------|-------|
| SessionId | Identifier | Required |
| Generation | Integer | Must equal current session generation |
| Sequence | Unsigned integer | Strictly increasing within a session |
| NormalizedText | UTF-8 text | Maximum 8 KiB; never logged or persisted |
| Layout | LayoutHints | Line breaks, indentation, and semantic grouping only |
| SourceBoundary | Line, Block, or IdlePrompt | Explains how the candidate was finalized |
| Priority | High or Normal | High for prompts, confirmations, and errors |
| RedrawKey | Optional opaque key | Allows a newer CR redraw to replace queued older text |
| CapturedAt | Monotonic timestamp | Used for 1.5-second queue expiry |
| State | SegmentState | Follows the state machine below |

### SegmentState

```text
Capturing -> Ready -> SkippedSensitive
                  -> SkippedLowValue
                  -> DroppedOverload
                  -> Queued -> Translating -> Translated
                                          -> Failed
                                          -> Canceled
all nonterminal states -> Expired
```

- Secret screening precedes classification and enqueue.
- No transition out of SkippedSensitive can reach Queued.
- A segment from an old Generation transitions to Canceled or Expired.
- Dropped, skipped, and failed segments contain no provider response.

## Privacy Decision

Records only the outcome of local secret screening for control flow and counters.

| Field | Type | Rules |
|-------|------|-------|
| SegmentSequence | Unsigned integer | Identifies the transient segment in memory |
| Outcome | Allow or Skip | Detector failure or uncertainty maps to Skip |
| ReasonCode | Enum | Generic category only; no matched text or pattern |

Allowed reason codes are `None`, `CredentialAssignment`, `AuthorizationValue`, `PrivateKey`,
`TokenShape`, `CredentialUri`, `MalformedSensitiveBlock`, and `DetectorFailure`.

## Translation Work Item

Queue-owned request metadata. It references an Output Segment and does not duplicate source text.

| Field | Type | Rules |
|-------|------|-------|
| Segment | OutputSegment reference | Must be Ready and allowed by Privacy Decision |
| EnqueuedAt | Monotonic timestamp | Used for expiry |
| Deadline | Monotonic timestamp | Cannot exceed provider timeout |
| QueueLane | High or Normal | Derived from segment priority |

Queue invariants:

- High lane capacity: 16 items.
- Normal lane capacity: 48 items.
- Aggregate referenced NormalizedText: at most 256 KiB.
- Queue offer is non-blocking.
- A high item may evict the oldest normal item; a normal item never evicts high priority.

## Translation Item

Transient successful provider output shown in the companion pane.

| Field | Type | Rules |
|-------|------|-------|
| SessionId | Identifier | Must match source segment |
| Generation | Integer | Must still be current before display |
| SegmentSequence | Unsigned integer | One successful item per source sequence |
| SourceText | UTF-8 text | Local companion display only; same transient lifetime as source |
| TranslatedText | UTF-8 text | Non-empty, maximum 16 KiB |
| Layout | LayoutHints | Preserves group and indentation correspondence |
| ProviderRequestId | Optional opaque text | Never used as content or persisted |
| CompletedAt | Monotonic timestamp | Used for latency measurement |

## Status Event

Content-free feedback sent to the companion pane.

| Field | Type | Rules |
|-------|------|-------|
| SessionId | Identifier | Required |
| Kind | StatusKind | State, PrivacySkip, Degraded, ProviderError, or SessionEnded |
| Code | Stable short code | Contains no source or translation text |
| Count | Optional integer | Aggregated drops or skips |
| OccurredAt | UTC timestamp | Required |

Degradation events are rate-limited to one per 5 seconds. A PrivacySkip event never contains the
suspected text or detector match.

## Relationships

```text
Translation Session
|-- exactly 1 Program Pane
|-- exactly 1 Companion Pane
|-- exactly 1 Provider Configuration
|-- 0..1 current Consent Grant
|-- 0..N transient Output Segments
|   |-- exactly 1 Privacy Decision after screening
|   |-- 0..1 Translation Work Item
|   `-- 0..1 Translation Item
`-- 0..N content-free Status Events
```

## Teardown Invariants

After normal or abnormal session termination:

- No queue accepts new work.
- In-flight provider operations are canceled and late results are rejected by generation.
- Output Segment, Translation Work Item, and Translation Item collections are cleared.
- No terminal content is written to settings, files, logs, telemetry, or exception messages.
- ConPTY final output is drained independently before native handles are released.
- Only aggregate process exit status may outlive the session; it contains no terminal content.
