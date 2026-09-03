# Phase 0 Research: On-Demand Terminal Assistance

## Scope and evidence

This research resolves implementation choices inside the already Accepted architecture. It does not
reconsider the capture source. Inputs were the [Accepted ADR](../../research/tt-last-capture/ADR-tt-last-capture.md),
the [Codex verification report](../../research/tt-last-capture/codex-verification/verification-report.md),
the real Windows Terminal + Windows PowerShell 5.1 verification evidence, Feature 001 artifacts, and
the current production/test code.

Primary platform references used for implementation semantics:

- [Start-Transcript (PowerShell 5.1)](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.host/start-transcript?view=powershell-5.1)
- [about_Prompts (PowerShell 5.1)](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_prompts?view=powershell-5.1)
- [Get-History (PowerShell 5.1)](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/get-history?view=powershell-5.1)
- [about_History (PowerShell 5.1)](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_history?view=powershell-5.1)
- [about_Automatic_Variables (PowerShell 5.1)](https://learn.microsoft.com/en-us/powershell/module/Microsoft.PowerShell.Core/about/about_automatic_variables?view=powershell-5.1)
- [Register-EngineEvent (PowerShell 5.1)](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/register-engineevent?view=powershell-5.1)
- [about_Profiles (PowerShell 5.1)](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_profiles?view=powershell-5.1)

## R1. PowerShell integration mechanism

**Decision**: Install a versioned managed loader in LocalAppData and one idempotent, uniquely marked
dot-source block in the Windows PowerShell CurrentUser/CurrentHost profile. The loader saves and wraps
the effective custom `prompt`, initializes capture after profile setup, delegates prompt rendering to
the saved function, and catches every TT-side failure. Disable/uninstall removes only TT-owned content.

**Rationale**: Ordinary sessions must capture without `tt start`, and Windows PowerShell 5.1 has no
proven universal independent command-start hook. A prompt wrapper is the verified boundary mechanism
and naturally runs after a completed command. A small managed block avoids owning or replacing the
user's profile and supports update/recovery.

**Alternatives considered**:

- Copy a full script into `$PROFILE`: rejected because upgrades and rollback would rewrite too much
  user-owned content.
- Require users to dot-source manually per session: rejected because opted-in future sessions must
  work automatically.
- Inject environment only when launching a shell: rejected because the feature must work in ordinary
  Windows Terminal sessions not launched through `tt start`.

## R2. Independent session identity

**Decision**: Generate a new GUID plus nonce in each integrated PowerShell process; bind its manifest
to current-user SID, PowerShell PID, process-start identity, integration version, and sequence. Pass
only the opaque session identity/nonce to child `tt.exe` in dedicated environment variables, and
validate the direct invoking process against the manifest.

**Rationale**: This creates an explicit authority chain from current shell to its own records, avoids
PID-reuse confusion, and implements the GUID association already shown feasible by verification.

**Alternatives considered**:

- Most recently modified capture: rejected by the product contract because concurrent sessions can
  modify independently.
- PID only: rejected because PIDs are reused and do not prove user/session binding.
- Stable identity inherited unchanged into nested shells: rejected because it would merge sessions;
  a newly initialized nested shell must mint its own identity.

## R3. Previous completed-command boundary

**Decision**: Use a per-command transcript interval with sidecar-only session/sequence opening and
closing boundaries. At prompt entry, snapshot the latest PowerShell history entry, execution status, `$?`,
relevant `$LASTEXITCODE`, and interruption evidence before TT maintenance. Finalize only when
sidecar boundary facts, history, transcript command echo, sequence, file lifecycle, and session
metadata agree. Command text comes from session history, not prompt parsing. Only contextual
self-polluting commands (`tt last` and `tt ask last`) are marked retrieval-ineligible; ordinary
`tt ask` remains an ordinary completed command under strict previous-command semantics. Boundary
facts and Start/Stop status are not printed into user-visible output.

**Rationale**: The next prompt is the reliable completion observation point supported by the
Accepted architecture evidence. History preserves multiline command identity and avoids fixed prompt
regex. The running `tt last` is not yet finalized, and explicit ineligibility prevents consecutive
calls from targeting prior TT commands.

**Alternatives considered**:

- `$?` alone: rejected because it cannot independently prove command identity or interruption.
- Prompt text regex: rejected because custom prompts and continuation forms are not universal.
- Treat all transcript content since last read as previous output: rejected because async output and
  self-output would contaminate boundaries. Content before the uniquely matched command echo is
  unattributed; V1 does not attempt provenance separation for output that arrives while a later
  command is already executing.

## R4. Retained 10,180,000-byte implementation

**Decision**: Give each command an active native Transcript staging file, then stop/flush it at the
safe prompt boundary, acquire a bounded-wait exclusive stable snapshot, and record its byte length/hash
before validation. Failure to obtain quiescence rejects the candidate. Publish validated commands as
immutable retained records. Define
`RetainedBytes` as logical byte lengths of every content/metadata/index file referenced by the single
committed manifest generation, including that manifest. Unpublished candidates, transaction files,
old manifests, unreferenced cleanup residue, and filesystem allocation slack are not eligible
retained records. Evict oldest complete retained records before publishing the newest candidate,
using atomic manifest-generation replacement. Active staging is not part of the retained hard limit
and may temporarily exceed it.

**Rationale**: This maps exactly to the amended Product Decision. The previously observed technical
blocker—native `Start-Transcript` can append past a realtime file ceiling—no longer conflicts with
the requirement. The retained generation stays within the cap before and after publication, and
failure can leave the previous valid generation untouched.

**Alternatives considered**:

- Stop/restart one shared transcript based on live byte polling: rejected because polling cannot
  guarantee a physical ceiling and creates avoidable gaps.
- Named-pipe Transcript target: isolated feasibility probing did not establish PowerShell 5.1 support
  and it is unnecessary under the amended semantics.
- In-place compaction of an open transcript: rejected because append ownership and partial rewrite
  failure make boundaries unreliable.

## R5. Single command larger than retained capacity

**Decision**: “Oversized” means the fully serialized retained record—validated extracted command
output plus full command identity/boundary/termination metadata—exceeds the maximum after older
records are evicted, not that raw transcript scaffolding is large. Stream the validated ordered
command output into a bounded, UTF-8-safe HEAD + TAIL representation. Dynamically reserve actual
serialized metadata; split remaining payload equally with an odd byte assigned to TAIL; move cuts
inward to rune boundaries and optionally an earlier line boundary. If measured serialization is
still high, remove bytes from the larger side and then TAIL on a tie. Record the original extracted
command-output UTF-8 byte count and `LocalCaptureTruncated`. If essential full command identity or
boundary metadata cannot fit, discard the candidate and fail contextual retrieval closed.

**Rationale**: It preserves the newest command and both ends of its diagnostics without claiming the
middle still exists. Dynamic accounting avoids inventing a second payload constant or violating the
exact total-store limit.

**Alternatives considered**:

- Drop every oversized command: safe but unnecessarily loses useful newest context.
- Keep only HEAD or only TAIL: rejected because the product explicitly permits HEAD + TAIL and both
  commonly carry setup and failure information.
- Arbitrarily cut the raw record at the cap: rejected because it can destroy boundary identity and
  would hide local truncation.

## R6. Retention and capture failure isolation

**Decision**: Finalization operates on a candidate separate from the last published retained
generation. Before atomic manifest replacement, failure leaves the old generation authoritative;
after replacement, the new already-bounded generation is authoritative. Failure deleting any raw
staging, unpublished/transactional candidate, or superseded-generation content creates one protected,
ineligible finalization residue set and is never a rollback or second reader choice. Capture stops
before any new staging/finalize, and recovery must remove the entire set before resuming. Retries occur
at the next prompt, normal exit, and stale initialization, so sets cannot accumulate across commands.
A set can include one possibly oversized raw staging file, bounded candidate artifacts, and at most
one superseded generation; these are temporary cleanup state, not committed retained records. Any
failed newest finalization/cleanup/restart transitions health to unavailable and returns the original
prompt; retrieval validates only the one committed manifest and refuses to skip back.

**Rationale**: The normal shell never depends on capture success. Candidate isolation yields both
fail-open shell behavior and fail-closed assistance without publishing partial content.

**Alternatives considered**:

- Retry indefinitely at the prompt: rejected because capture must not block the shell.
- Return the prior command silently after the newest candidate fails: rejected because strict `last`
  cannot skip backward.
- Guess from a partial staging file: rejected by boundary and privacy requirements.

## R7. Cleanup and protected storage

**Decision**: Store staging and retained session data below
`%LOCALAPPDATA%\TerminalTranslator\Capture\<random-session>` with inheritance disabled and a protected
DACL granting the current user (and Windows principals required for OS operation, such as SYSTEM)
access, but no broad user groups. A best-effort engine-exit handler and one cleanup-only watcher per
session delete the exact directory on normal process termination. The watcher starts once after
session initialization, validates PID/start identity, uses bounded idle resources, has no network or
provider access, never reads capture content, exits after cleanup, and is not automatically restarted.
Watcher failure leaves stale residue for the next TT initialization. Initialization may enumerate
content-free owner manifests solely for stale cleanup; question-only `tt ask` never opens record
content, resolves session context, or constructs a capture retriever. Uncertain stores are deleted or
ineligible, never associated with a new session.

**Rationale**: LocalAppData avoids user-document sync locations; manifest/process binding supports
safe stale discovery; the watcher covers normal window closure without becoming a capture source.

**Alternatives considered**:

- Documents/Desktop/profile directory: rejected due to sync and user-content exposure.
- Age alone for stale detection: rejected because a long-running live session can be old.
- Keep records for restart convenience: rejected because cross-session history is out of scope.

## R8. Capture enable/disable and configuration

**Decision**: Extend the existing configuration command/model with an independent capture preference:
`tt configure --capture enabled|disabled`. Enabling displays capture purpose before installing the
managed integration. Disabling persists first, removes the managed profile block, and causes an
already-loaded wrapper to delete/deactivate its session at the next prompt. Provider options remain
all-or-none when present. Capture-only and legacy provider-only invocations are valid, but mixing
capture and provider options in one invocation is rejected, avoiding cross-domain partial commit.
Capture changes stage/validate TT-owned loader, marked block, and preference changes, then restore
prior TT state on failure. Profile replacement uses a protected same-directory transaction temporary
that is deleted after commit/rollback and stale-cleaned after a crash; no durable full-profile backup
is retained.

**Rationale**: It reuses the current configuration entry point without adding a large CLI surface,
records one durable choice, and makes profile ownership reversible.

**Alternatives considered**:

- Ask at every shell start: rejected by the spec.
- Automatically enable during first `tt last`: rejected because capture requires an informed choice
  before future terminal content is persisted.
- Use provider consent as capture consent: rejected because local persistence and external disclosure
  are separate trust boundaries.
- Combined capture/provider mutation: rejected because it unnecessarily couples profile rollback to
  provider-settings atomicity.

## R9. Health notification

**Decision**: Maintain session-scoped health transition state in non-content metadata. The prompt
wrapper prints once on transition into unavailable and optionally once after a validated recovery;
all intermediate prompts stay quiet. `tt last`/`tt ask last` inspect the same state and fail closed.

**Rationale**: Transition-based notices meet timely visibility without prompt spam and keep diagnostic
state content-free.

**Alternatives considered**:

- Print every failed prompt: rejected as disruptive.
- Never notify until `tt last`: rejected because capture failure must be reported near the prompt.
- Put transcript excerpts in diagnostics: rejected by the local privacy boundary.

## R10. Existing pipeline reuse

**Decision**: Reuse stable leaf behavior: provider settings/storage conventions, destination
fingerprinting/disclosure, SecretDetector, credential lookup, safe HTTP handler policy, timeout,
response-size bounds, normalized errors, and content-free diagnostics. If necessary, extract only a
minimal chat-completion HTTP transport shared by the existing translation adapter and a new
on-demand adapter. Keep all live-mode prompts and limits unchanged.

Do not reuse the live segment classifier, VT extractor, work queue, worker, coordinator, hosted
ConPTY lifecycle, IPC, submitted-command tracker, or companion renderer.

**Rationale**: Stable leaves carry security behavior worth centralizing, while the live pipeline's
segment/generation/pane semantics do not match whole previous-command or stateless question flows.

**Alternatives considered**:

- Route everything through `ProductionTranslationPipeline`: rejected because it imposes live
  segmentation, queueing, and companion assumptions.
- Duplicate all HTTP/security code: rejected because fixes could diverge across two adapters.
- Refactor the entire live pipeline: rejected as speculative and an explicit scope violation.

## R11. Whole-output language eligibility

**Decision**: Add a small Feature 002 whole-output eligibility policy that detects any English
natural-language content without a length, word-count, or CJK-ratio threshold. It distinguishes
technical-only tokens enough to avoid meaningless calls but treats mixed Chinese/English as eligible
and sends the selected whole context after privacy checks.

**Rationale**: The current `EnglishCandidateClassifier` intentionally applies live-segment heuristics
(minimum size/word evidence and CJK ratio) that contradict Feature 002's “any translatable English”
semantics.

**Alternatives considered**:

- Reuse the classifier unchanged: rejected because it would skip required short English.
- Ask the provider unconditionally: rejected because Chinese-only output must make no provider call.
- Alter the live classifier globally: rejected due to Feature 001 regression risk.

## R12. Provider consent and privacy gate

**Decision**: Persist a non-content consent grant keyed by provider adapter, normalized destination,
model, credential-source identity, disclosed content scope, and consent-policy version. Reuse the
existing fingerprint/disclosure concept, not its session-only consent instance. Build the exact
outbound selection, run SecretDetector over every field, and require a final gate assertion at the
adapter boundary. `tt ask` uses a question-only scope; `tt last` and `tt ask last` share one disclosed
previous-command terminal-context scope.

**Rationale**: Product consent must survive unchanged future explicit requests and be invalidated by
trust-boundary changes. Screening exact selected content guarantees an oversized HEAD + TAIL cannot
bypass the gate. Local Capture is never screened merely to exist on disk.

**Alternatives considered**:

- Existing in-memory session consent only: rejected because it cannot provide consent-once behavior
  across ordinary shells.
- One permanent boolean: rejected because it cannot detect provider/destination/scope changes.
- Screen before local persistence: rejected because that is not the product's local-storage policy.

## R13. AI input budget and HEAD + TAIL selection

**Decision**: Use an adjustable 8,192 UTF-8-byte variable-input safety budget in V1, or the smaller
capability declared by an adapter. The current provider adapter already uses an 8-KiB source bound,
and no configured model capability is trustworthy today. Count command, question, selected output,
and termination metadata; reserve required context first; divide remaining output bytes between HEAD
and TAIL; trim only on valid UTF-8 rune boundaries and prefer nearby line boundaries. The adapter
also accounts for fixed prompt overhead and response reserve.

After reserving required fields, split remaining output bytes equally between HEAD and TAIL, assigning
an odd byte to TAIL. Move each cut inward to a valid UTF-8 rune boundary; prefer an earlier line
boundary within that share, but use the rune-safe cut when no newline exists.

Track AI selection separately from local completeness. `AiHeadTail` means locally complete output was
reduced for provider input; `LocalHeadTail` means content was already discarded by retained-cap
finalization. Both flags may apply and must be rendered separately.

**Rationale**: A byte budget is deterministic and testable with current configuration, while staying
replaceable by provider capability policy. Separate state prevents misleading completeness claims.

**Alternatives considered**:

- Hard-code model context windows in product requirements: rejected because providers/models change.
- Count characters: rejected because UTF-8 wire size varies.
- Tokenize with a provider-specific library in Core: rejected because it would break provider
  neutrality and still need wire-overhead accounting.

## R14. CLI grammar and inline behavior

**Decision**: Compose `last` and `ask` using System.CommandLine. `ask` accepts a required variadic
question argument and an exact `last` subcommand with its own required variadic question; quote and
Unicode handling are delegated to the parser and remaining tokens are joined without shell
reinterpretation. `--last` is rejected. All responses render in the invoking console; there is no
companion process or conversation store.

**Rationale**: This maps cleanly to the existing command-factory pattern and unambiguously separates
question-only from contextual scope. Variadic input accommodates quoted and ordinary multi-token
questions.

**Alternatives considered**:

- Treat the first word `last` as question text heuristically: rejected because grammar and privacy
  scope would be ambiguous.
- Add `--last`: rejected because the canonical product syntax is a subcommand.
- Reuse companion rendering: rejected because Feature 002 is inline by contract.

## R15. Ctrl+C, async output, and real-environment verification

**Decision**: Treat interrupted output as reliable only when history execution status,
sidecar session/sequence boundaries, stopped transcript tail, and captured termination evidence agree. Output
after prompt return is outside the closed record and is never reassigned backward. Preserve a manual
Windows Terminal + PowerShell 5.1 acceptance gate for Ctrl+C, native stdout/stderr ordering, custom
prompts, independent panes, cleanup, and failure isolation.

**Rationale**: Agent PIPE harness topology is not equivalent to a real Windows Terminal session, and
PowerShell 5.1 interruption signals differ among PowerShell and native commands. Conservative
retrieval meets the fail-closed requirement.

**Alternatives considered**:

- Promise every Ctrl+C case: rejected because reliability has not been established.
- Attribute late job output to the last command: rejected by V1's deterministic completion-boundary
  semantics.
- Treat harness-only tests as acceptance: rejected by prior verification evidence.

## R16. Transcript cycle and foreground-failure feasibility

**Decision**: Proceed with repeated per-command Transcript staging, subject to the real-WT acceptance
gate. A local Windows PowerShell 5.1 isolated probe completed three consecutive Start/Stop-Transcript
cycles; each chunk retained PowerShell output, PowerShell errors, native stdout, and native stderr,
and assigning Start/Stop results suppressed their status text while the session survived.

Reflection over the installed Windows PowerShell 5.1 `System.Management.Automation` assembly further
showed that `PSHostUserInterface.FlushPendingOutput` dispatches its flush lambda with `Task.Run`; that
lambda catches `IOException` and `UnauthorizedAccessException` around `FlushContentToDisk`. A direct
reflection fault/recovery probe made `FlushContentToDisk` throw `DirectoryNotFoundException`: the
pending list remained at one item, then wrote the marker after a valid path was supplied and cleared
to zero only after success. Installed IL likewise places `OutputBeingLogged.Clear()` after the full
`WriteLine` loop. Thus relevant write faults do not propagate through the foreground pipeline and
pending content is retained for retry rather than silently dropped. Boundary finalization still
requires bounded-wait exclusive access, stable length/hash, and a valid stopped transcript; otherwise
Capture becomes unavailable and contextual requests fail closed. Real Windows Terminal acceptance
remains necessary for interactive prompt topology, Ctrl+C, and visible failure behavior.

**Rationale**: This directly addresses the risk that native transcript staging I/O could violate the
hard shell fail-open requirement, without changing the Accepted capture architecture. The probe is
not treated as proof of every filesystem fault or GUI topology; conservative boundary validation and
real acceptance cover the remaining risk.

**Alternatives considered**:

- Declare fail-open based only on prompt-wrapper exception handling: rejected because foreground host
  transcript writes occur before the next prompt.
- Require a realtime bounded transcript sink: unnecessary after the Product Owner changed the hard
  limit to completed-command retained state.
- Treat PIPE-only cycling as final compatibility acceptance: rejected; it is feasibility evidence,
  not a replacement for real WT/PS5.1 validation.

## R17. Windows PowerShell 5.1 native transcript completion

**Decision**: Do not use an elapsed producer-quiet interval as completion proof. During a managed
transcript interval, set Windows PowerShell 5.1's internal `ConsoleVisibility.AlwaysCaptureApplicationIO`
mode so `NativeCommandProcessor` redirects native stdout/stderr through its reader threads and joins
those threads before the pipeline completes. At the next prompt, restore the host's prior mode, run
two synchronous `TranscribePipelineComplete` + `FlushContentToDisk` passes, and require both
`OutputToLog` and `OutputBeingLogged` to be empty with unchanged staging length before Stop. Validate
the stopped file again before immutable publication. If reflection or any structural signal is
unavailable, fail capture closed rather than falling back to a delay or console scrape.

Installed PowerShell 5.1 (`5.1.26100.9168`) evidence rejected the original late-enqueue theory. A
failed sequence observed 51.24 ms of empty transcript queues, no staging growth at 5/20/50 ms after
Stop, and a stable published snapshot, yet stdout was already absent before Stop. Runtime inspection
showed the default direct-console path waits for the process and then scrapes `RawUI.GetBufferContents`;
at the finite buffer bottom, its cursor-wrap test can select only the blank final row. The queues being
observed belong to transcript writing and cannot prove completion of that separate console scrape.

Native file redirection remains file-only. The captured-I/O path makes PowerShell transcribe an
internal `ParameterBinding(Out-File)` record; it is removed only when the command AST proves a native
file redirection, preserving identical user-authored output otherwise.

**Rationale**: PowerShell 5.1 provides no deterministic public transcription-completion event. The
joined native-reader lifecycle is the strongest available producer signal, and combining it with
two synchronous queue/content checks plus stopped-snapshot validation closes each independently
observable stage. It fixes the source that lost bytes instead of extending a probabilistic delay.

**Alternatives considered**:

- Increase 50 ms to a larger quiet constant: rejected because empty transcript queues do not cover
  the native console-buffer producer and the reproduced loss had no late activity.
- Read `CONOUT$` or add a console-buffer fallback: rejected by the accepted capture architecture and
  would retain the same finite-buffer ambiguity.
- Treat Stop-Transcript or post-Stop stability alone as completion: rejected because the reproduced
  staging file was already incomplete before Stop and remained stable afterward.

## R18. Selective joined-reader capture with TTY preservation

**Decision**: Preserve PSReadLine's current `AddToHistoryHandler` and compose it with a small managed
delegate. In PSReadLine 2.0 the hook runs after acceptance but before `ReadLine` returns the command
for execution, which selects the child-handle architecture without replacing Enter or validation
bindings and without running a PowerShell scriptblock inside PSReadLine's callback. Enable
the T133 joined application-I/O reader only when an accepted command line proves a direct capture-safe
native invocation. Known TUI, pager, remote-shell, and
interactive-runtime families (including `codex`) keep the original console mode, so the child
inherits genuine stdin/stdout/stderr console handles. Unknown or indirect invocations default to
TTY preservation.

PSReadLine 2.0 also invokes the history handler while loading its history file. The managed delegate
reads PSReadLine's one-time-initialization state and skips those callbacks. The last persisted history
command is decoded once at registration, and the last accepted classification is reapplied after transcript
rotation. This preserves classification for an immediately repeated command even though PSReadLine
can suppress the handler for consecutive duplicate history entries.

The classifier is syntax/name based because Windows console executables do not advertise whether
they will require terminal handles before process creation. It includes conditional rules for
commands such as `cmd /c`, PowerShell `-Command`/`-File`, and interpreter eval modes; a bounded
application-name override supports additional TTY-sensitive tools without changing provider or
previous-command selection behavior. Native file redirection remains AST-proven and file-only.

**Rationale**: T133 enabled the internal mode for the whole transcript interval, before the next
native child was created. That reliably joined stdout/stderr readers but replaced the child's output
console handles with pipes, causing Codex to report stdout/stderr as non-terminals. A pre-execution
decision is the last deterministic point before `NativeCommandProcessor` creates the process and can
therefore preserve both properties. Post-launch repair cannot replace inherited handles safely.

**Alternatives considered**:

- Globally disable `AlwaysCaptureApplicationIO`: rejected because it reopens T133's finite-console-
  buffer capture race for ordinary native commands.
- Add a delay at prompt completion: rejected because queue quietness does not repair the handles
  inherited by an already-created TUI process and does not cover the console scrape producer.
- Detect TTY use after process launch: rejected because stdin/stdout/stderr inheritance has already
  occurred before the application can call `GetConsoleMode`/`isatty`.
- Bind Enter to `ValidateAndAcceptLine` or a scriptblock wrapper around `AcceptLine`: rejected after
  the existing single-Ctrl+C probe showed both Enter-binding changes were not behaviorally transparent.
  The initially observed thousands of `AddToHistoryHandler` calls were traced to PSReadLine 2.0's
  one-time history-file load, not per-keystroke execution. A PowerShell history-handler scriptblock
  was also rejected after repeated probes exposed intermittent Ctrl+C loss; the managed delegate skips
  initialization callbacks, preserves accepted-line timing, and restores the prior handler on disable.
- Make every unknown command capture-safe: rejected because an unknown interactive program must keep
  the pre-T133 terminal behavior unless it is explicitly classified.

## T135 assistance-provider reliability boundary

**Decision**: Keep the configured ten-second provider limit and make the transport-linked
`CancelAfter` token the sole production timeout authority by setting the shared production
`HttpClient.Timeout` to infinite. Start that budget immediately before `SendAsync`, after capture,
selection, authorization, serialization, and spinner setup. Preserve provider failure source and
content-free request telemetry through the adapter: endpoint, model, HTTP status, request-start UTC,
response-header and completion latency, exception type, cancellation reason, and timeout source.
Detailed diagnostics are opt-in through `TT_PROVIDER_DIAGNOSTICS=1` and never contain request,
capture, response, or credential content.

**Rationale**: The two original user messages were renderer projections of only `ProviderError` and
`ProviderTimeout`. The former combined DNS/TLS/socket failures, HTTP failures, authentication,
rate-limiting, and malformed JSON, so the historical first failure cannot be reconstructed from the
old output. A content-safe live probe against the configured DeepSeek endpoint completed once with
HTTP 200 in 6.676 seconds and a following probe failed to complete within the probe's 30-second
observation boundary, establishing variable external latency but not a retryable status for the
original call. Retrying a chat-completion POST after a timeout can duplicate billable work whose
response was merely lost, and splitting the unchanged ten-second budget would reject observed valid
responses. No automatic retry is therefore added without status-specific production evidence.

**Alternatives considered**:

- Increase the provider timeout: rejected because it masks the observed boundary and violates T135.
- Retry every timeout/unavailable result: rejected because the original failure source was discarded,
  timeout completion is ambiguous, duplicate provider work is possible, and total latency would grow.
- Emit raw exception messages or response bodies: rejected because they can include destination or
  user/provider content and would pollute the normal command UX.
- Rework capture or T134 application-I/O classification: rejected because deterministic capture-to-
  selection tests preserve both smoke outputs exactly and the provider failure messages are reachable
  only after reliable retrieval, English eligibility, request selection, and authorization.

### T135 installed-acceptance blocker follow-up

The first T135 manual recipe inserted `$LASTEXITCODE` between each native command and `tt last`.
That changes the strict previous command by design. Inspection of the real installed capture
generation proved that the native stdout record was retained as 48 exact UTF-8 bytes with exit 5,
followed by a one-byte `$LASTEXITCODE` record containing `5`; the stderr/error-record capture was
retained as 827 bytes with exit 7, followed by a one-byte `$LASTEXITCODE` record containing `7`.
`tt last` therefore evaluated the numeric command output and correctly returned no translatable
English. T134's original reproduction invoked `tt last` immediately after the native command and did
not create this intervening record.

Do not weaken strict-previous selection or English eligibility to accommodate an acceptance probe.
Instead, acceptance must observe `$LASTEXITCODE` only after `tt last`, or preserve it in a variable as
part of the same submitted native-command line if needed. Add installed-loader coverage that runs the
native stdout, native stderr, and managed-output cases with no intervening command and passes their
real retained records through the production eligibility/selection policies with a fake provider.
Numeric noise remains ineligible. When `TT_PROVIDER_DIAGNOSTICS=1`, emit additional aggregate-only
pre-provider pipeline diagnostics so retrieval kind, byte/character counts, eligibility, and
selection can be distinguished without exposing command or output text.

## T136 provider request profile investigation

**Decision**: Do not change the production request profile in T136. The audited assistance request
serializes only `model`, two `messages`, and `temperature: 0`; it omits `thinking`,
`reasoning_effort`, `max_tokens`, and `response_format`. For DeepSeek V4 this selects the provider's
default thinking mode at high reasoning effort and uses text output rather than API JSON Output.

A serial controlled benchmark on 2026-09-02 used the configured `deepseek-v4-flash` endpoint, five
runs per primary profile/input cell, a 48-byte synthetic success message, and a 900-byte synthetic
PowerShell/package-error payload. The current profile completed short input at median 3.648 seconds
but timed out all five medium requests after HTTP 200 headers. Disabling thinking reduced medians to
0.653 and 1.902 seconds with no timeout, while retaining only prompt-level JSON instruction caused
frequent inner JSON parse failures. Adding API JSON Output to the non-thinking profile produced valid
translation/recommendation JSON and aggregate quality signals in every tested request. Adding a
1,024-token output bound did not show a further latency benefit in this small sample.

The latency evidence strongly identifies default high-effort thinking as the main ten-second timeout
source, and the malformed evidence independently identifies prompt-only JSON as an unreliable wire
contract. A thinking-only production change would leave the tested non-thinking profile malformed in
8 of 10 primary requests; a JSON-only change retained all three medium-input timeouts in the isolation
sample. Applying both would mix two behavior variables, contrary to the single-variable investigation
rule. Production therefore remains unchanged pending a separately approved request-contract change.
Content-free diagnostics now distinguish `outer-json-invalid`, `inner-json-invalid`, `empty-content`,
`missing-content`, `truncated/finish-length`, `schema-invalid`, and `response-too-large` without
recording request or response content. Full evidence is in
`validation/t136-provider-request-profile.md`.

## T137 non-thinking structured provider profile

**Decision**: Correct the production request contract with the two independently supported T136
controls. The shared typed DTO now always carries `thinking: {"type":"disabled"}` for Terminal
Translator's latency-sensitive translation and brief-assistance calls. The assistance adapter also
carries `response_format: {"type":"json_object"}` because LastTranslation, QuestionOnly, and
QuestionWithPreviousCommand all explicitly request JSON in their existing prompts and defensively
parse a request-kind-specific JSON business schema. The Feature 001 live translation adapter returns
plain translated text and therefore omits `response_format`.

These controls solve separate demonstrated defects rather than forming an untested multi-variable
optimization: disabling thinking removed the benchmark's latency/timeouts but prompt-only JSON was
malformed in 8 of 10 non-thinking primary calls; API JSON Output removed the malformed responses,
while JSON Output alone retained all three medium timeouts. Both are therefore required for the
structured assistance contract. Existing JSON prompt wording remains unchanged because DeepSeek JSON
Output also requires an explicit prompt instruction.

`temperature: 0` remains explicit. `reasoning_effort`, `max_tokens`, and `stream` remain omitted.
The 1,024-token experiment showed no latency advantage and did not establish safety for the complete
product input range, so output-token bounding remains a separate follow-up. T135's ten-second single
transport timeout authority, caller cancellation, no-retry policy, bounded response reading, and
T136 malformed subtype/schema diagnostics remain unchanged.

## Resolution

All implementation research questions required for Phase 1 are resolved. The amended retained-store
semantics remove the former realtime physical-file-cap contradiction. No new Product Clarification or
decision-changing technical feasibility blocker remains.
