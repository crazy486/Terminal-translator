# Previous-Command Retrieval Contract

## Purpose

Provide one minimal, shared, provider-neutral capability for `tt last` and `tt ask last`. The
retriever does not call AI, translate, render, or inspect another session.

## Input

A current-session proof containing the opaque session identity/nonce plus validated invoking process
association. There is no fallback input such as a newest file or prompt string.

## Exclusive outcomes

### ReliableSnapshot

- Session and command identity
- Full submitted command text
- Ordered retained output representation
- Known exit/termination metadata
- Reliable interruption flag when applicable
- Valid completed boundary evidence
- Local completeness: `Complete` or `LocalHeadTail`
- Original observed size when `LocalHeadTail`

### NoPreviousCommand

No eligible completed command exists in this current session.

### NoOutput

The strict previous completed command exists but produced no captured output. The caller must not
search for an earlier output-bearing record. Chinese-only or technical-only non-empty output remains
a ReliableSnapshot for the caller's separate language-eligibility decision.

### CaptureDisabled

Capture preference is off. The caller may explain how to enable future capture but may not use a
fallback.

### CaptureUnavailable

The session health cannot provide a reliable strict-latest snapshot, including a failed newest
finalization/retention/compaction operation.

### UnreliableOrCorrupt

Session association, sequence, boundary, record bytes, or metadata are missing, inconsistent,
unknown, or corrupt. No captured text is returned to the caller/provider path.

## Selection invariants

- Select the newest eligible finalized command in the invoking session only.
- Never substitute an older record for a newest no-output command or failed newest capture.
- Never select contextual self-polluting TT commands (`tt last` or `tt ask last`) as the target;
  ordinary question-only `tt ask` remains an ordinary completed command.
- Preserve stdout/stderr in transcript-observed order.
- Do not add output that arrived after the command's completion/prompt boundary.
- A snapshot is returned only when command identity and retained output boundary are jointly reliable.
- Local truncation is a fact about retained data and cannot be cleared by a later AI selection step.

## Caller obligations

- `tt last` performs whole-output English eligibility before any provider call.
- Both callers compute provider input selection independently from local completeness.
- Both callers disclose `LocalHeadTail` before/with results.
- Both callers apply consent and SecretDetector to the exact outbound selection.
- Callers must never log or echo a snapshot on failure.

## Corruption behavior

Readers validate published-generation identity, session binding, stable staging length/hash evidence,
record ordering, byte accounting, and boundary integrity before exposing content. A missing
superseded file during post-publication cleanup does not invalidate a complete published generation;
any missing file referenced by the published generation does. Unknown versions fail closed.
