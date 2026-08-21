# Tasks: Windows Terminal Output Translation

**Input**: Design documents from `/specs/001-translate-terminal-output/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: Required by the feature specification and constitution. Write each story's tests first
and confirm they fail for the intended reason before implementing that story.

**Organization**: Tasks are grouped by user story so each story can be implemented and validated as
an incremental slice.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel because it changes different files and has no dependency on another
  incomplete task in the same group.
- **[Story]**: Maps the task to US1, US2, US3, or US4 from spec.md.
- Every task includes an exact file path.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Initialize the .NET 10 solution, projects, dependency versions, and repository defaults.

- [X] T001 Create the .NET 10 SDK pin and shared compiler/test properties in global.json and Directory.Build.props
- [X] T002 Create TerminalTranslator.sln and the three production projects in src/TerminalTranslator.Core/TerminalTranslator.Core.csproj, src/TerminalTranslator.Windows/TerminalTranslator.Windows.csproj, and src/TerminalTranslator.Cli/TerminalTranslator.Cli.csproj with the references defined in plan.md
- [X] T003 [P] Create MSTest projects and production-project references in tests/TerminalTranslator.Core.Tests/TerminalTranslator.Core.Tests.csproj, tests/TerminalTranslator.Windows.Tests/TerminalTranslator.Windows.Tests.csproj, and tests/TerminalTranslator.Cli.Tests/TerminalTranslator.Cli.Tests.csproj
- [X] T004 Add stable System.CommandLine 2.0.x and win-x64 single-file publish defaults without trimming to src/TerminalTranslator.Cli/TerminalTranslator.Cli.csproj
- [X] T005 [P] Add C# formatting, nullable, analyzer, build-artifact, and secret-file exclusions in .editorconfig and .gitignore

**Checkpoint**: `dotnet restore` can resolve the empty solution using only the approved production
and test dependency budget.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Define shared contracts and platform seams that all four user stories require.

**CRITICAL**: No user-story implementation starts until this phase passes its build checkpoint.

- [X] T006 [P] Create session, pane, segment, translation, privacy, and status value types and enums from data-model.md in src/TerminalTranslator.Core/Models/SessionModels.cs and src/TerminalTranslator.Core/Models/TranslationModels.cs
- [X] T007 [P] Define provider-neutral request, result, normalized error, and ITranslationProvider contracts in src/TerminalTranslator.Core/Translation/ProviderModels.cs and src/TerminalTranslator.Core/Translation/ITranslationProvider.cs
- [X] T008 [P] Define injectable monotonic clock, delay, and content-free diagnostic interfaces in src/TerminalTranslator.Core/Sessions/IClock.cs and src/TerminalTranslator.Core/Sessions/IDiagnosticSink.cs
- [X] T009 [P] Define version-1 JSON Lines IPC DTOs and source-generated JSON metadata in src/TerminalTranslator.Windows/Ipc/SessionMessages.cs and src/TerminalTranslator.Windows/Ipc/SessionJsonContext.cs
- [X] T010 [P] Add source-generated Kernel32 ConPTY declarations, structs, and SafeHandle ownership in src/TerminalTranslator.Windows/ConPty/NativeMethods.cs and src/TerminalTranslator.Windows/ConPty/SafePseudoConsoleHandle.cs
- [X] T011 [P] Create reusable fake clock, fake provider, provider spy, and temporary settings helpers in tests/TerminalTranslator.Core.Tests/TestDoubles/Fakes.cs and tests/TerminalTranslator.Cli.Tests/TestDoubles/TestHttpMessageHandler.cs
- [X] T012 Build the explicit composition root and public/internal command skeleton without business logic in src/TerminalTranslator.Cli/Program.cs and src/TerminalTranslator.Cli/Commands/CommandFactory.cs
- [X] T013 Validate project references, analyzers, nullable warnings, and empty test discovery with TerminalTranslator.sln

**Checkpoint**: The solution builds with warnings treated as errors and each test project is
discoverable without network access.

---

## Phase 3: User Story 1 - Understand English Output (Priority: P1) MVP

**Goal**: Start a safe two-pane session, recognize useful line-oriented English, obtain a Chinese
translation after consent, and display it only in the companion pane.

**Independent Test**: Configure a fake provider, start the dedicated session, grant consent, emit
ordinary English information/errors, and verify associated Chinese appears in the companion while
the program-pane bytes remain identical and low-value text is skipped.

### Tests for User Story 1

- [X] T014 [P] [US1] Add failing incremental UTF-8, basic SGR, CR/LF, indentation, and logical-block extraction tests in tests/TerminalTranslator.Core.Tests/Unit/VtTextExtractorTests.cs
- [X] T015 [P] [US1] Add failing useful-English, Chinese, code, command, path, version, and priority classification tests in tests/TerminalTranslator.Core.Tests/Unit/EnglishCandidateClassifierTests.cs
- [X] T016 [P] [US1] Add failing chat-completion request, response, cancellation, credential-header, and adapter-swap contract tests in tests/TerminalTranslator.Cli.Tests/Contract/TranslationProviderContractTests.cs
- [X] T017 [P] [US1] Add failing translation/status event formatting and no-persistence tests in tests/TerminalTranslator.Cli.Tests/Contract/CompanionRendererContractTests.cs
- [X] T018 [P] [US1] Add a failing simulated-stream basic translation journey with fake provider and in-memory event sink in tests/TerminalTranslator.Core.Tests/Integration/BasicTranslationJourneyTests.cs

### Implementation for User Story 1

- [X] T019 [P] [US1] Implement the incremental UTF-8 and basic VT text extractor with 8 KiB candidate cap in src/TerminalTranslator.Core/Parsing/VtTextExtractor.cs
- [X] T020 [P] [US1] Implement useful-English classification, prompt/error priority, deduplication keys, and layout hints in src/TerminalTranslator.Core/Parsing/EnglishCandidateClassifier.cs
- [X] T021 [P] [US1] Implement validated non-sensitive provider settings and JSON settings persistence in src/TerminalTranslator.Cli/Configuration/ProviderSettings.cs and src/TerminalTranslator.Cli/Configuration/ProviderSettingsStore.cs
- [X] T022 [US1] Implement the configurable chat-completion HTTP adapter with long-lived HttpClient, typed JSON DTOs, response limits, and no retry in src/TerminalTranslator.Cli/Providers/ChatCompletionTranslationProvider.cs and src/TerminalTranslator.Cli/Providers/ProviderJsonContext.cs
- [X] T023 [US1] Implement the basic translation coordinator, sequence ordering, provider call, and transient TranslationItem creation in src/TerminalTranslator.Core/Translation/TranslationCoordinator.cs
- [X] T024 [P] [US1] Implement event-pipe translation/status serialization and client reconnect handshake in src/TerminalTranslator.Windows/Ipc/EventPipeServer.cs and src/TerminalTranslator.Windows/Ipc/EventPipeClient.cs
- [X] T025 [P] [US1] Implement companion-pane rendering with source/translation association and layout hints in src/TerminalTranslator.Cli/Commands/CompanionRenderer.cs
- [X] T026 [US1] Implement provider configuration validation and the public configure command from contracts/cli.md in src/TerminalTranslator.Cli/Commands/ConfigureCommand.cs
- [X] T027 [US1] Implement unique-session Windows Terminal two-pane command construction and the public start command in src/TerminalTranslator.Windows/Terminal/WindowsTerminalLauncher.cs and src/TerminalTranslator.Cli/Commands/StartCommand.cs
- [X] T028 [US1] Implement the interactive consent prompt and minimal enable request used by the public on command in src/TerminalTranslator.Cli/Commands/ConsentPrompt.cs and src/TerminalTranslator.Cli/Commands/OnCommand.cs
- [X] T029 [US1] Wire basic host and companion internal commands to the extractor, coordinator, event pipe, and renderer in src/TerminalTranslator.Cli/Commands/HostCommand.cs and src/TerminalTranslator.Cli/Commands/CompanionCommand.cs
- [X] T030 [US1] Complete the fake-provider two-pane MVP acceptance harness and enforce SC-001/SC-002 translation quality and latency fixtures in tests/TerminalTranslator.Cli.Tests/Integration/UserStory1AcceptanceTests.cs and tests/TerminalTranslator.Core.Tests/Fixtures/TranslationCorpus.json

**Checkpoint**: The US1 core pipeline is complete at the component-test level. US1 is not a runnable
terminal MVP until the `[US1F]` prerequisite tasks in Phase 4 complete; those tasks provide the real
PowerShell/ConPTY path, minimum enable control, and production composition required by the US1
independent test.

---

## Phase 4: US1 Runnable Terminal Foundation and User Story 2 (Priority: P2)

**Boundary correction**: Tasks tagged `[US1F]` are prerequisites for the runnable US1 checkpoint,
even though they live beside US2 because the original decomposition placed terminal hosting here.
They MUST complete before the advanced `[US2]` work. `[US1F]` provides only the minimum real
PowerShell terminal and enable path; complete off/status/re-enable/generation/ACL/teardown remains
in US3.

**Goal**: First make US1 runnable through the production ConPTY and provider path, then preserve
input, raw output, cursor/layout behavior, resize, Ctrl+C, Unicode, and exit status for interactive
PowerShell child programs while translation consumes only a side copy.

**Independent Test**: Complete streaming-output, input, choice, confirmation, resize, and interruption
flows and compare program-pane VT bytes and child exit results with translation disabled.

### Tests for User Story 2

- [X] T030A [US1F] Add failing minimum enable-control and production-runtime composition tests in tests/TerminalTranslator.Cli.Tests/Integration/ProductionRuntimeJourneyTests.cs
- [X] T030D [US1F] Add a failing real ConPTY-to-event-pipe acceptance journey with fake provider in tests/TerminalTranslator.Cli.Tests/Integration/RunnableUserStory1AcceptanceTests.cs
- [X] T031 [P] [US2] Add failing split-byte UTF-8, split VT, OSC/DCS suppression, erase/redraw, alternate-buffer, malformed-control, and idle-prompt tests in tests/TerminalTranslator.Core.Tests/Unit/VtTextExtractorInteractiveTests.cs
- [X] T032 [P] [US1F] Add failing basic PowerShell launch, stdin relay, stdout relay, raw-byte parity, and non-blocking analysis-copy tests in tests/TerminalTranslator.Windows.Tests/Integration/ConPtyRelayTests.cs
- [X] T032A [P] [US2] Extend ConPTY relay tests for simultaneous input/output, final-frame drain, and child exit-code propagation in tests/TerminalTranslator.Windows.Tests/Integration/ConPtyRelayTests.cs
- [X] T033 [P] [US2] Add failing Ctrl+C, resize, paste, Unicode/IME, cursor-query, and console-mode restoration tests in tests/TerminalTranslator.Windows.Tests/Integration/InteractiveConsoleTests.cs
- [X] T034 [P] [US2] Create a deterministic native interactive probe for prompt, progress, alternate-screen, and exit-code scenarios in tests/TerminalTranslator.Windows.Tests/Fixtures/InteractiveProbe/Program.cs

### Implementation for User Story 2

- [X] T030B [US1F] Implement the minimum authenticated enable request server and client response path in src/TerminalTranslator.Windows/Ipc/MinimalControlPipeServer.cs and src/TerminalTranslator.Cli/Commands/OnCommand.cs
- [X] T030C [US1F] Compose settings, ChatCompletionTranslationProvider, extractor, classifier, coordinator, event pipe, control server, and ConPTY relays in the production HostCommand path in src/TerminalTranslator.Cli/Commands/HostCommand.cs
- [X] T035 [US1F] Implement basic ConPTY creation, Windows PowerShell child launch, process attributes, and handle inheritance in src/TerminalTranslator.Windows/ConPty/ConPtySession.cs
- [X] T035A [US2] Extend ConPtySession for child-tree shutdown, final-frame drain, and exit-code propagation in src/TerminalTranslator.Windows/ConPty/ConPtySession.cs
- [X] T036 [US1F] Implement independent basic outer-console input relay in src/TerminalTranslator.Windows/Console/ConsoleInputRelay.cs
- [X] T036A [US2] Extend input relay for VT input, Ctrl+C forwarding, Unicode/paste, and console-mode restoration in src/TerminalTranslator.Windows/Console/ConsoleInputRelay.cs and src/TerminalTranslator.Windows/Console/ConsoleModeScope.cs
- [X] T037 [US1F] Implement immediate raw ConPTY output forwarding plus non-blocking analysis-copy offers in src/TerminalTranslator.Windows/Console/ConsoleOutputRelay.cs
- [X] T038 [P] [US2] Implement character-cell resize observation and ResizePseudoConsole propagation in src/TerminalTranslator.Windows/ConPty/PseudoConsoleResizeMonitor.cs
- [X] T039 [US2] Extend the VT extractor for control-string boundaries, erase/redraw supersession, indented-block coalescing, and 120 ms idle prompts in src/TerminalTranslator.Core/Parsing/VtTextExtractor.cs
- [X] T040 [US1F] Integrate the basic ConPTY input/output and analysis-copy lifecycle into src/TerminalTranslator.Cli/Commands/HostCommand.cs
- [X] T040A [US2] Integrate resize, Ctrl+C, final-frame drain, console restoration, and child exit propagation into src/TerminalTranslator.Cli/Commands/HostCommand.cs
- [X] T041 [US2] Add representative codex, git, npm, and python interaction matrices and assert SC-003 parity in tests/TerminalTranslator.Windows.Tests/Integration/UserStory2AcceptanceTests.cs

**Checkpoint**: The joint US1+US2 runtime is manually testable through `tt start` and `tt on`; US2
preserves interactive behavior independently of provider speed or translation display and never
sends companion text into child input.

---

## Phase 5: User Story 3 - Control Translation State (Priority: P3)

**Goal**: Provide explicit session configuration, per-generation consent, on/off/status controls,
same-user IPC authentication, fail-closed secret skipping, and transient teardown.

**Independent Test**: Start disabled, deny then grant consent, disable and re-enable, inject suspected
secrets, and terminate normally/abnormally; verify transmission only while authorized and no
content remains after teardown.

### Tests for User Story 3

- [X] T042 [P] [US3] Add failing TranslationSession state, generation, consent fingerprint, invalid-transition, and late-result tests in tests/TerminalTranslator.Core.Tests/Unit/TranslationSessionTests.cs
- [X] T043 [P] [US3] Add failing configure/start/on/off/status parser, disclosure, idempotency, and exit-code contract tests in tests/TerminalTranslator.Cli.Tests/Contract/CliContractTests.cs
- [X] T044 [P] [US3] Add failing same-user ACL, nonce handshake, protocol-version, role, malformed JSON, and 32 KiB limit tests in tests/TerminalTranslator.Windows.Tests/Integration/SessionPipeSecurityTests.cs
- [X] T045 [P] [US3] Add failing credential assignment, authorization, JWT, private-key, credential-URI, split-block, detector-fault, and benign-ID tests in tests/TerminalTranslator.Core.Tests/Unit/SecretDetectorTests.cs and tests/TerminalTranslator.Core.Tests/Fixtures/SecretCorpus.json
- [X] T046 [P] [US3] Add failing disable-under-load, old-generation suppression, normal/abnormal cleanup, and no-content-file tests in tests/TerminalTranslator.Core.Tests/Integration/SessionLifecycleTests.cs

### Implementation for User Story 3

- [X] T047 [US3] Implement TranslationSession state transitions, generation cancellation, consent fingerprinting, and content collection ownership in src/TerminalTranslator.Core/Sessions/TranslationSession.cs
- [X] T048 [P] [US3] Implement current-user-only, network-denied named-pipe creation and authenticated handshake validation in src/TerminalTranslator.Windows/Ipc/CurrentUserPipeFactory.cs and src/TerminalTranslator.Windows/Ipc/SessionHandshakeValidator.cs
- [X] T049 [US3] Implement the versioned duplex control server with enable, disable, and content-free status responses in src/TerminalTranslator.Windows/Ipc/ControlPipeServer.cs
- [X] T050 [US3] Implement the control client and complete public on, off, and status commands in src/TerminalTranslator.Windows/Ipc/ControlPipeClient.cs, src/TerminalTranslator.Cli/Commands/OnCommand.cs, src/TerminalTranslator.Cli/Commands/OffCommand.cs, and src/TerminalTranslator.Cli/Commands/StatusCommand.cs
- [X] T051 [P] [US3] Implement the fail-closed multiline secret detector and generic PrivacyDecision reason codes in src/TerminalTranslator.Core/Privacy/SecretDetector.cs
- [X] T052 [US3] Enforce secret screening before classification/enqueue and again before provider serialization in src/TerminalTranslator.Core/Translation/TranslationCoordinator.cs and src/TerminalTranslator.Cli/Providers/ChatCompletionTranslationProvider.cs
- [X] T053 [P] [US3] Harden settings and diagnostics so credentials and content never enter JSON settings, exceptions, or status output in src/TerminalTranslator.Cli/Configuration/ProviderSettingsStore.cs and src/TerminalTranslator.Cli/Diagnostics/ContentFreeDiagnosticSink.cs
- [X] T054 [US3] Implement cancellation-first normal/abnormal teardown, queue/content clearing, and companion session-ended events in src/TerminalTranslator.Core/Sessions/SessionTeardown.cs and src/TerminalTranslator.Cli/Commands/HostCommand.cs
- [X] T055 [US3] Complete SC-005/SC-006/SC-008/SC-009/SC-010 acceptance coverage in tests/TerminalTranslator.Cli.Tests/Integration/UserStory3AcceptanceTests.cs

**Checkpoint**: US3 proves explicit authority over every external transmission, safe secret
handling, disablement within one second, and no content persistence.

---

## Phase 6: User Story 4 - Degrade Safely (Priority: P4)

**Goal**: Keep the original program usable during provider errors, companion loss, translation
overload, cancellation races, and malformed translation results.

**Independent Test**: Inject all normalized provider failures, fill queues beyond limits, disconnect
the companion, and ignore cancellation in a fake provider; verify the shell remains responsive and
status remains bounded and content-free.

### Tests for User Story 4

- [X] T056 [P] [US4] Add failing timeout, cancellation, 401/403, 429, 408/5xx, connection, malformed, empty, oversized, redirect, cookie, and no-retry tests in tests/TerminalTranslator.Cli.Tests/Contract/ProviderFailureContractTests.cs
- [X] T057 [P] [US4] Add failing 16-high/48-normal/256-KiB limits, redraw replacement, priority eviction, 1.5-second expiry, and non-blocking offer tests in tests/TerminalTranslator.Core.Tests/Unit/TranslationWorkQueueTests.cs
- [X] T058 [P] [US4] Add failing five-second degradation aggregation and content-free provider/privacy status tests in tests/TerminalTranslator.Core.Tests/Unit/StatusAggregatorTests.cs
- [X] T059 [P] [US4] Add failing slow provider, companion disconnect/reconnect, ignored cancellation, active-output shutdown, and unchanged shell tests in tests/TerminalTranslator.Windows.Tests/Integration/FailureIsolationTests.cs

### Implementation for User Story 4

- [X] T060 [US4] Implement the non-blocking two-lane bounded queue, text budget, redraw replacement, eviction, and expiry policy in src/TerminalTranslator.Core/Translation/TranslationWorkQueue.cs
- [X] T061 [US4] Implement provider deadline handling and normalized error mapping with redirects/cookies disabled and no retry in src/TerminalTranslator.Cli/Providers/ChatCompletionTranslationProvider.cs
- [X] T062 [P] [US4] Implement rate-limited content-free privacy, overload, and provider status aggregation in src/TerminalTranslator.Core/Sessions/StatusAggregator.cs
- [X] T063 [US4] Implement translation worker cancellation, stale-generation rejection, invalid-result discard, and companion-unavailable behavior in src/TerminalTranslator.Core/Translation/TranslationWorker.cs
- [X] T064 [US4] Integrate queue and worker failure isolation without waits in the ConPTY drain path in src/TerminalTranslator.Windows/Console/ConsoleOutputRelay.cs and src/TerminalTranslator.Cli/Commands/HostCommand.cs
- [X] T065 [US4] Enforce SC-004 and fixed memory/notice bounds with sustained-output acceptance tests in tests/TerminalTranslator.Cli.Tests/Integration/UserStory4AcceptanceTests.cs

**Checkpoint**: US4 demonstrates that translation is always an optional side path and cannot stop,
delay, or change the original program.

---

## Phase 7: Polish and Cross-Cutting Validation

**Purpose**: Package, document, audit, and validate the complete phase-one product.

- [X] T066 [P] Add user-facing install, privacy model, provider configuration, command reference, and limitations to README.md
- [X] T067 [P] Add a deterministic local provider stub for quickstart and failure scenarios in tests/TerminalTranslator.Cli.Tests/Fixtures/StubTranslationServer.cs
- [X] T068 Add self-contained single-file win-x64 publish and artifact verification commands to scripts/publish.ps1
- [X] T069 Add an offline full validation runner matching quickstart.md to scripts/validate.ps1
- [X] T070 Audit dependency direction, public types, and source/translation logging sinks against the constitution in specs/001-translate-terminal-output/checklists/implementation-audit.md
- [X] T071 Run the translation corpus, interactive matrix, security corpus, overload tests, and full regression suite defined by TerminalTranslator.sln and record any justified exclusions in specs/001-translate-terminal-output/checklists/implementation-audit.md
- [ ] T072 Execute every runnable scenario in specs/001-translate-terminal-output/quickstart.md with test credentials and update only inaccurate validation instructions in specs/001-translate-terminal-output/quickstart.md

---

## Dependencies and Execution Order

### Phase Dependencies

- **Phase 1 - Setup**: No dependencies.
- **Phase 2 - Foundational**: Depends on Phase 1 and blocks all user stories.
- **Phase 3 - US1**: Depends on Phase 2 and creates the safe basic translation MVP.
- **Phase 4 - US2**: Depends on the US1 pipeline and host/companion skeleton.
- **Phase 5 - US3**: Depends on the US1 session, provider, and IPC skeleton; it can run in parallel
  with US2 after US1.
- **Phase 6 - US4**: Depends on the US1 pipeline; it can run in parallel with US2 and US3 after US1.
- **Phase 7 - Polish**: Depends on all user stories selected for the release.

### User Story Dependency Graph

```text
Setup -> Foundational -> US1 core -> US1F runnable terminal foundation -> US2
                                      |---> US3
                                      `---> US4
US2 + US3 + US4 -> Polish
```

### Within Each User Story

- Write the story tests first and verify they fail for the intended missing behavior.
- Implement models and pure core logic before platform or CLI integration.
- Keep raw ConPTY I/O independent from translation/provider work.
- Complete the independent acceptance test before declaring the story checkpoint complete.

## Parallel Opportunities

- **Setup**: T003 and T005 can proceed while the production project structure is prepared.
- **Foundational**: T006 through T011 touch separate boundaries and can proceed in parallel.
- **After US1 core**: Complete all `[US1F]` tasks before declaring US1 runnable or starting advanced
  US2 acceptance. US3 and US4 may otherwise proceed from the core pipeline.
- **Within US1**: T014-T018 tests can run in parallel; T019-T021 and T024-T025 touch separate files.
- **Within US2**: T031-T034 tests can run in parallel; T038 is separate from input/output relay work.
- **Within US3**: T042-T046 tests can run in parallel; T048, T051, and T053 are separate boundaries.
- **Within US4**: T056-T059 tests can run in parallel; T062 is separate from queue/provider work.
- **Polish**: T066 and T067 can run in parallel before packaging and final validation.

## Parallel Example: User Story 1

```text
Task T014: VtTextExtractorTests.cs
Task T015: EnglishCandidateClassifierTests.cs
Task T016: TranslationProviderContractTests.cs
Task T017: CompanionRendererContractTests.cs
Task T018: BasicTranslationJourneyTests.cs
```

After those tests fail as expected:

```text
Task T019: VtTextExtractor.cs
Task T020: EnglishCandidateClassifier.cs
Task T021: ProviderSettings.cs and ProviderSettingsStore.cs
Task T024: EventPipeServer.cs and EventPipeClient.cs
Task T025: CompanionRenderer.cs
```

## Parallel Example: User Story 2

```text
Task T031: VtTextExtractorInteractiveTests.cs
Task T032: ConPtyRelayTests.cs
Task T033: InteractiveConsoleTests.cs
Task T034: InteractiveProbe/Program.cs
```

## Parallel Example: User Story 3

```text
Task T042: TranslationSessionTests.cs
Task T043: CliContractTests.cs
Task T044: SessionPipeSecurityTests.cs
Task T045: SecretDetectorTests.cs and SecretCorpus.json
Task T046: SessionLifecycleTests.cs
```

## Parallel Example: User Story 4

```text
Task T056: ProviderFailureContractTests.cs
Task T057: TranslationWorkQueueTests.cs
Task T058: StatusAggregatorTests.cs
Task T059: FailureIsolationTests.cs
```

## Implementation Strategy

### MVP First

1. Complete Setup and Foundational phases.
2. Complete US1 tests and implementation.
3. Stop and validate the fake-provider two-pane MVP independently.
4. Demonstrate useful English translation without inline output or automatic command execution.

### Incremental Delivery

1. **US1**: Basic safe two-pane translation.
2. **US2**: Interactive CLI fidelity and terminal control behavior.
3. **US3**: Complete control, consent, secret, and transient lifecycle guarantees.
4. **US4**: Provider failure, overload, disconnect, and cancellation resilience.
5. **Polish**: Package and run the complete quickstart/regression audit.

### Parallel Team Strategy

After US1 is complete:

- Developer A completes US2 ConPTY and interactive fidelity.
- Developer B completes US3 state, consent, IPC security, and secret handling.
- Developer C completes US4 queue and failure isolation.

## Notes

- `[P]` means different files and no incomplete same-phase dependency.
- User-story labels provide traceability to spec.md.
- Tests are intentionally placed before implementation tasks.
- No task introduces a GUI, database, account, cloud sync, telemetry, or background service.
- No task may add terminal content to logs, settings, snapshots, or committed fixtures containing
  real secrets.
- Commit after each task or cohesive task group and validate at every checkpoint.
