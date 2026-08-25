# Data Model: On-Demand Terminal Assistance

This document defines domain concepts, invariants, and transitions. It deliberately does not fix a
serialization format, concrete C# type names, or transcript rotation algorithm.

## 1. CaptureSession

Represents one currently living, supported Windows PowerShell 5.1 process whose user enabled capture.

**Identity and facts**

- Opaque session GUID and nonce
- Current Windows user identity
- PowerShell process ID and process-start identity
- Integration/version identity
- Monotonic command sequence
- Capture preference observed by this session
- Lifecycle and health state
- Exact protected session storage authority

**Relationships**

- Owns zero or one active TranscriptStaging item.
- Owns zero or more immutable CapturedCommand records.
- Owns one CaptureRetentionState and one CaptureHealth.
- Is the only session authorized to produce PreviousCommandSnapshot values from its records.

**Invariants**

- Identity is independently generated per initialized PowerShell process.
- Identity is never inferred from file recency.
- A child `tt.exe` must prove invoking-session association before reading records.
- A record from another or stale session is never eligible.
- All content-bearing paths are current-user protected and local.

**Lifecycle**

```text
NotInitialized
  → Initializing
  → Active
  → Closing → Deleted
  → Abandoned → Stale → DeletedOnNextInitialization
```

Initialization failure skips `Active` without affecting the shell. A disabled preference does not
create an active CaptureSession.

## 2. TranscriptStaging

The native append-oriented transcript for the command interval currently in progress. It is capture
content, but it is not a completed-command retained record.

**Facts**

- Session and sequence association
- Opening boundary identity
- Protected local path
- Start/stop state
- Observed physical byte size
- Candidate finalization state

**Invariants**

- It may temporarily exceed 10,180,000 bytes while the command runs.
- It has the same ACL, purpose, no-upload/no-log, and cleanup rules as retained capture.
- It is never returned directly by previous-command retrieval.
- Only a stopped, flushed candidate that yields a bounded-wait exclusive stable snapshot with recorded
  length/hash and valid boundaries may become CapturedCommand.
- A failed candidate is discarded or left stale/ineligible; it is never guessed into a record.

**Transitions**

```text
Absent → Recording → Stopping → Candidate
Candidate → PublishedRetainedRecord
Candidate → DiscardedUnreliable
Recording/Stopping → AbandonedOnCrash
```

## 3. CommandBoundary

Evidence that one command interval belongs to a specific session/sequence and has completed.

**Facts**

- Session/sequence-bound opening and closing identities
- PowerShell history identity and full submitted command text
- Completion observation point
- Known PowerShell success/exit facts
- Known native exit code, when relevant
- Interruption evidence and confidence
- Transcript span association

**Validation rules**

- Opening/closing/session/sequence facts must agree.
- Command identity comes from integration metadata/history, not a fixed prompt regex.
- Multiline commands remain one logical submitted command.
- A custom prompt changes presentation only.
- Output after closing/prompt return is not included in the closed interval.
- Ctrl+C is marked interrupted only when evidence is reliable; otherwise the candidate is unreliable.

**State**

```text
Open → Closing → ValidCompleted
Open/Closing → InvalidOrAmbiguous
```

## 4. CapturedCommand

One finalized previous-command candidate retained for the live session.

**Facts**

- Session and monotonic sequence identity
- Command text and command classification (ordinary or contextual-assistance command)
- Ordered observed stdout/stderr transcript content for the validated interval
- Exit and termination metadata when known
- Interrupted state when known reliably
- LocalCaptureCompleteness
- Original observed byte count when locally truncated
- Retained byte accounting contribution
- Boundary validity/version

**LocalCaptureCompleteness**

- `Complete`: the finalized command output is locally retained in full.
- `LocalHeadTail`: the single command exceeded the retained capacity; only a bounded beginning and end
  remain, with the middle irreversibly discarded.

**Invariants**

- Ordinary commands that fit are published/evicted as complete logical records; their middle is never
  arbitrarily cut for ordinary cumulative pressure.
- An individually oversized record may use `LocalHeadTail`, but keeps trustworthy command identity,
  boundary, and explicit truncation metadata.
- A partially published, invalid, or inconsistent record is never retrievable.
- Contextual TT command records may exist for boundary continuity but are not eligible as the target
  of `tt last` or `tt ask last`.

## 5. CaptureRetentionState

The accounting and ordering of completed-command retained records for one session.

**Facts**

- Exact limit: 10,180,000 bytes
- Actual retained logical bytes: all content/metadata/index files referenced by the committed
  generation, including its manifest
- Oldest-to-newest immutable record order
- Published generation identity
- Last finalize outcome

**Invariants**

- At every safe command/prompt boundary after finalization handling:
  `RetainedBytes <= 10,180,000`.
- Active TranscriptStaging bytes are not included in `RetainedBytes`.
- Newest complete record is preferred; normal pressure evicts oldest complete records first.
- Publication has one commit point: before manifest replacement readers see the prior generation;
  after replacement they see only the new generation. Unpublished candidates, old manifests,
  transaction files, unreferenced cleanup residue, and allocation slack do not count as eligible
  retained records.
- Any content cleanup failure may leave one ineligible finalization residue set containing the current
  raw staging (possibly oversized), bounded candidate files, and at most one superseded generation.
  It stops capture and blocks new staging until the entire set is removed, so residue cannot
  accumulate across commands. Only the committed generation counts as retained Capture Store.
- If finalization fails, the prior retained generation remains valid and within the limit, but health
  prevents strict `last` from silently skipping the failed newest command.

**Finalize transition**

```text
Valid candidate
  → calculate fully serialized candidate size
  → if individually oversized: create marked LocalHeadTail candidate
  → evict oldest records in proposed generation until total <= limit
  → atomically publish proposed generation
  → delete superseded/candidate staging artifacts best-effort
```

Pre-commit failure preserves the old generation; post-commit failure preserves the new generation.
Either may leave one protected, ineligible finalization residue set; it never makes readers roll back
or choose by timestamp. Recovery deletes the entire set before opening another staging interval.

## 6. CaptureHealth

Content-free state describing whether the integration can reliably finalize/retrieve the strict last
command.

**States**

- `Starting`: integration is attempting first transcript interval.
- `Healthy`: the active interval and latest published generation are trustworthy.
- `Unavailable`: capture/finalize/retrieval cannot currently establish a trustworthy last record.
- `Recovering`: a new interval is being established after failure.
- `Closing`: capture is stopping and session cleanup has begun.

**Facts**

- Non-content reason category
- Whether unavailable notice has been emitted for the current outage
- Whether restoration notice is eligible
- Last healthy sequence/generation

**Transitions and effects**

- First failure: `Healthy → Unavailable`, emit one nearby unavailability notice.
- Repeated failure: remain `Unavailable`, do not repeat per prompt.
- Validated recovery: `Unavailable/Recovering → Healthy`, optionally emit one restored notice.
- Any state failure remains fail-open for the shell.
- `Unavailable`, uncertain recovery, or failed latest finalization is fail-closed for contextual
  retrieval.

## 7. PreviousCommandSnapshot

A trustworthy, immutable view returned to both contextual commands.

**Facts**

- Invoking CaptureSession identity
- Selected CapturedCommand identity and text
- Ordered retained output representation
- Exit/termination/interruption facts
- Boundary confidence (`Reliable` only for a successful snapshot)
- LocalCaptureCompleteness and original size disclosure facts

**Result alternatives**

- `Snapshot`
- `NoPreviousCommand`
- `NoOutput`
- `CaptureDisabled`
- `CaptureUnavailable`
- `UnreliableOrCorrupt`

**Invariants**

- `NoOutput` means the strict previous command produced no captured output; Chinese-only or
  technical-only non-empty output remains a Snapshot for caller-side eligibility classification.
- Retrieval never substitutes an older output-bearing command for a no-output previous command.
- Retrieval never crosses sessions or guesses through missing boundaries.
- Running/finalized TT contextual commands do not self-pollute the selected target.
- `NoOutput` and `NoPreviousCommand` are distinct user outcomes.

## 8. AIRequestSelection

The exact payload proposed for one explicit provider request after local retrieval but before
transmission.

**Request kinds**

- `LastTranslation`: previous command context; translate output and add short recommendation.
- `QuestionOnly`: explicit question only; no Capture lookup.
- `QuestionWithPreviousCommand`: explicit question plus PreviousCommandSnapshot.

**Facts**

- Request kind and explicit question, when applicable
- Command/output/termination fields allowed for that kind
- Response-language instruction
- LocalCaptureCompleteness inherited from snapshot
- AIInputCompleteness
- Variable input byte count and provider budget identity
- Privacy-gate outcome
- Consent scope/fingerprint

**AIInputCompleteness**

- `Complete`: all locally available output selected within the provider input policy.
- `AiHeadTail`: local output was complete at selection time but provider budget selected only HEAD +
  TAIL. If local output was already `LocalHeadTail`, further budget selection may additionally set an
  AI reduction flag; both disclosures remain represented.

**Invariants**

- Command text is context, never translation target.
- Question-only selection contains no Capture/session data.
- The exact complete selection, including question and metadata, passes SecretDetector before the
  provider can observe it.
- A blocked selection transmits zero request content.
- AI input truncation never overwrites local capture truncation state.

## 9. ConsentProviderContext

Non-content state defining an authorized provider trust boundary.

**Facts**

- Provider/adapter identity
- Normalized destination identity
- Model identity
- Credential-source identity (not credential value)
- Disclosed content scope
- Consent policy/version
- Grant decision and time metadata

**Invariants**

- Capture enablement is not provider consent.
- A grant matches only an identical trust fingerprint and scope.
- Provider/destination/model/credential-source or relevant scope/policy change requires renewed
  consent.
- Consent state stores no terminal/question content.

## 10. AssistanceResult

The inert inline outcome returned to the user.

**Kinds**

- Translation plus recommendation
- Stateless answer
- Informational no-send outcome
- Capture/privacy/provider failure outcome

**Disclosure flags**

- Local capture was truncated
- AI provider input used HEAD + TAIL
- Command was interrupted
- Provider content was blocked by privacy gate

**Invariants**

- `tt last` normal output has `[翻译]` then `[建议]`; recommendation is about 1–2 lines.
- Full original output is not repeated.
- Local and AI truncation notices are distinct and both render when applicable.
- AI-generated commands/snippets are display-only; AssistanceResult grants no execution authority.
- Error/health/privacy messages contain no captured secret content.

## Relationship summary

```text
CaptureSession
  ├─ owns TranscriptStaging (0..1 active)
  ├─ owns CapturedCommand (0..n retained)
  ├─ owns CaptureRetentionState
  └─ owns CaptureHealth

validated CaptureSession + latest eligible CapturedCommand
  → PreviousCommandSnapshot
  → AIRequestSelection + ConsentProviderContext + SecretDetector outcome
  → AssistanceResult

QuestionOnly bypasses CaptureSession/PreviousCommandSnapshot
  → AIRequestSelection + ConsentProviderContext + SecretDetector outcome
  → AssistanceResult
```
