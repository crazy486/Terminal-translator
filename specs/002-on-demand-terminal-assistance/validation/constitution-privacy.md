# Gate C — Constitution 2.0 and privacy audit

**Result:** PASS  
**Recorded:** 2026-08-25T15:52:02Z  
**Audit basis:** corrected production call paths, production-root tests, committed-store round trips,
and the newly published binary.

## Remediated C-001: published assistance composition

The old published paths stopped at safe placeholder delegates:

```text
Program -> CommandFactory -> LastCommand -> CaptureUnavailable
Program -> CommandFactory -> AskCommand(question) -> ProviderError
Program -> CommandFactory -> AskCommand(last) -> CaptureUnavailable
```

The corrected production paths are:

```text
tt last
Program -> CommandFactory -> OnDemandAssistanceRuntimeComposition -> LastCommand
  -> validated GUID/nonce/SID/PID/process-start/integration-version proof
  -> committed RetainedCaptureStore -> RetainedCommandRecordCodec -> PreviousCommandRetriever
  -> WholeOutputEligibility -> AiRequestSelector
  -> scope-matching ProviderConsentGrantStore/AssistanceConsentPrompt
  -> AssistancePrivacyGate(SecretDetector) -> AuthorizedAssistanceRequest
  -> ChatCompletionAssistanceProvider -> SafeChatCompletionTransport -> InlineAssistanceRenderer

tt ask
Program -> CommandFactory -> OnDemandAssistanceRuntimeComposition -> AskCommand
  -> AskAssistanceCoordinator -> QuestionOnly -> AiRequestSelector
  -> question-only consent -> exact AssistancePrivacyGate(SecretDetector)
  -> AuthorizedAssistanceRequest -> ChatCompletionAssistanceProvider -> InlineAssistanceRenderer

tt ask last
Program -> CommandFactory -> OnDemandAssistanceRuntimeComposition -> AskCommand(last)
  -> the same lazy ProductionPreviousCommandRetriever used by tt last
  -> AskLastAssistanceCoordinator -> AiRequestSelector
  -> previous-command-context consent -> exact AssistancePrivacyGate(SecretDetector)
  -> AuthorizedAssistanceRequest -> ChatCompletionAssistanceProvider -> InlineAssistanceRenderer
```

`tt ask` does not evaluate the lazy contextual retriever. Production-root regression tests make its
contextual factory throw if constructed and still complete a QuestionOnly request. No on-demand path
uses Feature 001's pane, hosted session, queue, worker, ConPTY, or IPC composition.

Provider settings/provider/authorizer leaves are deferred until the coordinator has completed
retrieval, eligibility, and exact request selection and first reaches authorization. Credentials are
still not resolved there: `SafeChatCompletionTransport` resolves the named credential only inside
`SendAsync`, after the authorizer has matched consent and run SecretDetector and
`ChatCompletionAssistanceProvider` has validated `AuthorizedAssistanceRequest`.

## Remediated C-002: oversized production finalization

The old production path ended at `CapturedCommandFinalizer -> RequiresLocalReduction`; the boundary
processor marked capture unavailable and never called `OversizedCommandRetention`.

The corrected path is:

```text
Stop-Transcript -> stable stopped snapshot -> hash/boundary validation
  -> CaptureBoundaryProcessor creates trusted CapturedCommand
  -> CapturedCommandFinalizer -> CaptureRetentionPolicy
  -> if candidate alone is oversized: OversizedCommandRetention
  -> UTF-8-safe deterministic HEAD+TAIL + LocalHeadTail/original byte count
  -> CaptureRetentionPolicy with ordinary oldest-record eviction
  -> RetainedCaptureStore candidate isolation -> atomic manifest swap
  -> one committed bounded generation -> ProductionPreviousCommandRetriever
```

The oversized branch re-enters the same retention plan and `RetainedCaptureStore.PublishAsync`; it
does not have a publication shortcut. Actual serialized metadata and manifest/descriptor overhead
are remeasured until the proposed generation is at most 10,180,000 bytes. The store independently
rejects any over-cap candidate before manifest replacement.

Persisted metadata now round-trips session GUID/nonce, sequence, PowerShell history identity,
command text, boundary session/nonce/sequence/command/reliability, exit/interruption facts,
contextual classification, `LocalCaptureCompleteness`, and original output byte count. Invalid UTF-8,
unknown/corrupt metadata, inconsistent identity/boundary, missing/hash-mismatched files, and essential
metadata overflow all fail closed.

## Constitution/privacy result

| Requirement | Corrected evidence | Result |
|---|---|---|
| CLI-first usable production commands | Published composition reaches all three coordinators and inline renderer | PASS |
| Scope-matching external consent | Question-only and previous-command scopes remain distinct | PASS |
| Exact secret gate before credentials/HTTP | Selection -> consent -> SecretDetector -> authorized adapter -> transport | PASS |
| Current-session-only capture | GUID/nonce plus direct SID/PID/start/version proof; no mtime/latest-file lookup | PASS |
| Exact retained cap | Finalizer and store measure content + metadata + descriptors + committed manifest at `<= 10,180,000` | PASS |
| Truthful local truncation | Production finalizer persists and retriever returns `LocalHeadTail` plus original bytes | PASS |
| Local + AI distinction | Disk-origin LocalHeadTail survives a later AiHeadTail selection and both renderer notices | PASS |
| Publication/failure isolation | Oversized branch uses normal candidate isolation, manifest swap, residue, cleanup, and corruption checks | PASS |
| Purpose limitation / no secondary use | Capture remains LocalAppData/current-user protected and absent from logs, telemetry, export, and sync paths | PASS |
| Feature 001 isolation | Explicit 160-test matrix passed; on-demand composition is not inserted into live mode | PASS |

Gate C passes. Gate E, Gate F, T126, and real Windows Terminal acceptance remain intentionally
unexecuted.

## 2026-08-27 bootstrap remediation applicability

**Targeted re-audit:** PASS for the new binary. The remediation changes loader executable
resolution, fresh-session environment initialization, and content-free bootstrap failure
classification only. Diagnostic output is restricted to fixed categories such as
`LoaderBridgeFailed`, `OwnerValidationFailed`, `TranscriptStartFailed`, and
`MetadataWriteFailed`; it does not render exception messages, command text, transcript content,
nonce, credentials, secrets, or captured output. Provider composition, consent, exact secret-gate
placement, retention bounds, and external disclosure paths are unchanged.
