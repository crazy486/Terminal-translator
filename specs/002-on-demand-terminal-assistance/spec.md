# Feature Specification: On-Demand Terminal Assistance

**Feature Branch**: Not created (no `before_specify` hook configured)

**Created**: 2026-08-25

**Status**: Draft

**Input**: User description: "Add `tt last`, stateless `tt ask`, and `tt ask last` assistance for ordinary Windows Terminal + Windows PowerShell 5.1 sessions without requiring `tt start`."

**Architecture Constraint**: The Accepted `tt last` Capture Architecture ADR governs capture for
this feature. The sole production capture architecture is a bounded PowerShell Transcript plus
session and command metadata. This specification does not reopen that decision. Windows Console
Buffer / `CONOUT$` and other rejected alternatives are not fallbacks.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Translate the Previous Command On Demand (Priority: P1)

As a developer using an ordinary Windows PowerShell session, I run `tt last` after a command has
completed and receive an inline Chinese translation of that command's output followed by one short,
practical recommendation, without first entering `tt start`.

**Why this priority**: This is the primary user value: understanding terminal output at the moment
the user asks for help while preserving the user's normal shell workflow.

**Independent Test**: In Windows Terminal with Windows PowerShell 5.1 and capture enabled, run
ordinary successful and failing commands, then invoke `tt last`. Verify that the observed output is
translated inline, technical tokens remain recognizable, one short recommendation follows, and no
suggested command executes.

**Acceptance Scenarios**:

1. **Given** a completed command produced translatable English output, **When** the user runs
   `tt last`, **Then** the current PowerShell displays `[翻译]` with a Chinese translation and
   `[建议]` with one brief recommendation of about one or two lines.
2. **Given** a PowerShell command produced an error, **When** the user runs `tt last`, **Then** the
   error output is translated without changing paths, error codes, identifiers, or code fragments.
3. **Given** a native program produced interleaved standard output and standard error, **When** the
   user runs `tt last`, **Then** both are handled together in the order the user observed them.
4. **Given** the generated recommendation contains a shell command, **When** the result is shown,
   **Then** the command is displayed only and Terminal Translator does not execute it, modify files,
   run a fix, or confirm any action.
5. **Given** the previous output is already visible in terminal history, **When** `tt last` returns,
   **Then** it does not repeat the complete original output and does not open a companion pane.

---

### User Story 2 - Identify the Strict Previous Command (Priority: P1)

As a developer, I can trust that `last` means the previous completed command in this exact shell
session, including commands with no output, multiline commands, and interrupted commands, rather
than an older command that happens to have useful output.

**Why this priority**: A fluent translation of the wrong command is misleading and could expose
unintended content; reliable identity and boundaries are prerequisites for safe assistance.

**Independent Test**: Exercise normal, empty-output, multiline, custom-prompt, interrupted,
`Clear-Host`, and two-session cases. Verify that each request selects only the strict previous
completed command or fails closed with an accurate explanation.

**Acceptance Scenarios**:

1. **Given** `git status` produced output and the next completed command was `cd ..` with no output,
   **When** the user runs `tt last`, **Then** the tool reports that the previous command has no
   translatable output and does not translate `git status`.
2. **Given** no command precedes `tt last` in the current session, **When** the user invokes it,
   **Then** it reports exactly `No previous command output is available.` and sends nothing to a
   provider.
3. **Given** the previous command spans multiple input lines or continuation prompts, **When** the
   user runs `tt last`, **Then** the complete logical command and its bounded output are associated
   as one completed command.
4. **Given** the user has configured a custom prompt, **When** the user runs `tt last`, **Then** the
   previous command is identified without depending on a fixed `PS ...>` prompt pattern.
5. **Given** a command was interrupted with Ctrl+C and its produced output and termination boundary
   are reliable, **When** the user runs `tt last`, **Then** output captured through interruption is
   eligible for translation and the assistance context identifies the command as interrupted.
6. **Given** the user ran a command, then `Clear-Host`, **When** the user runs `tt last`, **Then**
   `Clear-Host` remains the strict previous command; if it produced no translatable output, the tool
   says so rather than falling back to content that existed before the screen was cleared.
7. **Given** two PowerShell sessions have independent histories, **When** `tt last` is invoked in
   either session, **Then** only that exact session's previous completed command is considered,
   never a record selected because it was modified most recently.
8. **Given** `tt last` is executing, **When** the previous-command boundary is evaluated, **Then**
   the `tt last` command and its own echo/output do not replace or contaminate the true previous
   command.
9. **Given** output arrives from a background job after the previous command's completion boundary,
   **When** the user runs `tt last`, **Then** that later output is not automatically attributed to
   the previous command.

---

### User Story 3 - Handle Long and Mixed-Language Output Honestly (Priority: P2)

As a developer, I receive useful assistance for short, long, and mixed Chinese/English output while
being told whenever the model saw only a selected portion of an oversized result.

**Why this priority**: Long build logs and mixed-language diagnostics are common, and the result must
not imply completeness that the provider did not receive.

**Independent Test**: Use English, Chinese-only, mixed-language, below-limit, and above-limit
outputs. Verify the selection, notification, preservation, and provider-call behavior for each.

**Acceptance Scenarios**:

1. **Given** the previous output contains any English natural language worth translating, even if
   brief, **When** the user runs `tt last`, **Then** it is eligible for translation without a minimum
   content-length threshold.
2. **Given** the previous output is mainly Chinese and contains no English that needs translation,
   **When** the user runs `tt last`, **Then** it reports `No translatable English content was found.`
   and does not contact the provider.
3. **Given** the previous output mixes Chinese and English, **When** it is translated, **Then** the
   selected output may be used as a whole, existing Chinese remains unchanged, English natural
   language becomes Chinese, and technical tokens remain as originally shown.
4. **Given** the previous output fits within the applicable AI input limit, **When** the user runs
   `tt last`, **Then** the complete previous-command output is eligible for transmission after the
   privacy gate.
5. **Given** the previous output exceeds the applicable AI input limit, **When** the user runs
   `tt last`, **Then** only a head and tail selection is eligible for transmission after the privacy
   gate, the model produces a summarized translation, and the UI clearly states that the result is
   based on the beginning and end rather than a complete line-by-line translation.
6. **Given** one completed command by itself exceeds the 10,180,000-byte retained Capture Store
   limit, **When** it is finalized at a safe command/prompt boundary, **Then** it is retained as a
   bounded HEAD + TAIL representation with trustworthy command identity and boundaries, and
   `tt last` and `tt ask last` clearly state that the local Capture itself was truncated.
7. **Given** local capture truncation and AI input truncation are observed separately or together,
   **When** contextual assistance is rendered, **Then** each applicable state has a distinct notice
   and the product never implies that locally discarded content still exists or that the provider
   received content it did not see.

---

### User Story 4 - Control a Private, Session-Bounded Capture (Priority: P1)

As a developer, I choose once whether on-demand capture is enabled, can keep using my shell if
capture fails, and know that captured terminal content stays local, bounded, and tied to the living
session that produced it.

**Why this priority**: On-demand assistance depends on local capture, but capture must remain
purpose-limited, user-controlled, and unable to break the shell.

**Independent Test**: Exercise opt-in, disabled capture, capacity pressure, two live sessions,
normal shutdown, simulated crash residue, capture failure/recovery, and corrupt boundaries. Verify
lifecycle, access, messages, and fail-open/fail-closed behavior.

**Acceptance Scenarios**:

1. **Given** Terminal Translator installation or initialization is configuring capture for the first
   time, **When** the user reviews the purpose and chooses to enable it, **Then** future ordinary
   PowerShell sessions use capture without asking again on every shell start.
2. **Given** the user declined or later disabled capture, **When** `tt last` is invoked, **Then** it
   reports `tt last capture is disabled.` and `Enable it to capture future command output.`, and
   uses no hidden fallback.
3. **Given** a session's completed-command retained Capture Store approaches its exact
   10,180,000-byte hard limit, **When** another command completes at a safe command/prompt boundary,
   **Then** the store is promptly restored to the limit by evicting the oldest complete records as
   needed, the newest complete record is preferred, no ordinary complete record is cut through its
   boundary, and the shell continues normally.
4. **Given** the current command is still running, **When** its native PowerShell Transcript staging
   file grows beyond 10,180,000 bytes, **Then** this temporary growth does not violate the retained
   Capture Store limit and does not interrupt or alter the command; retention is enforced promptly
   after the command reaches a safe command/prompt boundary.
5. **Given** a PowerShell session ends normally, **When** cleanup completes, **Then** that session's
   Capture Store is automatically deleted and cannot be used by a later session.
6. **Given** a crash or forced termination left stale capture, **When** Terminal Translator next
   initializes, **Then** stale capture is removed before normal operation and is not exposed as a
   later session's previous command.
7. **Given** capture becomes unavailable while a command runs, **When** the command completes,
   **Then** its execution, exit code, standard input, standard output, and PowerShell session remain
   unaffected, while one timely nearby notice says `[tt] Capture unavailable. `tt last` will not
   work until capture recovers.` without repeating at every prompt.
8. **Given** capture becomes healthy after an outage, **When** reliable capture resumes, **Then** the
   user may receive one `[tt] Capture restored.` notice.
9. **Given** capture is corrupt, its boundary is missing, metadata is inconsistent, session
   association is wrong or unknown, or previous output cannot be recovered reliably, **When**
   `tt last` is invoked, **Then** it reports `[tt] Previous command output could not be recovered
   reliably.`, sends no guessed text, and continues to leave the shell usable.
10. **Given** capture is active and the user launches a TTY-sensitive native application such as
    Codex, **When** that process is created, **Then** stdin, stdout, and stderr remain genuine terminal
    handles and the application launches normally, while later capture-safe native commands retain
    reliable joined-reader capture.

---

### User Story 5 - Ask a One-Time AI Question (Priority: P2)

As a developer, I run `tt ask "question"` for a one-time AI answer without silently attaching
terminal history or creating a conversation.

**Why this priority**: Users need general terminal-oriented assistance even when the answer does not
depend on the previous command.

**Independent Test**: Invoke `tt ask` with a distinctive question while capture contains unrelated
terminal content. Verify only the supplied question is eligible for transmission, no prior context
appears in the answer request, no history is retained, and suggested commands remain text.

**Acceptance Scenarios**:

1. **Given** the user enters `tt ask "问题文本"`, **When** the request is sent, **Then** only the
   explicitly entered question is sent and no previous command, command output, Capture Store
   content, or conversation history is included.
2. **Given** one `tt ask` request has completed, **When** the user makes another, **Then** the second
   request has no conversation memory from the first.
3. **Given** the AI answer suggests a shell command, **When** the answer is displayed inline,
   **Then** Terminal Translator never executes, confirms, or applies it.
4. **Given** `tt ask` is used without an explicit language request, **When** the answer is presented,
   **Then** it answers in Simplified Chinese; if the question explicitly requests another language,
   it answers in that explicitly requested language.

---

### User Story 6 - Ask About the Previous Command (Priority: P2)

As a developer, I run `tt ask last "question"` to ask a focused, one-time question using the strict
previous completed command and its output as context.

**Why this priority**: A targeted question can explain causes and next steps beyond the standard
translation and short recommendation while retaining the same capture and privacy protections.

**Independent Test**: Run a command that produces a diagnostic, then invoke `tt ask last` with a
focused question. Verify the exact previous context and question are used statelessly, and repeat
capture-disabled, secret, corrupt-boundary, no-output, and no-previous-command cases.

**Acceptance Scenarios**:

1. **Given** a reliable previous completed command and output exist, **When** the user enters
   `tt ask last "这个报错为什么发生？"`, **Then** the one-time request uses the command text, output,
   relevant exit or termination metadata, and explicit question as context.
2. **Given** the contextual request completes, **When** a later `tt ask last` request starts,
   **Then** no conversation history from the earlier request is included.
3. **Given** capture is disabled or unavailable, no previous command exists, the strict previous
   command has no usable output, or its session/boundary is unreliable, **When** `tt ask last` is
   invoked, **Then** it follows the same explicit, fail-closed capture behavior as `tt last` and
   sends no guessed context.
4. **Given** the previous output exceeds the applicable AI input limit, **When** `tt ask last` is
   invoked, **Then** its contextual output is limited and disclosed using the same head-and-tail
   policy as `tt last`.

---

### User Story 7 - Prevent Unauthorized External Disclosure (Priority: P1)

As a privacy-conscious developer, I know that local capture is not permission to upload content and
that every external AI request is screened before the provider can see it.

**Why this priority**: Terminal output may contain credentials or private data; provider disclosure
must remain an explicit and independently protected trust boundary.

**Independent Test**: Populate local capture and explicit questions with representative suspected
secrets, exercise consent and provider changes, and inspect provider-bound requests. Verify zero
protected content is transmitted and every blocked request produces a clear notice.

**Acceptance Scenarios**:

1. **Given** selected command or output content contains a suspected secret, **When** `tt last` or
   `tt ask last` prepares an external request, **Then** the protected content is blocked before the
   provider sees it and the user is told that suspected sensitive information was not sent.
2. **Given** a head-and-tail selection is used for oversized output, **When** it is prepared for
   transmission, **Then** the complete selected head and tail pass the same privacy gate before any
   provider request.
3. **Given** a `tt ask` question itself contains a suspected secret, **When** the question is prepared
   for external transmission, **Then** it is blocked before provider disclosure and the user receives
   the same clear privacy outcome.
4. **Given** capture content exists locally, **When** the user merely runs ordinary commands or does
   not explicitly request AI assistance, **Then** no capture content is automatically uploaded,
   synchronized, logged, diagnosed, or emitted as telemetry.
5. **Given** the user has consented for an unchanged provider, destination, and relevant transmission
   configuration, **When** the user explicitly invokes a later eligible `tt last`, `tt ask`, or
   `tt ask last` request, **Then** the recorded consent remains applicable.
6. **Given** a provider, destination, or relevant transmission setting changes the trust boundary,
   **When** a later request would transmit content, **Then** the user sees the new destination and
   content scope and must consent again before transmission.

### Edge Cases

- Empty-output commands remain the strict previous command and never cause an older result to be
  substituted.
- A command that outputs only whitespace, terminal control effects, or non-translatable technical
  tokens is treated as having no translatable output.
- Command text is context only and is never itself translated by `tt last`.
- Filesystem paths, URLs, shell commands, switches, error codes, identifiers, code fragments, and
  package or module names remain recognizable across translation and summaries.
- If an interrupted command lacks a reliable completion or termination boundary, assistance fails
  closed rather than guessing; reliably bounded partial output remains eligible.
- Output produced after prompt return by a background job or asynchronous source is not reassigned
  to the preceding command in V1.
- Capacity pressure cannot block command execution or cause a partial record to masquerade as a
  reliable previous command.
- Ordinary text commands that complete and return to a PowerShell prompt are supported; complete
  reconstruction of full-screen, continuously redrawing, REPL, or interactive TUI state is not
  promised.
- If provider service, network access, or AI response generation fails, the failure is explained
  without changing shell state or executing any suggested recovery action.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The product MUST provide `tt last` inline in an ordinary Windows Terminal + Windows
  PowerShell 5.1 session without requiring the user to start the Feature 001 hosted live mode.
- **FR-002**: `last` MUST mean the previous completed command in the exact current PowerShell
  session, including empty-output commands; it MUST NOT search backward for an older command with
  translatable output or cross session boundaries.
- **FR-003**: Previous-command identification MUST reliably distinguish session identity, command
  identity, completion, output boundaries, multiline input, custom prompts, and the self-output of
  `tt last` and `tt ask last`; a fixed prompt-text pattern alone is insufficient.
- **FR-004**: Capture for this feature MUST use the Accepted ADR's sole production architecture:
  bounded PowerShell Transcript plus session and command metadata. Console Buffer / `CONOUT$` and
  other rejected architectures MUST NOT be used as fallback.
- **FR-005**: Installation or initialization MUST explain capture's purpose and request an enablement
  choice; after opt-in, capture MUST operate automatically in future supported PowerShell sessions
  without repeated per-session prompts, and the user MUST be able to disable it.
- **FR-006**: With capture disabled, `tt last` and `tt ask last` MUST explain that capture is disabled
  and can only capture future output after enablement; no hidden fallback is permitted.
- **FR-007**: Each live shell session MUST have an unambiguous, independent retained Capture Store
  for completed commands with an exact hard maximum of 10,180,000 bytes. An active native
  PowerShell Transcript staging file MAY temporarily exceed that value while its current command is
  running. At the next safe command/prompt boundary, Terminal Translator MUST promptly restore the
  retained store to no more than the exact limit by discarding the oldest complete command records
  as needed, preferring the newest complete record, and preserving ordinary record boundaries.
- **FR-008**: A session's Capture Store MUST be deleted automatically when that session ends
  normally. Stale capture left by abnormal termination MUST be cleaned at the next Terminal
  Translator initialization and MUST never supply cross-session `last` results.
- **FR-009**: Local capture MUST be held only for the current user within local application data,
  protected by current-user-only access, and MUST NOT be placed in obvious user-content sync
  locations, automatically synchronized, uploaded, included in ordinary telemetry or application
  logs, or automatically included in diagnostic or support bundles.
- **FR-010**: The product MUST NOT provide a user-facing command or workflow to show or export raw
  Capture Store contents.
- **FR-011**: Local capture MAY retain terminal-produced content as observed, including potentially
  sensitive information. Local retention MUST remain a separate authorization boundary from any
  external transmission.
- **FR-012**: Capture, retention, or compaction failure MUST be fail-open for the shell: it MUST NOT
  prevent commands, alter exit codes, block normal input/output, or terminate the PowerShell
  session. The product MUST issue
  one timely unavailability notice without prompt-by-prompt repetition and MAY issue one restoration
  notice after reliable recovery.
- **FR-013**: Missing or inconsistent boundaries or metadata, corruption, failed retention or
  compaction, unknown or wrong session association, and unrecoverable previous output MUST make
  `tt last` and `tt ask last` fail closed with a clear explanation and no provider transmission.
- **FR-014**: When a Ctrl+C-interrupted command has reliable output and termination boundaries, its
  captured output MUST be eligible and its interrupted state MUST be included in AI context; without
  reliable boundaries, contextual assistance MUST fail closed.
- **FR-015**: V1 MUST attribute only output through a command's completion boundary to that command;
  background, job, or asynchronous output appearing after prompt return MUST NOT be automatically
  reassigned to it.
- **FR-016**: `Clear-Host` MUST affect terminal presentation only, not erase otherwise valid Capture
  Store history. It remains an ordinary command under strict previous-command semantics.
- **FR-017**: `tt last` MAY send the previous command text, its output, and relevant exit or
  termination metadata as context. It MUST translate English natural language in the output into
  Chinese without translating the command text, while preserving existing Chinese and technical
  tokens as far as practical.
- **FR-018**: Standard output and standard error MUST be handled together in their captured observed
  order rather than exposed as separate translation sections.
- **FR-019**: Any amount of translatable English output MUST be eligible without a minimum-length
  threshold. Chinese-only output with no translatable English MUST produce an explicit no-English
  result without contacting a provider.
- **FR-020**: The Capture Store capacity and AI input limit MUST be treated as separate limits. The
  specific AI input limit is deferred to planning and provider policy and MUST NOT be fixed by this
  specification.
- **FR-021**: When local Capture is complete, output within the applicable AI input limit MUST be
  eligible in full. Complete local output over the AI input limit MUST use only a head-and-tail
  selection, request a summarized translation, and clearly tell the user that provider input used
  only the beginning and end and is not a complete line-by-line translation.
- **FR-022**: `tt last` MUST print inline under `[翻译]` followed immediately by `[建议]` with one
  short recommendation of about one or two lines. It MUST NOT open a companion pane or repeat the
  complete original output.
- **FR-023**: The product MUST provide `tt ask "问题文本"` as a stateless, one-time request that sends
  only the explicit question and does not read previous commands, output, capture, or conversation
  history.
- **FR-024**: `tt ask` MUST answer in Simplified Chinese by default. If the user's question explicitly
  requests another language, the answer MUST use that explicitly requested language.
- **FR-025**: The product MUST provide the exact syntax `tt ask last "问题文本"` as a stateless,
  one-time contextual request using the strict current-session previous command text, output,
  relevant exit or termination metadata, and explicit question.
- **FR-026**: `tt ask last` MUST apply the same capture availability, session identity, boundary
  integrity, input-size disclosure, privacy gate, and fail-closed behaviors as `tt last`.
- **FR-027**: Before any `tt last`, `tt ask`, or `tt ask last` content is transmitted externally,
  the entire intended transmission MUST pass the SecretDetector/privacy gate. Suspected protected
  content MUST NOT be disclosed in whole or part to the provider, and the user MUST receive a clear
  notice that suspected sensitive content was not sent.
- **FR-028**: Explicit capture enablement MUST NOT count as external-transmission consent. Provider,
  destination, and content scope MUST be disclosed before first transmission; consent MAY continue
  across later explicit requests while the relevant trust-boundary configuration is unchanged and
  MUST be renewed after a trust-boundary-affecting change.
- **FR-029**: AI-generated commands, snippets, fixes, file changes, confirmations, and destructive
  actions MUST be displayed only. Terminal Translator MUST never execute or apply them.
- **FR-030**: V1 compatibility MUST cover Windows Terminal with Windows PowerShell 5.1 Desktop.
  PowerShell 7, cmd.exe, bash/WSL, third-party terminals, legacy ConsoleHost-only environments,
  Linux, and macOS MUST NOT be represented as part of this feature's acceptance contract.
- **FR-031**: Existing `tt start`, `tt on`, `tt off`, and `tt status` live-translation behavior MUST
  continue to exist without requiring redesign. `tt start` MAY continue using its companion pane;
  this feature's commands use the current ordinary shell inline.
- **FR-032**: If one completed command by itself exceeds the 10,180,000-byte retained Capture Store
  limit, Terminal Translator MUST convert it at a safe command/prompt boundary into a bounded
  HEAD + TAIL representation whose command identity and boundaries remain trustworthy, mark it as
  locally truncated, keep the total retained store within the limit, and disclose that local
  truncation to `tt last` and `tt ask last` users without implying complete output is retained.
- **FR-033**: Local capture truncation and AI input truncation MUST remain distinct states with
  distinct user notices. If both apply to one assistance request, both states MUST be preserved and
  disclosed.
- **FR-034**: Capture integration MUST decide before native process creation whether a command is
  capture-safe or TTY-sensitive. TTY-sensitive/interactive native applications MUST inherit genuine
  terminal stdin/stdout/stderr handles; capture-safe native commands MUST retain T133's reliable
  joined-reader completion behavior. The decision MUST NOT use arbitrary delays or globally disable
  the completion invariant.
- **FR-035**: DeepSeek Chat Completions requests for Terminal Translator's latency-sensitive
  translation and brief-assistance workloads MUST explicitly disable thinking. Requests whose
  provider adapter contract requires structured JSON MUST also select API JSON Output while keeping
  the prompt's explicit JSON instruction. Plain-text translation requests MUST NOT be relabeled as a
  JSON contract. This profile change MUST NOT add a generation-token limit, streaming, retry, or a
  second timeout authority.

### Scope Boundaries

**In scope**:

- On-demand translation and a short recommendation for the strict previous completed command in the
  current supported PowerShell session.
- One-time stateless questions with and without reliable previous-command context.
- User-controlled, session-bounded capture with exact capacity, cleanup, failure isolation, and
  local privacy constraints.
- External transmission consent, pre-provider secret screening, long-output disclosure, and inline
  terminal results.

**Out of scope**:

- Console Buffer / `CONOUT$` fallback; `tt run`; `Out-Default`; Tee or pipeline interception;
  resident recorder alternatives; or reopening the Accepted Capture Architecture.
- `tt chat`, conversation history, cross-session `tt last`, persistent terminal-history products,
  or user-facing capture display/export.
- PowerShell 7, cmd.exe, bash/WSL, third-party terminals, legacy ConsoleHost-only environments,
  Linux, or macOS compatibility promises.
- Complete capture/reconstruction fidelity for Codex interactive TUI, Vim, full-screen applications,
  REPLs, continuously redrawing programs, or arbitrary ANSI screen state. Their genuine console-handle
  semantics are nevertheless in scope and must not be broken by capture.
- Automatic execution of generated commands or changes, redesign of Feature 001 live mode,
  speculative shared-pipeline refactoring, or new provider models solely for this feature.

### Privacy and Trust Boundaries

- **Local capture**: Explicitly enabled, purpose-limited to current-session on-demand assistance,
  current-user-only, and local. Completed-command retained records are bounded to exactly
  10,180,000 bytes per session; an active Transcript staging file may temporarily exceed that value
  until the next safe boundary. Capture is deleted at normal session end and stale-cleaned after
  abnormal termination. It may contain terminal content in its original sensitive form but is not
  authority for any secondary use.
- **External transmission**: Occurs only after an explicit assistance request, applicable consent,
  and pre-provider privacy screening. It includes only the question and/or previous-command context
  required by that request, subject to honest long-output selection and disclosure.
- **Explicitly excluded secondary uses**: Local capture does not enter normal logs, telemetry,
  synchronization, automatic upload, diagnostics, support bundles, or user-facing export.
- **Threat boundary**: V1 does not claim to protect local capture against malware already operating
  with the current Windows user's authority, Administrator, SYSTEM, or an otherwise compromised
  host.

### Key Entities *(include if feature involves data)*

- **PowerShell Session**: One currently living supported shell with an independent identity and no
  authority to read another session's previous command.
- **Command Record**: One completed command's text, bounded observed output, relevant completion,
  exit, or interruption facts, and any local-capture-truncation state; ordinary records are retained
  and evicted only as complete logical records, while one individually oversized command may retain
  a boundary-safe HEAD + TAIL representation.
- **Capture Store**: Purpose-limited local records for one live session, with user choice, exact
  capacity, current-user access, and cleanup lifecycle.
- **Assistance Request**: One explicit stateless `tt last`, `tt ask`, or `tt ask last` operation with
  a defined content scope, destination consent state, privacy-gate outcome, and optional long-output
  disclosure.
- **Assistance Result**: Inline AI-generated translation, answer, summary, or recommendation that is
  informational only and never grants execution authority.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In 100% of acceptance tests for normal output, PowerShell errors, native mixed
  stdout/stderr, empty-output commands, no previous command, multiline input, custom prompts,
  Ctrl+C, `Clear-Host`, and self-pollution, the product selects the strict previous completed command
  or returns the specified fail-closed outcome; it never substitutes an older command.
- **SC-002**: Across two or more simultaneously active supported PowerShell sessions, 100% of
  `tt last` and `tt ask last` tests use only the invoking session's records, including when another
  session's capture was modified more recently.
- **SC-003**: At every tested safe command/prompt boundary, each session's completed-command retained
  Capture Store is at or below exactly 10,180,000 bytes, all retained command boundaries remain
  valid, newest complete records remain available when individually able to fit, an individually
  oversized command is represented by marked boundary-safe HEAD + TAIL content, and shell command
  outcomes are unchanged even when active staging temporarily grows beyond the limit.
- **SC-004**: In 100% of tested normal session endings, that session's capture is no longer available;
  in 100% of tested crash-residue cases, stale capture is removed at the next initialization and is
  never returned to a later session.
- **SC-005**: In 100% of capture outage, recovery, corruption, incomplete-boundary, and inconsistent-
  metadata tests, the shell remains usable and preserves command output and exit behavior, while
  contextual assistance either uses reliable records or sends no content.
- **SC-006**: In representative English, Chinese-only, and mixed-language tests, 100% of existing
  Chinese and protected technical tokens remain recognizable, and Chinese-only cases produce no
  provider request.
- **SC-007**: In 100% of below-AI-limit tests with complete local capture, complete selected output is
  eligible for the privacy gate. In 100% of local-capture-truncation and AI-input-truncation tests,
  only the applicable head-and-tail context is eligible, each condition has a distinct visible
  notice, and both notices appear when both conditions apply.
- **SC-008**: Across representative suspected secrets in commands, outputs, oversized head-and-tail
  selections, and explicit questions, zero protected content reaches the provider and every blocked
  request displays a clear privacy notice.
- **SC-009**: In consent tests, zero terminal-derived or question content is externally transmitted
  before applicable consent, and 100% of trust-boundary-affecting provider or configuration changes
  require renewed consent.
- **SC-010**: At least 90% of representative users can enable capture during setup, use `tt last`,
  distinguish translation from the short recommendation, and understand a disabled, unavailable,
  or truncated-context notice on first attempt without documentation.
- **SC-011**: In 100% of `tt ask` and `tt ask last` tests, each request is stateless, and `tt ask`
  includes no capture or previous-command content unless the user chose the `last` subcommand.
- **SC-012**: Across all generated-command acceptance tests, zero AI-generated commands, fixes,
  confirmations, or file changes are automatically executed or applied.
- **SC-013**: In the permanent TTY probe and installed Codex acceptance, stdin, stdout, and stderr are
  terminals and the TUI starts; in the immediately following capture-safe native cases, stdout/stderr,
  exit status, and file-only redirection retain their specified behavior.

## Assumptions

- The Accepted Capture Architecture ADR is final for this feature; planning may design its details
  but may not select another capture source or fallback.
- Supported users run Windows Terminal with Windows PowerShell 5.1 Desktop and allow the lightweight
  PowerShell integration needed after capture opt-in.
- Simplified Chinese is the `tt last` translation target, consistent with Feature 001, and the
  default answer language for `tt ask`; an explicit language request in a `tt ask` question overrides
  that default for that request only.
- The applicable AI input limit and provider-specific context capacity will be established during
  planning or provider policy. Regardless of its value, the full-versus-head-and-tail behavior in
  this specification remains binding.
- External provider/network availability is not guaranteed; assistance failure is non-destructive
  and never changes the authoritative shell command outcome.
- Original terminal output remains authoritative. Translation and advice are assistive text.
- V1 targets ordinary completed commands that return control to the PowerShell prompt; no complete
  TUI or asynchronous provenance guarantee is implied.
