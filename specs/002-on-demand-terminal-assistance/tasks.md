# Tasks: On-Demand Terminal Assistance

**Input**: Design documents in `specs/002-on-demand-terminal-assistance/` and the Accepted capture ADR  
**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`

**Tests**: Decision-critical behavior is specified test-first. Each production task follows the nearest relevant test task unless an explicit dependency says otherwise.

**Organization**: Tasks are grouped by phase and user story. Paths match the existing Core / Windows / CLI repository split; new directories are only those approved by `plan.md`.

## Format: `[ID] [P?] [Story?] Description with path`

- **[P]**: Can run in parallel with other `[P]` tasks in the same phase after that phase's prerequisites are complete.
- **[USn]**: Maps the task to a user story in `spec.md`.
- Every task names the exact file or directory it changes.

---

## Phase 1: Setup / Shared Test Prerequisites

**Purpose**: Establish deterministic, content-safe fixtures and test doubles used by later capture and assistance tests.

- [X] T001 Add deterministic normal, empty, multiline, mixed-stream, corrupt-boundary, and oversized transcript fixtures in `tests/TerminalTranslator.Core.Tests/Fixtures/CaptureTranscriptCorpus.cs`
- [X] T002 [P] Add fault-injectable filesystem, process-identity, transcript, and prompt-host test doubles in `tests/TerminalTranslator.Windows.Tests/TestDoubles/CaptureTestDoubles.cs`
- [X] T003 [P] Add recording/reject-if-called assistance provider, consent, privacy-gate, retriever, and capture-access test doubles in `tests/TerminalTranslator.Cli.Tests/TestDoubles/AssistanceTestDoubles.cs`
- [X] T004 [P] Add a non-production Windows PowerShell 5.1 probe for repeated transcript cycles, mixed native streams, custom prompts, and exit-state observation in `tests/TerminalTranslator.Windows.Tests/Fixtures/PowerShellCaptureProbe/Invoke-CaptureProbe.ps1`

**Checkpoint**: Later tests can model output and faults without using real credentials or user profile/capture directories.

---

## Phase 2: Foundational Capture Infrastructure

**Purpose**: Build the minimum trusted capture/session/retention/retrieval substrate required by both contextual commands.

**Critical dependency**: No contextual user-story implementation begins until this phase passes.

- [X] T005 [P] Add invariant tests for capture session, command record, boundary, health, retention, snapshot, and distinct local/AI completeness states in `tests/TerminalTranslator.Core.Tests/Unit/CaptureModelsTests.cs`
- [X] T006 Implement provider-neutral capture domain values and exclusive retrieval outcomes in `src/TerminalTranslator.Core/Capture/CaptureModels.cs`
- [X] T007 [P] Add tests for GUID/nonce generation and SID/PID/process-start/integration-version binding, including PID reuse and nested-session rejection, in `tests/TerminalTranslator.Windows.Tests/Unit/CaptureSessionIdentityTests.cs`
- [X] T008 Implement session proof creation, environment propagation, and direct invoking-process validation without file-recency lookup in `src/TerminalTranslator.Windows/Capture/CaptureSessionIdentity.cs`
- [X] T009 [P] Add LocalAppData path and protected current-user DACL tests, including rejection of broad user access and user-document paths, in `tests/TerminalTranslator.Windows.Tests/Integration/CaptureStorageSecurityTests.cs`
- [X] T010 Implement random per-session storage authority and protected ACL creation below `%LOCALAPPDATA%\TerminalTranslator\Capture` in `src/TerminalTranslator.Windows/Capture/ProtectedCaptureStorage.cs`
- [X] T011 [P] Add atomic save/load/default tests for the independent enabled/disabled capture preference in `tests/TerminalTranslator.Cli.Tests/Configuration/CapturePreferenceStoreTests.cs`
- [X] T012 Implement non-content capture preference persistence using existing LocalAppData conventions in `src/TerminalTranslator.Cli/Configuration/CapturePreferenceStore.cs`
- [X] T013 [P] Add idempotency, concurrent-edit detection, rollback, protected temporary-file cleanup, and unrelated-profile preservation tests in `tests/TerminalTranslator.Windows.Tests/Integration/PowerShellProfileInstallerTests.cs`
- [X] T014 Implement transactional insertion/removal of the uniquely marked CurrentUser/CurrentHost profile block in `src/TerminalTranslator.Windows/PowerShell/PowerShellProfileInstaller.cs`
- [X] T015 [P] Add managed-loader and hidden maintenance-bridge contract tests for versioning, LocalAppData placement, hosted-session guard, original prompt delegation, internal-only visibility, and no fixed prompt regex in `tests/TerminalTranslator.Windows.Tests/Unit/PowerShellLoaderContractTests.cs` and `tests/TerminalTranslator.Cli.Tests/Contract/CaptureIntegrationCommandContractTests.cs`
- [X] T016 Implement managed loader installation/update validation, hosted Feature 001 session exclusion, and the hidden shell-maintenance bridge used by the managed script in `src/TerminalTranslator.Windows/PowerShell/PowerShellIntegrationInstaller.cs`, `src/TerminalTranslator.Cli/Commands/CaptureIntegrationCommand.cs`, and `src/TerminalTranslator.Cli/Commands/CommandFactory.cs`
- [X] T017 Add isolated integration tests for repeated `Start-Transcript`/`Stop-Transcript` intervals, suppressed status text, PowerShell errors, and native stdout/stderr order in `tests/TerminalTranslator.Windows.Tests/Integration/TranscriptStagingLifecycleTests.cs`
- [X] T018 Implement protected per-command transcript staging start/stop/flush state with shell-safe error containment in `src/TerminalTranslator.Windows/Capture/TranscriptStagingController.cs`
- [X] T019 Add prompt-wrapper integration tests that snapshot history and automatic variables before maintenance while preserving custom prompt and multiline/continuation input in `tests/TerminalTranslator.Windows.Tests/Integration/PowerShellPromptWrapperTests.cs`
- [X] T020 Implement the failure-catching command-interval/prompt wrapper and original prompt delegation in `src/TerminalTranslator.Windows/PowerShell/TerminalTranslator.Profile.ps1`
- [X] T021 Add tests for session/sequence opening and closing evidence, command history identity, transcript echo matching, exit facts, and ambiguous/missing boundaries in `tests/TerminalTranslator.Core.Tests/Unit/CommandBoundaryValidatorTests.cs`
- [X] T022 Implement sidecar boundary validation and versioned command-interval extraction in `src/TerminalTranslator.Core/Capture/CommandBoundaryValidator.cs`
- [X] T023 Add bounded-wait stable-snapshot tests for exclusive access, stable length/hash, timeout, mutation, and inaccessible staging files in `tests/TerminalTranslator.Windows.Tests/Unit/StableTranscriptSnapshotTests.cs`
- [X] T024 Implement stopped-transcript exclusive snapshot acquisition and length/hash evidence in `src/TerminalTranslator.Windows/Capture/StableTranscriptSnapshot.cs`
- [X] T025 Add tests for immutable record creation and one-authoritative-generation manifest visibility before and after the commit point in `tests/TerminalTranslator.Windows.Tests/Unit/RetainedCaptureStoreTests.cs`
- [X] T026 Implement immutable retained record storage and committed-manifest reads without timestamp selection in `src/TerminalTranslator.Windows/Capture/RetainedCaptureStore.cs`
- [X] T027 Add logical content/metadata/index/committed-manifest accounting tests at 10,179,999, exactly 10,180,000, and 10,180,001 bytes, plus multi-record pressure, oldest-complete eviction, no ordinary record splitting, and newest-record preference in `tests/TerminalTranslator.Core.Tests/Unit/CaptureRetentionPolicyTests.cs`
- [X] T028 Implement exact 10,180,000-byte retained-generation accounting across content, metadata, indexes, and the committed manifest plus oldest-complete-record eviction in `src/TerminalTranslator.Core/Capture/CaptureRetentionPolicy.cs`
- [X] T029 Add publication fault tests for incomplete candidates, temporary manifest write/flush, pre-swap failure, immediate post-swap failure, and superseded-generation deletion in `tests/TerminalTranslator.Windows.Tests/Integration/RetainedCapturePublicationFailureTests.cs`
- [X] T030 Implement candidate isolation, atomic committed-manifest replacement, and strict-latest publication outcomes in `src/TerminalTranslator.Windows/Capture/CapturedCommandFinalizer.cs`
- [X] T031 Add tests proving content cleanup failure leaves one ineligible residue set, blocks new staging, never accumulates a second set, and resumes only after complete cleanup in `tests/TerminalTranslator.Windows.Tests/Integration/FinalizationResidueRecoveryTests.cs`
- [X] T032 Implement non-accumulating finalization-residue tracking and recovery gating in `src/TerminalTranslator.Windows/Capture/FinalizationResidueManager.cs`
- [X] T033 [P] Add state-transition and one-warning/one-restoration suppression tests for Starting/Healthy/Unavailable/Recovering/Closing in `tests/TerminalTranslator.Core.Tests/Unit/CaptureHealthTests.cs`
- [X] T034 Implement content-free capture health transitions and notification decisions in `src/TerminalTranslator.Core/Capture/CaptureHealth.cs`
- [X] T035 Add basic retriever tests for reliable snapshot, no previous command, no output, disabled, unavailable, and unreliable/corrupt exclusive outcomes in `tests/TerminalTranslator.Core.Tests/Unit/PreviousCommandRetrieverTests.cs`
- [X] T036 Implement the shared provider-neutral previous-command retriever over validated current-session committed records in `src/TerminalTranslator.Core/Capture/PreviousCommandRetriever.cs`

**Checkpoint**: A test-controlled PowerShell interval can produce one trustworthy, capacity-bounded retained record and retrieve it by explicit current-session proof; all faults remain fail-closed for retrieval.

---

## Phase 3: User Story 1 — Inline `tt last` in an Ordinary PowerShell Session (Priority: P1)

**Goal**: Translate the strict previous command's English output inline and append one short recommendation.

**Independent test**: Given a reliable English snapshot, `tt last` prints `[翻译]` and `[建议]`, preserves command/stream context, opens no pane, and executes no provider-generated text.

- [X] T037 [P] [US1] Add root `last` grammar, usage, and unexpected-argument contract tests in `tests/TerminalTranslator.Cli.Tests/Contract/LastCommandContractTests.cs`
- [X] T038 [P] [US1] Add provider-neutral LastTranslation request/result and inert-output contract tests in `tests/TerminalTranslator.Core.Tests/Unit/AssistanceModelsTests.cs`
- [X] T039 [US1] Implement on-demand request kinds, selected context, result, and display-only provider abstractions in `src/TerminalTranslator.Core/Assistance/AssistanceModels.cs`
- [X] T040 [P] [US1] Add safe HTTP transport regression tests for credentials, cookies/redirects, timeout, response bounds, status mapping, and unchanged live-provider behavior in `tests/TerminalTranslator.Cli.Tests/Contract/SafeChatCompletionTransportTests.cs`
- [X] T041 [US1] Extract the minimum shared safe chat-completion transport while preserving Feature 001 prompts and limits in `src/TerminalTranslator.Cli/Providers/SafeChatCompletionTransport.cs` and `src/TerminalTranslator.Cli/Providers/ChatCompletionTranslationProvider.cs`
- [X] T042 [US1] Add LastTranslation adapter contract tests for command-as-context, ordered output, exit/interruption facts, Simplified Chinese translation, technical-token preservation, fixed-instruction/response-reserve capacity validation, and 1–2 line recommendation in `tests/TerminalTranslator.Cli.Tests/Contract/OnDemandProviderContractTests.cs`
- [X] T043 [US1] Implement the request-kind-specific on-demand provider adapter without live queue/coordinator dependencies in `src/TerminalTranslator.Cli/Providers/ChatCompletionAssistanceProvider.cs`
- [X] T044 [US1] Add orchestration tests for ordinary output, PowerShell error, native mixed-stream order, provider timeout/error mapping, and zero execution authority in `tests/TerminalTranslator.Core.Tests/Integration/LastAssistanceJourneyTests.cs`
- [X] T045 [US1] Implement the minimal last-translation coordinator from retrieval result through provider result in `src/TerminalTranslator.Core/Assistance/LastAssistanceCoordinator.cs`
- [X] T046 [P] [US1] Add inline renderer contract tests for `[翻译]`, `[建议]`, no full-source replay, content-safe failures, and inert shell snippets in `tests/TerminalTranslator.Cli.Tests/Contract/InlineAssistanceRendererTests.cs`
- [X] T047 [US1] Implement Feature 002 console-only rendering, distinct safe statuses, and display-only response handling in `src/TerminalTranslator.Cli/Commands/InlineAssistanceRenderer.cs`
- [X] T048 [US1] Implement `LastCommand` composition and register `tt last` without companion-pane startup in `src/TerminalTranslator.Cli/Commands/LastCommand.cs` and `src/TerminalTranslator.Cli/Commands/CommandFactory.cs`

**Checkpoint**: US1 works against a reliable snapshot with a fake authorized provider and has no execution path for suggested commands.

---

## Phase 4: User Story 2 — Strict Previous-Command Correctness (Priority: P1)

**Goal**: Reliably select the current session's strict previous completed command and fail closed when identity or boundaries are uncertain.

**Independent test**: Normal, no-output, multiline, continuation, custom-prompt, interrupted, async, and independent-session fixtures produce only the specified exclusive outcome; contextual commands never self-pollute.

- [X] T049 [P] [US2] Expand boundary tests for normal, empty-output, multiline/continuation, custom-prompt, and command-echo ambiguity cases in `tests/TerminalTranslator.Core.Tests/Unit/CommandBoundaryValidatorTests.cs`
- [X] T050 [US2] Harden command text/output extraction against prompt text and pre-echo unattributed content in `src/TerminalTranslator.Core/Capture/CommandBoundaryValidator.cs`
- [X] T051 [P] [US2] Add wrong/unknown session, nonce mismatch, SID/PID/start-token mismatch, duplicate/nested shell, and stale-owner association tests in `tests/TerminalTranslator.Windows.Tests/Unit/CaptureSessionIdentityTests.cs`
- [X] T052 [US2] Enforce direct current-shell association and reject inherited/stale/cross-session identities in `src/TerminalTranslator.Windows/Capture/CaptureSessionIdentity.cs`
- [X] T053 [US2] Add strict result-matrix tests proving no previous, no output, disabled, unavailable, missing references, hash mismatch, inconsistent sequence, and unknown version never fall back to older content in `tests/TerminalTranslator.Core.Tests/Unit/PreviousCommandRetrieverTests.cs`
- [X] T054 [US2] Complete committed-generation/session/boundary integrity checks and exclusive outcome mapping in `src/TerminalTranslator.Core/Capture/PreviousCommandRetriever.cs`
- [X] T055 [US2] Add consecutive `tt last`/`tt ask last` self-pollution tests and prove ordinary `tt ask` remains an ordinary previous command in `tests/TerminalTranslator.Core.Tests/Unit/ContextualCommandSelectionTests.cs`
- [X] T056 [US2] Implement contextual-command classification and newest eligible strict-target selection in `src/TerminalTranslator.Core/Capture/ContextualCommandSelection.cs`
- [X] T057 [P] [US2] Add reliable and ambiguous Ctrl+C metadata tests for PowerShell and native command fixtures in `tests/TerminalTranslator.Core.Tests/Unit/InterruptedCommandBoundaryTests.cs`
- [X] T058 [US2] Implement conservative interrupted-state derivation that rejects unprovable termination boundaries in `src/TerminalTranslator.Core/Capture/CommandBoundaryValidator.cs`
- [X] T059 [P] [US2] Add tests proving post-prompt async output is not reassigned backward and `Clear-Host` retains history while itself remains a strict no-output command in `tests/TerminalTranslator.Windows.Tests/Integration/CompletionBoundarySemanticsTests.cs`
- [X] T060 [US2] Add end-to-end strict-last acceptance for normal, no-output, no-previous, multiline, custom-prompt, self-pollution, and two explicit session identities in `tests/TerminalTranslator.Cli.Tests/Integration/StrictPreviousCommandAcceptanceTests.cs`

**Checkpoint**: Retrieval never uses file recency, never skips a no-output or failed newest command, and never sends uncertain text.

---

## Phase 5: User Story 3 — Long Output and Mixed-Language Processing (Priority: P2)

**Goal**: Decide whole-output eligibility, enforce the adjustable provider input budget, and disclose local and AI reductions independently.

**Independent test**: English-only, Chinese-only, mixed, below-budget, above-budget, and single-command-over-cap fixtures produce deterministic selections, provider call counts, and distinct notices.

- [X] T061 [P] [US3] Add whole-output eligibility tests for tiny English, English-only, Chinese-only, mixed text, and technical-only paths/URLs/options/error codes/identifiers/code/package names in `tests/TerminalTranslator.Core.Tests/Unit/WholeOutputEligibilityTests.cs`
- [X] T062 [US3] Implement the no-minimum-length whole-output English eligibility policy without changing the live classifier in `src/TerminalTranslator.Core/Parsing/WholeOutputEligibility.cs`
- [X] T063 [P] [US3] Add policy tests for the adjustable 8,192 UTF-8-byte V1 default and a smaller adapter-declared capability in `tests/TerminalTranslator.Core.Tests/Unit/AiInputBudgetPolicyTests.cs`
- [X] T064 [US3] Add selector tests for under/exact/over budget, command/question/termination accounting, required-context overflow, and the independent `AiHeadTail`/AI-input-truncated state in `tests/TerminalTranslator.Core.Tests/Unit/AiRequestSelectorTests.cs`
- [X] T065 [US3] Implement provider-neutral variable-input budgeting and required-context-first selection in `src/TerminalTranslator.Core/Assistance/AiRequestSelector.cs`
- [X] T066 [P] [US3] Add deterministic equal HEAD/TAIL, odd-byte-to-tail, newline preference, and multibyte rune-boundary tests in `tests/TerminalTranslator.Core.Tests/Unit/Utf8HeadTailSelectorTests.cs`
- [X] T067 [US3] Implement streaming UTF-8-safe HEAD + TAIL selection without a scattered magic number in `src/TerminalTranslator.Core/Assistance/Utf8HeadTailSelector.cs`
- [X] T068 [US3] Add oversized single-command tests for dynamic metadata reservation, original byte count, trusted identity/boundary preservation, and fail-closed essential-metadata overflow in `tests/TerminalTranslator.Core.Tests/Unit/LocalCaptureTruncationTests.cs`
- [X] T069 [US3] Implement bounded local HEAD + TAIL record finalization and explicit local-truncation metadata in `src/TerminalTranslator.Core/Capture/OversizedCommandRetention.cs`
- [X] T070 [US3] Add retained-store integration tests proving a locally truncated newest record and all committed metadata remain at or below 10,180,000 bytes under one-under/exact/one-over and multi-record pressure in `tests/TerminalTranslator.Windows.Tests/Integration/RetainedCapacityAcceptanceTests.cs`
- [X] T071 [P] [US3] Add provider contract cases proving command text is not translated, Chinese is preserved, and technical tokens remain recognizable in `tests/TerminalTranslator.Cli.Tests/Contract/OnDemandProviderContractTests.cs`
- [X] T072 [US3] Add renderer tests for `LocalHeadTail` only, `AiHeadTail` only, both states, interrupted context, and summarized-translation disclosure in `tests/TerminalTranslator.Cli.Tests/Contract/InlineAssistanceRendererTests.cs`
- [X] T073 [US3] Implement separate local-capture and provider-input disclosure notices in `src/TerminalTranslator.Cli/Commands/InlineAssistanceRenderer.cs`
- [X] T074 [US3] Add integration acceptance for Chinese-only zero-call, mixed-language full-context handling, complete under-budget output, AI HEAD + TAIL, local HEAD + TAIL, and both truncation states in `tests/TerminalTranslator.Cli.Tests/Integration/LongAndMixedOutputAcceptanceTests.cs`

**Checkpoint**: The local retained cap and provider input policy are independently enforced and never represented as the same truncation event.

---

## Phase 6: User Story 4 — Capture Lifecycle, Local Privacy, and Failure Isolation (Priority: P1)

**Goal**: Make capture opt-in, reversible, current-user local, automatically cleaned, and harmless to normal shell execution under every fault.

**Independent test**: Enable/disable/profile/cleanup/fault-injection cases preserve command output, exit facts, stdin/stdout, prompt presentation, and shell lifetime while contextual retrieval fails closed.

- [X] T075 [P] [US4] Add configure grammar tests for capture-only enable/disable, unchanged provider-only all-or-none behavior, rejected mixed capture/provider mutation, and purpose disclosure in `tests/TerminalTranslator.Cli.Tests/Contract/CaptureConfigureContractTests.cs`
- [X] T076 [US4] Extend `tt configure` to atomically coordinate capture preference and integration installation without changing provider-only behavior in `src/TerminalTranslator.Cli/Commands/ConfigureCommand.cs`
- [X] T077 [US4] Add enable/disable/update/rollback tests for idempotent marked-block ownership, custom-profile preservation, and protected transaction-temporary cleanup in `tests/TerminalTranslator.Windows.Tests/Integration/PowerShellIntegrationLifecycleTests.cs`
- [X] T078 [US4] Complete reversible enable/disable loader lifecycle and prior TT-owned state recovery in `src/TerminalTranslator.Windows/PowerShell/PowerShellIntegrationInstaller.cs`
- [X] T079 [US4] Add session bootstrap tests for fresh identity, environment proof, enabled-state lookup, nested independent shell, disabled no-start, and Feature 001 hosted-session guard in `tests/TerminalTranslator.Windows.Tests/Integration/CaptureSessionBootstrapTests.cs`
- [X] T080 [US4] Implement fail-open PowerShell capture bootstrap and current-session runtime composition in `src/TerminalTranslator.Windows/PowerShell/CaptureSessionBootstrap.cs`
- [X] T081 [P] [US4] Add exact-owner normal-exit tests for one cleanup-only watcher, bounded idle/no-network/content-blind behavior, PID/start-token validation, and watcher failure fallback in `tests/TerminalTranslator.Windows.Tests/Integration/CaptureCleanupWatcherTests.cs`
- [X] T082 [US4] Implement the engine-exit handler and at-most-one exact-owner cleanup watcher lifecycle in `src/TerminalTranslator.Windows/Capture/CaptureCleanupWatcher.cs`
- [X] T083 [US4] Add stale crash-residue tests for dead-owner discovery, live/uncertain owner rejection, ineligible stale records, next-initialization deletion, and question-only initialization opening owner metadata but no command content in `tests/TerminalTranslator.Windows.Tests/Integration/StaleCaptureCleanupTests.cs`
- [X] T084 [US4] Implement content-free owner-manifest stale discovery and exact-session cleanup, including the content-blind initialization path allowed for question-only requests, in `src/TerminalTranslator.Windows/Capture/StaleCaptureCleaner.cs`
- [X] T085 [US4] Add active-session disable and uninstall tests proving next-prompt stop/delete, watcher shutdown, no future staging, documented TT-owned capture/consent cleanup, and preservation of unrelated profile content in `tests/TerminalTranslator.Windows.Tests/Integration/CaptureDisableAndUninstallTests.cs`
- [X] T086 [US4] Implement loaded-wrapper deactivation, session deletion, and TT-owned integration reversal in `src/TerminalTranslator.Windows/PowerShell/TerminalTranslator.Profile.ps1` and `src/TerminalTranslator.Windows/PowerShell/PowerShellIntegrationInstaller.cs`
- [X] T087 [P] [US4] Add health notification tests for first unavailable warning, repeated-prompt suppression, at-most-one restored notice, and exclusion of notices from command records in `tests/TerminalTranslator.Windows.Tests/Integration/CaptureHealthNotificationTests.cs`
- [X] T088 [US4] Wire content-free health/recovery decisions into prompt maintenance without changing prompt or automatic command results in `src/TerminalTranslator.Windows/PowerShell/TerminalTranslator.Profile.ps1`
- [X] T089 [US4] Add fault-injection integration tests for transcript start/stop, metadata write, ACL/storage creation, and next-interval restart, asserting normal command execution is unaffected and contextual retrieval is unavailable in `tests/TerminalTranslator.Windows.Tests/Integration/CaptureForegroundFailureIsolationTests.cs`
- [X] T090 [US4] Add fault-injection integration tests for snapshot acquisition, hash mismatch, publication/retention/compaction cleanup, and corrupt retained records, asserting one authoritative bounded generation, no partial read, no strict-last rollback, and shell survival in `tests/TerminalTranslator.Windows.Tests/Integration/CaptureFinalizationFailureIsolationTests.cs`
- [X] T091 [P] [US4] Add local privacy tests proving raw capture remains local and current-user protected but absent from normal logs, telemetry, diagnostics/support fixtures, sync-oriented paths, and user-facing show/export surfaces in `tests/TerminalTranslator.Cli.Tests/Integration/LocalCapturePrivacyTests.cs`

**Checkpoint**: Capture is durable only for the live opted-in session, cleanup is deterministic, and every capture fault is fail-open for the shell and fail-closed for contextual assistance.

---

## Phase 7: User Story 5 — Stateless `tt ask` (Priority: P2)

**Goal**: Answer one explicit question without reading Capture or retaining conversation history.

**Independent test**: Quoted, spaced, and Unicode questions reach a one-shot request unchanged; default language is Simplified Chinese unless explicitly overridden; capture-access and execution spies remain untouched.

- [X] T092 [P] [US5] Add `tt ask <question...>` parser tests for quoted text, multiple tokens, Unicode, empty question, and `last` subcommand disambiguation in `tests/TerminalTranslator.Cli.Tests/Contract/AskCommandContractTests.cs`
- [X] T093 [US5] Add QuestionOnly request tests for default Simplified Chinese, explicit language override, question-only content scope, and no conversation identifier/history in `tests/TerminalTranslator.Core.Tests/Unit/QuestionOnlyRequestTests.cs`
- [X] T094 [P] [US5] Implement response-language instruction selection from explicit user requests in `src/TerminalTranslator.Core/Assistance/ResponseLanguagePolicy.cs`
- [X] T095 [US5] Add QuestionOnly provider contract tests for stateless shape, bounded response, error normalization, and inert suggested commands in `tests/TerminalTranslator.Cli.Tests/Contract/OnDemandProviderContractTests.cs`
- [X] T096 [US5] Implement stateless QuestionOnly orchestration with no previous-command dependency in `src/TerminalTranslator.Core/Assistance/AskAssistanceCoordinator.cs`
- [X] T097 [US5] Add a reject-if-accessed integration test proving ordinary `tt ask` does not resolve session identity, open command records, or construct a contextual retriever in `tests/TerminalTranslator.Cli.Tests/Integration/AskCaptureIsolationTests.cs`
- [X] T098 [US5] Implement `AskCommand` question-only dispatch and direct inline answer rendering in `src/TerminalTranslator.Cli/Commands/AskCommand.cs` and `src/TerminalTranslator.Cli/Commands/CommandFactory.cs`
- [X] T099 [US5] Add consecutive-invocation acceptance proving no conversation persistence, language override isolation, no companion pane, and zero automatic process/file action in `tests/TerminalTranslator.Cli.Tests/Integration/StatelessAskAcceptanceTests.cs`

**Checkpoint**: Question-only assistance is stateless and cannot acquire terminal context even when a valid capture session exists.

---

## Phase 8: User Story 6 — Stateless `tt ask last` (Priority: P2)

**Goal**: Answer a one-shot question using the same trustworthy previous-command snapshot as `tt last`.

**Independent test**: Canonical syntax attaches command/output/termination/question context through the shared retriever; all capture failures and both truncation states are disclosed without fallback.

- [X] T100 [P] [US6] Add exact `tt ask last <question...>` grammar tests, including Unicode/multi-token text, missing question, and rejection of `tt ask --last` in `tests/TerminalTranslator.Cli.Tests/Contract/AskLastCommandContractTests.cs`
- [X] T101 [US6] Add QuestionWithPreviousCommand selection tests for command, ordered output, exit/interruption metadata, user question, response language, and stateless identity in `tests/TerminalTranslator.Core.Tests/Unit/AskLastRequestTests.cs`
- [X] T102 [US6] Implement contextual question orchestration by composing the existing shared retriever and request selector in `src/TerminalTranslator.Core/Assistance/AskLastAssistanceCoordinator.cs`
- [X] T103 [US6] Add tests mapping no previous, no output, disabled, unavailable, corrupt/wrong-session, and unreliable interruption outcomes without provider calls in `tests/TerminalTranslator.Core.Tests/Integration/AskLastFailureJourneyTests.cs`
- [X] T104 [US6] Add tests for local-only, AI-only, and simultaneous truncation context/disclosures in `tests/TerminalTranslator.Core.Tests/Integration/AskLastTruncationJourneyTests.cs`
- [X] T105 [US6] Add default Simplified Chinese and explicit alternate-language provider contract cases for contextual answers in `tests/TerminalTranslator.Cli.Tests/Contract/OnDemandProviderContractTests.cs`
- [X] T106 [US6] Implement the canonical `ask last` subcommand using shared retrieval and inline answer rendering in `src/TerminalTranslator.Cli/Commands/AskCommand.cs`
- [X] T107 [US6] Add integration acceptance proving shared retrieval, contextual self-pollution prevention, stateless responses, no companion pane, and display-only suggested commands in `tests/TerminalTranslator.Cli.Tests/Integration/AskLastAcceptanceTests.cs`

**Checkpoint**: `tt ask last` has no second retrieval implementation and cannot weaken strict-last integrity.

---

## Phase 9: User Story 7 — External Transmission Privacy and Consent (Priority: P1)

**Goal**: Authorize an unchanged trust boundary once, re-consent after relevant changes, and block every suspected secret before serialization/transmission.

**Independent test**: Recording providers observe zero calls for a secret in any outbound field or HEAD/TAIL selection; unchanged fingerprints reuse consent and changed trust-boundary fields require consent again.

- [X] T108 [P] [US7] Add canonical trust-fingerprint tests for adapter, normalized destination, model, credential-source identity, disclosed scope, policy version, and non-boundary timeout/display changes in `tests/TerminalTranslator.Cli.Tests/Configuration/ProviderConsentGrantTests.cs`
- [X] T109 [US7] Implement non-content persistent consent grants under existing LocalAppData conventions in `src/TerminalTranslator.Cli/Configuration/ProviderConsentGrantStore.cs`
- [X] T110 [US7] Add consent disclosure tests for question-only versus shared previous-command context scopes, capture-consent separation, persisted reuse, decline, and changed configuration in `tests/TerminalTranslator.Cli.Tests/Contract/AssistanceConsentContractTests.cs`
- [X] T111 [US7] Implement assistance-specific disclosure and matching-grant flow without altering live-session consent semantics in `src/TerminalTranslator.Cli/Commands/AssistanceConsentPrompt.cs`
- [X] T112 [P] [US7] Add exact-selection privacy tests with synthetic secrets in question, command, output, exit metadata, local HEAD/TAIL, and AI HEAD/TAIL positions in `tests/TerminalTranslator.Core.Tests/Unit/AssistancePrivacyGateTests.cs`
- [X] T113 [US7] Implement fail-closed SecretDetector screening over every exact outbound field after selection and before adapter access in `src/TerminalTranslator.Core/Assistance/AssistancePrivacyGate.cs`
- [X] T114 [US7] Add adapter-boundary tests proving an unconsented or ungated request cannot serialize, resolve credentials, or invoke HTTP in `tests/TerminalTranslator.Cli.Tests/Contract/OnDemandProviderAuthorizationTests.cs`
- [X] T115 [US7] Require matching consent and a passed privacy-gate assertion at the on-demand adapter boundary in `src/TerminalTranslator.Cli/Providers/ChatCompletionAssistanceProvider.cs`
- [X] T116 [US7] Add end-to-end zero-provider-call tests for suspected secrets across `tt last`, `tt ask`, and `tt ask last`, including selected HEAD/TAIL payloads, in `tests/TerminalTranslator.Cli.Tests/Integration/AssistanceSecretBlockingTests.cs`
- [X] T117 [US7] Add trust-boundary integration tests proving unchanged provider/destination/scope reuses consent while provider, destination, model, credential source, scope, or policy changes re-prompt in `tests/TerminalTranslator.Cli.Tests/Integration/AssistanceConsentPersistenceTests.cs`
- [X] T118 [US7] Add a local-boundary test proving synthetic sensitive terminal content may be retained without pre-persistence filtering but is blocked from every external path in `tests/TerminalTranslator.Cli.Tests/Integration/LocalPersistenceExternalGateSeparationTests.cs`
- [X] T119 [US7] Add error/diagnostic tests proving secret-block, provider timeout/error, parse failure, and capture failure messages never contain captured/question content in `tests/TerminalTranslator.Cli.Tests/Integration/AssistanceContentFreeFailureTests.cs`

**Checkpoint**: No on-demand provider path can observe content without both a matching consent grant and a successful exact-payload privacy gate.

---

## Phase 10: Integration, Regression, and Real Windows Terminal Acceptance

**Purpose**: Prove all automated, compatibility, Constitution, build, and topology-dependent release gates.

- [X] T120 Run Gate A (all automated tests) with `dotnet test TerminalTranslator.sln --configuration Release --no-restore -p:UseAppHost=false` and record the result in `specs/002-on-demand-terminal-assistance/validation/automated-gates.md`
- [X] T121 Run the explicit Feature 001 regression matrix for `tt start`, `tt on`, `tt off`, `tt status`, live classifier ownership, raw-loss/teardown, ConPTY relay, IPC security, session startup, and runtime journey tests; record it in `specs/002-on-demand-terminal-assistance/validation/feature-001-regression.md`
- [X] T122 Run Gate B with Release build and publish smoke (`dotnet build TerminalTranslator.sln --configuration Release --no-restore` and `dotnet publish src/TerminalTranslator.Cli/TerminalTranslator.Cli.csproj --configuration Release`) and record zero-error/warning-policy results in `specs/002-on-demand-terminal-assistance/validation/build-and-publish.md`
- [X] T123 Run Gate C by auditing purpose limitation, LocalAppData/DACL, retained capacity/lifetime/cleanup, sync/log/diagnostic exclusion, consent disclosure, and secret-gate placement against Constitution 2.0; record evidence in `specs/002-on-demand-terminal-assistance/validation/constitution-privacy.md`
- [X] T124 Run Gate E by scanning production capture composition for only the Accepted Transcript-plus-metadata source and no rejected fallback path; record commands and results in `specs/002-on-demand-terminal-assistance/validation/capture-architecture-audit.md`
- [X] T125 Run Gate F using behavioral and static call-path checks that every AI-generated command/snippet remains display-only and cannot reach process, PowerShell, filesystem, or confirmation APIs; record evidence in `specs/002-on-demand-terminal-assistance/validation/no-automatic-execution.md`
- [X] T126 Prepare a published test build, synthetic secret corpus, native mixed-stream fixture, deterministic AI-budget corpus, and practical over-10,180,000-byte local corpus for the manual guide in `specs/002-on-demand-terminal-assistance/validation/real-wt-fixtures.md`
- [ ] T127 [P] Run Gate D functional acceptance in real Windows Terminal + Windows PowerShell 5.1 for new-shell initialization, normal output, PowerShell error, native stdout/stderr, git, multiline/continuation, custom prompt, no output, no previous, `Clear-Host`, Ctrl+C, async boundary, and two independent sessions; record results in `specs/002-on-demand-terminal-assistance/validation/real-wt-functional.md`
- [ ] T128 [P] Run Gate D long/language/privacy acceptance in real Windows Terminal + Windows PowerShell 5.1 for Chinese-only, mixed language, below/over AI budget, local over-cap HEAD/TAIL, both truncation notices, `tt ask`, `tt ask last`, suspected secrets, and display-only generated commands; record results in `specs/002-on-demand-terminal-assistance/validation/real-wt-assistance.md`
- [ ] T129 [P] Run Gate D lifecycle/failure acceptance in real Windows Terminal + Windows PowerShell 5.1 for capture disabled, safe simulated capture failure, warning suppression/recovery, normal exit deletion, forced-termination stale cleanup, profile reversal, and unaffected shell execution/exit behavior; record results in `specs/002-on-demand-terminal-assistance/validation/real-wt-lifecycle.md`
- [ ] T130 Consolidate Windows Terminal version, Windows build, `$PSVersionTable`, published TT build, provider test adapter, Gate A–F results, and all manual pass/fail evidence into `specs/002-on-demand-terminal-assistance/validation/acceptance-record.md`

**Checkpoint**: All release gates pass in automation and the supported real GUI topology; PIPE-only evidence is supporting evidence, not the final compatibility claim.

---

## Dependencies and Execution Order

### Phase dependencies

- **Phase 1** has no feature dependency.
- **Phase 2** depends on Phase 1 and blocks all contextual command stories.
- **US1 (Phase 3)** depends on Phase 2.
- **US2 (Phase 4)** depends on Phase 2 and uses the US1 CLI/provider seams for end-to-end acceptance.
- **US3 (Phase 5)** depends on US1 and US2 because selection operates on a reliable strict snapshot.
- **US4 (Phase 6)** depends on Phase 2; its release acceptance also exercises US1/US2/US3.
- **US5 (Phase 7)** depends on the US1 provider/renderer leaves but must remain capture-independent.
- **US6 (Phase 8)** depends on US2, US3, and US5 and reuses their retriever, selector, and question behavior.
- **US7 (Phase 9)** depends on all provider request kinds being defined; no provider-backed story is releasable before US7 passes.
- **Phase 10** depends on every story selected for release; the full Feature 002 release depends on Phases 1–9.

### User-story dependency graph

```text
Foundational Capture
  +--> US1 tt last --------+--> US3 long/mixed output --+
  |                        |                             |
  +--> US2 strict last ----+-----------------------------+--> US6 tt ask last
                                                         |
US1 provider/renderer leaves --> US5 tt ask -------------+

US1 / US3 / US5 / US6 provider paths --> US7 privacy + consent --> Release gates
US4 lifecycle + failure isolation -------------------------------> Release gates
```

### Parallel opportunities

- Phase 1: T002–T004 can run in parallel after T001's fixture conventions are agreed.
- Phase 2: model/policy test authoring T005, T007, T009, T011, T013, T015, and T033 can proceed independently; each implementation still follows its tests and listed substrate.
- US1: T037, T038, T040, and T046 are independent contract-test work after Phase 2.
- US2: T049, T051, T057, and T059 exercise separate boundary dimensions.
- US3: T061, T063, T066, and T071 cover independent pure-policy/provider contracts.
- US4: T075, T081, T087, and T091 cover independent configuration, cleanup, health, and privacy surfaces.
- US5/US6: T092, T094, and T100 can proceed independently once shared assistance models exist.
- US7: T108 and T112 independently define consent and exact-payload privacy behavior.
- Phase 10 manual cases T127–T129 may run in separate clean panes/test profiles only after T126 and the automated/build gates pass.

---

## Increment Strategy

### First demonstrable increment

Complete Phases 1–3 to demonstrate inline `tt last` against a trustworthy retained snapshot and a fake authorized provider. This is not release-ready until strict-boundary, lifecycle, truncation, and external privacy phases pass.

### Recommended shippable MVP

Ship the `tt last` slice only after Phases 1–6, the `tt last`-applicable consent/privacy tasks in Phase 9, and all corresponding Phase 10 gates pass. This preserves every P1 safety/lifecycle story and the required long-input truthfulness. `tt ask` and `tt ask last` can remain disabled until their complete Phases 7–9 pass.

### Full Feature 002

Complete all phases in order, then satisfy Gates A–F and the real Windows Terminal + Windows PowerShell 5.1 acceptance record. Do not begin a later provider-backed increment with a permissive or bypassable authorization default.

---

## Task Quality Rules

- Tests for retention, boundary/session isolation, truncation, privacy, no automatic execution, and failure isolation precede or sit immediately beside their production changes.
- A task marked `[P]` must not edit the same file as another concurrently executing task.
- Contextual commands share `PreviousCommandRetriever`; no second retrieval implementation is permitted.
- Feature 001 live queue/coordinator, hosted ConPTY lifecycle, live classifier, VT extraction, IPC, and companion rendering remain outside Feature 002 changes except the explicitly tested minimal safe transport leaf.
- The 8,192-byte value is an adjustable Plan/provider policy, centralized in the budget policy; it is not copied into the Product Spec.
- Active Transcript staging and completed-command retained bytes remain separate accounting domains.
- No task adds conversation history, capture show/export, automatic command execution, extra shell support, full-screen reconstruction, or an alternative capture source.
