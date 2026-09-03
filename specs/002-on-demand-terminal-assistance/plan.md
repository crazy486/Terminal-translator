# Implementation Plan: On-Demand Terminal Assistance

**Branch**: `002-on-demand-terminal-assistance` | **Date**: 2026-08-25 | **Spec**: [spec.md](./spec.md)

**Input**: Frozen Feature 002 product specification plus the Accepted `tt last` Capture Architecture
ADR, as minimally amended by the Product Owner on 2026-08-25.

## Summary

Add inline, stateless `tt last`, `tt ask`, and `tt ask last` commands to the existing local .NET CLI.
Ordinary Windows PowerShell 5.1 sessions opt into a lightweight profile integration that uses native
PowerShell Transcript staging plus session/command metadata. At each trustworthy prompt boundary,
the integration finalizes the just-completed command into a per-session retained store capped at
exactly 10,180,000 bytes. Normal pressure evicts oldest complete records; one individually oversized
command becomes a marked, boundary-safe HEAD + TAIL local representation. `tt last` and `tt ask last`
share one fail-closed previous-command retriever, apply consent and SecretDetector before provider
transmission, and render inline. `tt ask` sends only its explicit question. Feature 001's hosted
ConPTY session, queues, live classifier, and companion pane remain unchanged.

## Technical Context

**Language/Version**: C# 14 on .NET SDK 10.0.400; Windows PowerShell 5.1 integration script

**Primary Dependencies**: System.CommandLine 2.0.11; existing `TerminalTranslator.Core`,
`TerminalTranslator.Windows`, and `TerminalTranslator.Cli` projects; native Windows file security and
process APIs; PowerShell 5.1 `Start-Transcript`, prompt function, history, and engine-exit facilities

**Storage**: Versioned, content-bearing session directories below
`%LOCALAPPDATA%\TerminalTranslator\Capture`; existing provider configuration plus new non-content
capture preference and consent-grant state below the established local application-data root

**Testing**: MSTest.Sdk 4.2.3 with Microsoft.Testing.Platform; unit and integration suites plus
manual/GUI acceptance in real Windows Terminal + Windows PowerShell 5.1

**Target Platform**: Windows Terminal with Windows PowerShell 5.1 Desktop, x64 V1 acceptance target

**Project Type**: Local Windows CLI with a lightweight PowerShell profile integration

**Performance Goals**: Never delay or alter command execution; bound boundary-time retention work to
one staging record and at most 10,180,000 retained bytes; return the prompt promptly; avoid repeated
health messages; make ordinary `tt ask` independent of Capture

**Constraints**: Accepted bounded Transcript + metadata architecture only; retained completed-command
store must be at most exactly 10,180,000 bytes at safe boundaries; active staging may temporarily
exceed; current-session identity only; current-user ACL; no raw capture in logs/telemetry/support
bundles; pre-provider secret gate; no generated-command execution; no production implementation in
this planning phase

**Scale/Scope**: One independently identified CaptureSession per live PowerShell process; tens to
hundreds of retained command records within a 10,180,000-byte total; three stateless CLI flows; no
cross-session history, chat history, full-screen reconstruction, or additional shell support

## Constitution Check

*GATE: Passed before Phase 0 and passed again after Phase 1 design.*

| Principle / gate | Design evidence | Result |
|---|---|---|
| I. CLI-First | `tt last`, `tt ask`, and `tt ask last` are complete inline CLI workflows; no GUI or companion pane is required. | PASS |
| II. Authorized External Disclosure | Each explicit request has a defined content scope; destination/trust fingerprint consent precedes transmission; SecretDetector examines the exact outbound selection before the provider. | PASS |
| III. Purpose-Limited Local Data | Capture is opt-in, current-session-only, current-user protected, retained-cap bounded, deleted on normal exit, stale-cleaned after crash, excluded from secondary uses, and never user-exported. Active staging has the same ACL/lifecycle even though its runtime size is not the retained hard cap. Any content cleanup failure stops capture and permits only one non-accumulating finalization residue set until retry succeeds. | PASS |
| IV. Justified Architecture | The Accepted ADR fixes bounded Transcript + session/command metadata. This plan narrows implementation choices without reopening capture-source selection. | PASS |
| V. Provider-Agnostic Design | On-demand behavioral requests remain provider-neutral; chat-completion wire details stay in the CLI adapter. The input budget is a replaceable provider policy. | PASS |
| VI. Safe Execution | AI commands and snippets are text only. No result can execute a command, modify a file, or confirm an action. | PASS |
| VII. Maintainability | Reuse stable privacy/config/error leaves; add a small retrieval core and a small transport seam; do not refactor Feature 001's live pipeline. | PASS |
| VIII. Testability | Deterministic retention, boundary, selection, privacy, consent, and renderer contracts are independently testable; real WT/PS5.1 acceptance covers topology-dependent behavior. | PASS |

No Constitution exception is required. Local staging that can temporarily exceed 10,180,000 bytes is
explicitly authorized by the amended product/ADR semantics, remains purpose-limited, and is removed
or finalized at the next safe boundary.

## Project Structure

### Documentation (this feature)

```text
specs/002-on-demand-terminal-assistance/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── checklists/
│   └── requirements.md
└── contracts/
    ├── cli.md
    ├── on-demand-provider.md
    ├── previous-command-retrieval.md
    └── shell-integration.md
```

`tasks.md` is intentionally not created in this phase.

### Source Code (repository root)

```text
src/
├── TerminalTranslator.Core/
│   ├── Assistance/                 # provider-neutral request/selection/result behavior
│   ├── Capture/                    # retained record model, retrieval, retention decisions
│   ├── Parsing/                    # new whole-output English eligibility leaf
│   ├── Privacy/                    # existing SecretDetector reused unchanged where possible
│   └── Translation/                # Feature 001 live pipeline remains behaviorally unchanged
├── TerminalTranslator.Windows/
│   ├── Capture/                    # ACL, session store, atomic publication, stale cleanup
│   ├── PowerShell/                 # managed loader/profile integration lifecycle
│   └── ConPty/ Ipc/ Terminal/      # existing Feature 001 paths remain unchanged
└── TerminalTranslator.Cli/
    ├── Commands/                   # LastCommand, AskCommand, inline renderer, composition
    ├── Configuration/              # capture preference and persistent trust consent
    └── Providers/                  # on-demand adapter + minimal shared safe HTTP transport

tests/
├── TerminalTranslator.Core.Tests/
│   ├── Unit/                       # retention, parsing, selection, privacy, state models
│   └── Integration/                # assistance orchestration without Windows profile side effects
├── TerminalTranslator.Windows.Tests/
│   ├── Unit/                       # metadata validation and store accounting where applicable
│   └── Integration/                # transcript/profile/ACL/cleanup lifecycle
└── TerminalTranslator.Cli.Tests/
    ├── Contract/                   # CLI/provider/renderer behavior
    ├── Unit/                       # argument grammar and inline outcomes
    └── Integration/                # config, consent, provider errors, regression gates
```

**Structure Decision**: Keep the existing three-project split. Provider-neutral state and policies
belong in Core; Windows filesystem, ACL, process, and PowerShell integration belong in Windows; CLI
grammar, composition, persistence adapters, provider wire protocol, and rendering belong in Cli.
No new project, service process, server endpoint, or Feature 001 pipeline framework is introduced.

## Implementation Architecture

### End-to-end data flow

```text
PowerShell profile loader (capture opted in)
  → create independent session identity and protected staging/retained directories
  → Start-Transcript for current command staging
  → original/custom prompt wrapper reaches next safe boundary
  → close transcript + snapshot history/exit/interruption metadata
  → validate boundaries/session → finalize retained record → enforce 10,180,000-byte cap
  → start next staging transcript → render original prompt

tt last / tt ask last child process
  → resolve inherited current-session identity (never newest-file lookup)
  → retrieve last finalized record while excluding only contextual self-polluting TT commands
  → whole-output language eligibility (`tt last` only)
  → provider input selection and truncation-state calculation
  → consent fingerprint check → SecretDetector over exact outbound content
  → on-demand provider adapter → inline renderer

tt ask "question"
  → parse explicit question only → consent + SecretDetector → provider → inline renderer
```

### PowerShell integration lifecycle

- `tt configure --capture enabled` explains purpose, saves the opt-in, installs a versioned managed
  loader under `%LOCALAPPDATA%\TerminalTranslator\PowerShell`, and adds one uniquely marked dot-source
  block to the Windows PowerShell CurrentUser/CurrentHost profile. The edit is idempotent and keeps a
  recoverable original/profile state; it does not replace the whole profile.
- The same operation copies the single-file product binary into a content-addressed directory below
  `%LOCALAPPDATA%\TerminalTranslator\Versions`. The managed loader binds both its hidden capture
  bridge and a public global `tt` alias to that one installed path. It checks `Get-Command tt` first,
  preserves a pre-existing non-TT command with a warning, and never mutates User or Machine PATH.
  Upgrades publish a new immutable version and atomically replace the loader, so already-open shells
  remain internally consistent while fresh shells resolve the new binary.
- The loader runs after existing profile content, saves the existing `prompt` function, and installs
  a wrapper that always calls the saved prompt. Custom prompt text is never parsed to identify a
  boundary.
- A Feature 001 hosted-session guard prevents Feature 002 capture initialization inside `tt start`'s
  managed shell and prevents inherited Feature 002 identity from being reused there.
- `tt configure --capture disabled` persists disabled state, removes only the managed profile block,
  and causes an already-loaded wrapper to stop/delete its current capture at its next prompt. The
  in-memory wrapper then becomes a no-op that delegates to the original prompt.
- Normal exit uses a best-effort `PowerShell.Exiting` handler and at most one cleanup-only watcher per
  session, tied to the exact owner PID/process-start token. It starts after session initialization,
  uses bounded idle resources, has no network/provider access, never reads transcript content, exits
  after owner cleanup, and is never automatically restarted. Its failure leaves only stale residue
  for the next capture-related initialization. Disable/uninstall asks the exact watcher to exit or
  allows it to finish owner cleanup. Every TT initialization may run the metadata-only dead-owner
  sweep required by the spec, but question-only `tt ask` never opens capture content, resolves a
  session, or constructs a retriever/context request.
- Installation/update/uninstall owns the managed loader and marked block only. Uninstall removes
  those artifacts and capture/consent configuration within its documented scope, without rewriting
  unrelated profile content.
- Current provider options remain all-or-none when any provider option is supplied. Capture-only
  invocation is valid without provider options; legacy provider-only invocation remains valid;
  `--capture` and provider-setting options are mutually exclusive in one invocation. Capture changes
  stage and validate the loader, marked block, and preference before replacement; a write failure
  restores prior TT-owned profile state and capture preference.
- Profile edits use a protected same-directory transaction temporary file, not a retained full-profile
  backup. The temporary copy is deleted immediately after commit/rollback and is excluded from sync,
  logs, diagnostics, and support bundles; crash residue is removed by the next capture configuration.

### Session identity and association

- Generate a cryptographically random session GUID when the profile loader initializes capture in a
  PowerShell process. Bind it in the session manifest to the current Windows user SID, PowerShell
  PID, process-start identity, integration version, and random session nonce.
- Expose only the opaque identity/nonce to child `tt.exe` through dedicated environment variables.
  `tt.exe` validates the direct invoking process and manifest binding before opening any record.
- A separately initialized/nested supported shell gets a new GUID. A process lacking a valid
  integration identity fails closed for contextual commands. No path uses modification time or a
  global “latest” record.

### Command completion and boundary strategy

- The preceding prompt invocation opens the next command interval by starting a fresh transcript
  staging file and recording a sidecar-only opening boundary tied to session and sequence identity.
  Boundary metadata and transcript start/stop status are suppressed from user-visible output.
- The initial transcript interval starts with native joined-reader capture disabled. A preserved
  PSReadLine accepted-history hook uses a managed delegate to classify after PSReadLine accepts the
  line but before `ReadLine` returns for process creation. Direct
  capture-safe native commands enable the T133 joined stdout/stderr reader; TTY-sensitive/TUI,
  pager, remote-shell, interactive-runtime, unknown, and indirect invocations retain genuine console
  handles. Transcript rotation reapplies the last accepted classification so an immediately repeated
  command remains correct when PSReadLine suppresses its duplicate history callback.
- At the next prompt invocation, the wrapper snapshots `Get-History -Count 1`, `$?`, relevant
  `$LASTEXITCODE`, history execution status, and interruption evidence before running TT maintenance.
  For a command classified capture-safe, Windows PowerShell 5.1 native application I/O is routed
  through the engine's joined stdout/stderr reader path instead of its finite console-buffer scrape.
  TTY-sensitive applications bypass that capture mode and keep console stdin/stdout/stderr. At prompt,
  two synchronous pipeline/Transcript flushes must each observe empty producer queues and unchanged
  staging length before Stop-Transcript. Finalization proceeds only when history, transcript command
  echo, sequence, file lifecycle, producer completion, and session metadata agree.
- Command text comes from PowerShell session history/metadata, not prompt text. Multiline input is
  retained as one history entry. `$?` alone is not treated as proof of interruption.
- The currently executing `tt last`/`tt ask last` does not become finalized until the following
  prompt. Only those contextual self-polluting command records are ineligible targets; ordinary
  `tt ask` remains an ordinary strict previous command. Consecutive contextual requests therefore do
  not replace the true target.
- Content between transcript opening and the uniquely matched command echo is unattributed and
  excluded. Output after a prompt boundary is never retroactively assigned to the command that
  already completed. If it arrives while a later command is executing, V1 has no provenance tracker
  and treats it only as observed content inside that later command interval. `Clear-Host` changes
  presentation, not retained records.
- Any unmatched command echo, ambiguous transcript tail, inconsistent history, unexpected session
  switch, or unprovable Ctrl+C boundary marks the candidate unreliable and prevents contextual
  transmission.

### Bounded retained Transcript strategy

Use **per-command transcript staging followed by boundary-time immutable record publication**:

1. The active native transcript writes to a protected session staging path. It is not a retained
   command record and may exceed 10,180,000 bytes while the command runs.
2. At the safe boundary, require the joined native-reader invariant, two synchronous transcript
   flush/drain passes, and content stability; then stop the transcript and obtain an exclusive,
   stable file snapshot. Failure of any signal rejects the candidate. PowerShell 5.1 exposes no
   public native-transcription completion event, so the integration fails closed if the required
   engine capability or internal structural validation is unavailable.
3. Define `RetainedBytes` canonically as the sum of logical byte lengths for every record content and
   record metadata/index referenced by the single committed manifest generation, including that
   committed manifest. Active/unpublished candidates, transaction temporaries, old manifests, and
   unreferenced cleanup residue are staging/cleanup state and are not retained records or eligible for
   retrieval; filesystem allocation slack is not logical content. The limit does not count loader or
   configuration files.
4. If the candidate can fit individually, evict the oldest immutable complete records until the
   candidate plus remaining store is at most 10,180,000 bytes. Publish the new record and manifest
   generation atomically, then remove superseded files.
5. “Candidate alone” means the fully retained representation of validated extracted command output
   plus full command identity/boundary/termination metadata after older records are removed—not raw
   transcript headers, prompt artifacts, or staging bytes. If it is oversized, stream the validated
   ordered command output into a UTF-8-safe HEAD + TAIL payload. Reserve actual serialized metadata
   bytes, divide remaining payload bytes equally (odd byte to TAIL), move cuts inward to rune
   boundaries, and optionally trim to an earlier line boundary within each share. If measured output
   still exceeds the cap, remove bytes from the larger side, then TAIL on a tie, until it fits. Store
   the original extracted command-output UTF-8 byte count and `LocalCaptureTruncated`; never truncate
   essential command identity or present the bounded representation as complete.
6. Before the atomic committed-manifest replacement, failure leaves the old generation authoritative.
   After replacement, the new already-bounded generation is authoritative. In either phase, failure
   deleting any content-bearing artifact—raw stopped staging, unpublished/transactional candidate, or
   superseded generation—creates one ACL-protected, ineligible **finalization residue set** for that
   command/transaction. Capture immediately becomes unavailable; no new staging/finalize may begin
   until the entire set is removed. Cleanup retries at the next prompt, normal exit, and stale
   initialization, so residue sets cannot accumulate across commands. The set may contain one raw
   oversized staging file (whose size was allowed while active), bounded candidate artifacts, and at
   most one superseded generation. Readers validate only the committed manifest and never choose
   residue or generations by timestamps.
7. Start the next staging transcript only after finalize handling and confirmation that no residue
   set exists. A failed restart leaves capture
   unavailable but the original prompt and command behavior continue.

This satisfies the amended hard limit without a named-pipe transcript sink, live file rotation,
Console Buffer fallback, pipeline interception, or resident recorder. A command whose required
identity metadata cannot itself fit is not guessed or partially identified: the candidate is
discarded and contextual retrieval fails closed.

### Capture health and cleanup

- Health is session-scoped: `Starting`, `Healthy`, `Unavailable`, `Recovering`, or `Closing`, with a
  reason category that contains no captured content.
- The prompt wrapper catches all TT errors. The first transition to `Unavailable` prints one nearby
  notice; repeated prompts remain quiet. A successful, validated transcript start/finalize cycle may
  transition to `Healthy` and print one restoration notice. Health notices are printed while no
  command transcript is active and are explicitly integration output, never command output.
- Recovery from any content cleanup failure must delete the single finalization residue set before a
  new transcript interval can start. Repeated deletion failure keeps capture stopped and contextual
  commands fail closed.
- Retained and staging paths share current-user protected ACLs under LocalAppData. Random names and
  manifest binding prevent predictable cross-session selection. Capture content never enters the
  ordinary diagnostic sink.
- Normal exit deletes the exact session directory. Forced termination can leave staging, retained
  records, and an owner manifest; the next TT initialization deletes stores whose bound process
  identity is no longer live. Cleanup uncertainty never makes stale content eligible.

### Retrieval core and truncation states

One small provider-neutral retrieval capability accepts a validated current-session identity and
returns exactly one typed outcome: reliable snapshot, no previous command, no output, capture
disabled, capture unavailable, or unreliable/corrupt. A reliable snapshot includes command text,
ordered observed output, known exit/termination facts, interruption state, boundary confidence, and
local completeness (`Complete` or `LocalHeadTail`). Both `tt last` and `tt ask last` use this contract.

Provider selection then independently produces `AiInputComplete` or `AiHeadTail`. The renderer must
show the local-truncation notice for `LocalHeadTail`, the provider-input summary notice for
`AiHeadTail`, and both when both apply. See [previous-command-retrieval.md](./contracts/previous-command-retrieval.md).

### Existing pipeline reuse and isolation

Directly reuse:

- `ProviderSettings` and `ProviderSettingsStore` conventions;
- provider destination/configuration fingerprint concept and disclosure data;
- `SecretDetector` over the exact selected outbound request;
- normalized provider/translation error categories, credential resolution, timeout handling,
  redirect/cookie restrictions, response-size bounds, and content-free diagnostics.

Add one minimal shared safe chat-completion transport leaf only if needed to avoid duplicating those
wire-safety rules. Existing `ChatCompletionTranslationProvider` keeps its translation prompt,
8-KiB live-segment bound, and tests; the new on-demand adapter owns request-kind-specific messages.

Do not reuse or refactor into Feature 002:

- `ProductionTranslationPipeline`, `TranslationWorkQueue`, `TranslationWorker`, or live-session
  generation/cancellation;
- `VtTextExtractor`, `SubmittedCommandTracker`, or `EnglishCandidateClassifier` as the whole-output
  decision (its minimum length/word/CJK heuristics conflict with Feature 002);
- ConPTY relays, IPC pipes, `TranslationSession`, hosted-shell lifecycle, or companion renderer.

Feature 002 gets a simple whole-output English-natural-language eligibility decision with no minimum
length threshold. Feature 001 regression tests are a mandatory gate for any shared leaf extraction.

### Provider consent, privacy, and input budget

- Persist a non-content consent grant keyed by a canonical trust fingerprint: adapter/provider,
  destination URI, model, credential-source identity, disclosed content scope, and consent-policy
  version. Timeout and display-only settings do not force consent unless they change the trust
  boundary. `tt ask` uses a question-only scope; `tt last` and `tt ask last` share one disclosed
  previous-command terminal-context scope.
- Capture enablement is never provider consent. A changed trust fingerprint invalidates the grant.
- Build the exact request selection first, then run SecretDetector across every outbound field
  (question, command, selected output, exit/termination facts). A match sends nothing. The provider
  adapter performs a final gate assertion before serialization.
- V1 uses an adjustable provider-neutral **8,192 UTF-8-byte variable-input budget** because the
  current production adapter already enforces an 8-KiB source safety bound and the settings model
  exposes no trustworthy context-window capability. The selector uses the smaller of this policy and
  any future adapter-declared capability. This is Plan/provider policy, not product semantics.
- Fixed instruction overhead and response reserve are outside variable input but must be included in
  adapter capacity validation. Command text, question, output, and termination metadata all consume
  the variable budget. Required command/question metadata is allocated first; remaining output bytes
  are split equally between HEAD and TAIL with an odd byte assigned to TAIL, moved inward to valid
  UTF-8 rune boundaries, and optionally trimmed to an earlier line boundary within each share. When
  no line boundary exists, use the rune-safe cut. If required context cannot fit, fail closed rather
  than silently omit its meaning.

### T137 DeepSeek production request profile

- Extend the shared typed Chat Completions request DTO with `thinking` and an optional
  `response_format`; do not construct request JSON with string concatenation.
- Both the Feature 001 translation adapter and Feature 002 assistance adapter use the configured
  model and shared transport, so both explicitly send `thinking: {"type":"disabled"}`.
- All three assistance request kinds already require JSON in their system prompts and parse a JSON
  business schema from `message.content`; they additionally send
  `response_format: {"type":"json_object"}`. The live translation adapter expects plain translated
  text, so it omits `response_format` and retains its existing prompt unchanged.
- Keep `temperature: 0`; omit `reasoning_effort`, `max_tokens`, and `stream`; preserve the ten-second
  transport `CancelAfter`, infinite production assistance `HttpClient.Timeout`, no retry, response
  byte bound, and every T136 malformed-response diagnostic and schema check.

### CLI and inline rendering

- Add root `last` and `ask` commands through the existing command factory/composition pattern.
- Grammar is `tt last`, `tt ask <question...>`, and `tt ask last <question...>`. The parser joins the
  remaining argument tokens after System.CommandLine has handled quotes, preserving Unicode and
  embedded spaces. Empty questions are usage errors. `--last` is not an alias.
- `tt ask` never resolves session identity or opens Capture. Both ask forms are stateless. Default
  answer language is Simplified Chinese unless the explicit question requests another language.
- `tt last` uses a provider behavioral contract that translates output, does not translate command
  text, preserves technical tokens, and ends with a 1–2 line recommendation. `tt ask last` answers
  the question using context rather than forcing translation.
- Inline rendering is content-safe and never echoes the full original output. It prints `[翻译]` then
  `[建议]` for normal `tt last`; ask results print a direct answer. Capture/privacy/provider failure
  messages contain no captured text. Generated commands are rendered only as inert text.

## Testing Strategy

### Unit

- Retained accounting at 10,180,000 bytes, below/equal/above boundaries, oldest-complete eviction,
  atomic generation selection, oversized single-command HEAD + TAIL, UTF-8-safe slicing, and
  separate local/AI truncation states.
- Session GUID/PID/start-token/SID association, wrong-session rejection, sidecar-boundary/history
  parsing, multiline and custom-prompt independence, no-output/no-previous outcomes, contextual
  self-pollution exclusion, async boundary exclusion, inconsistent/corrupt metadata, and Ctrl+C
  confidence rules.
- Pre-execution native classification, preservation of an existing PSReadLine history handler,
  genuine stdin/stdout/stderr terminal handles for Codex/TUI probes, and joined-reader capture for
  capture-safe commands in the immediately following interval.
- Whole-output English eligibility for tiny English, Chinese-only, mixed language, and technical
  tokens; no provider call for Chinese-only output.
- Conditional configure parsing: capture-only, unchanged provider-only, rejected combined/partial
  invocations, and rollback after simulated profile/config write failures.
- Exact outbound selection SecretDetector coverage, persistent consent fingerprint invalidation,
  head/tail budget accounting, stateless ask/ask-last requests, and proof that AI text has no
  execution path.

### Integration

- Native transcript staging + metadata finalize, protected store ACL, atomic publication/failure,
  retention pressure, oversized staging, enable/disable/profile block reversibility, health
  unavailable/restored suppression, normal exit cleanup, and stale process cleanup.
- Commit-point fault permutations: incomplete candidate; complete candidate before manifest swap;
  manifest temporary write/flush; immediately after atomic swap; superseded deletion failure;
  next-transcript restart failure; and oversized staging deletion failure. Assert exactly one
  authoritative committed generation, logical retained bytes within cap, no partial retrieval, no
  strict-last skip after failed newest capture, and shell survival.
- Provider adapter request shapes, exact secret gate placement, consent persistence/config changes,
  timeouts/errors/oversized responses, inline rendering, and no-capture `tt ask` execution.
- Feature 001 regression gates: CLI contracts; `tt start/on/off/status`; session startup; production
  runtime journey; classifier ownership/raw-loss/teardown; ConPTY relay; pipe security; failure
  isolation; publish smoke.

### Real Windows Terminal + Windows PowerShell 5.1 acceptance

Run the [quickstart](./quickstart.md) in the real GUI topology, not only an agent PIPE harness. Cover
ordinary output, PowerShell error, native interleaved stdout/stderr, no output, no previous command,
multiline input, custom prompt, Ctrl+C, `Clear-Host`, under/over AI budget, one command over the local
retained cap, two independent panes, capture failure without shell damage, normal close cleanup, and
crash-residue cleanup. TUI screen reconstruction remains best effort, but launching a TTY-sensitive
native application must preserve its genuine stdin/stdout/stderr console handles.

## Remaining Technical Risks

- PowerShell 5.1 has no proven universal command-start hook. The accepted prompt-wrapper scheme is
  therefore limited to correctly installed, supported integration; ambiguous records fail closed.
- Ctrl+C status varies by command type. Automation can validate metadata rules, but final confidence
  requires real WT/PS5.1 tests; unreliable cases intentionally produce no contextual request.
- Transcript presentation includes headers/echo/prompt artifacts. Parser evolution must stay
  versioned and conservative; a new unknown format becomes unavailable rather than guessed.
- Profile files can be concurrently/user edited. Managed-block installation needs atomic replace,
  protected transaction temporary lifecycle, ownership markers, and conflict-safe uninstall tests.
- Boundary-time processing of an exceptionally large staging file must be time-bounded. Failure may
  lose that command's assistance record, but retained state remains valid and the shell remains
  usable as required.
- Local Windows PowerShell 5.1 feasibility probes confirmed three repeated Start/Stop-Transcript
  cycles retained PowerShell output/errors and native stdout/stderr while assignment suppressed the
  cmdlets' status text. Reflection over the installed 5.1 assembly confirmed transcript flush is
  dispatched by `Task.Run` and catches `IOException`/`UnauthorizedAccessException`, isolating those
  write failures from foreground command execution. A direct reflection fault/recovery probe also
  confirmed pending output remains buffered after `DirectoryNotFoundException`, is written after the
  path recovers, and is cleared only after success; installed IL places buffer clear after the
  `WriteLine` loop. Exclusive stable-snapshot acquisition after Stop is still required, otherwise the
  candidate fails closed. Real WT acceptance remains required for prompt topology, Ctrl+C, and
  user-visible failure behavior.

These are implementation/test risks, not decision-changing feasibility blockers. There are no new
Product Clarifications required.

## Complexity Tracking

No Constitution violations require justification.
