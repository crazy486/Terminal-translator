# Phase-One Implementation Audit

**Feature**: `001-translate-terminal-output`  
**Audit date**: 2026-08-21  
**Authority**: project constitution 1.0.0, normative `spec.md`, contracts, plan, research,
data model, quickstart, README, and the production source under `src/`.

## T070 — Implementation Audit

### Dependency direction — PASS

- [x] `TerminalTranslator.Core` has no project references and no Windows, CLI, HTTP-adapter, or
  `System.CommandLine` dependency.
- [x] `TerminalTranslator.Windows` references only `TerminalTranslator.Core`; it does not reference
  CLI or provider configuration.
- [x] `TerminalTranslator.Cli` is the composition root and references Core and Windows.
- [x] The only non-framework production package is `System.CommandLine` 2.0.11 in CLI, matching the
  constitution and research decision.
- [x] Provider-specific JSON and HTTP behavior remain in CLI; provider-neutral request/result/error
  contracts remain in Core.

Evidence was taken from all three production `.csproj` files and `dotnet list ... reference`.
No reverse dependency or late-feature architecture inversion was found.

### Public API surface — PASS with justified test seams

- [x] Core public models, provider/clock/event interfaces, parser, session, bounded queue, worker,
  and coordinator are used across the Core/Windows/CLI assembly boundaries.
- [x] Windows public ConPTY, console-relay, IPC DTO/client/server, and launcher APIs are consumed by
  CLI or cross-assembly integration tests.
- [x] CLI public command factories, provider/configuration types, renderer, runtime composition, and
  pipeline are consumed by the executable composition root or CLI contract/acceptance tests.
- [x] Provider wire DTOs and `ProviderJsonContext`, which are used only inside the CLI adapter, were
  changed from public to internal during this audit.

Justified deviation: `BasicHostPipeline`, `MinimalControlPipeServer`,
`MinimalEnableRequestSender`, and `HostCommand.RunEventServerAsync` remain public because the
existing cross-assembly MVP/startup acceptance suites bind to them. They are not documented user
APIs and are not used by the production HostCommand path. Reworking every historical test seam via
friend assemblies would add release risk without improving the runtime security boundary, so the
audit records the surface rather than performing a broad visibility refactor.

### Logging, diagnostics, and persistence sinks — PASS

- [x] A global source scan found no production `ILogger`, `Trace`, `Debug`, `EventSource`, telemetry,
  transcript, snapshot, terminal-history, or translation-history sink.
- [x] The only production filesystem writer is `ProviderSettingsStore`; its private persisted DTO is
  an explicit allowlist of adapter, endpoint, model, API-key environment-variable name, timeout,
  source language, target language, and default-timeout migration marker.
- [x] The current settings file was inspected without printing its contents: allowlist validation
  passed and the credential value was absent.
- [x] `ContentFreeDiagnosticSink` accepts only a bounded identifier regex, count, and duration.
- [x] Provider/privacy/overload status uses normalized codes and counts. Raw provider exceptions,
  response bodies, source, translations, credentials, and Authorization values are not accepted by
  the status event API.
- [x] CLI errors are fixed operational messages or bounded platform exception messages. Provider
  exceptions never reach the host terminal error catch; the translation worker normalizes them.
- [x] `CompanionRenderer` intentionally renders transient source/translation received over the event
  pipe. This is the product display path, not a diagnostic or persistence sink.
- [x] Test corpora and fixtures contain only committed synthetic examples. No real credential,
  terminal transcript, generated history, or sensitive snapshot was found.

### IPC and security boundary — PASS

- [x] Both event and control servers use `PipeOptions.CurrentUserOnly`; clients connect to `.` and
  remote clients are not allowed.
- [x] Production pipe names include random session ID and nonce. The nonce is inherited through
  `TT_SESSION_NONCE`, not exposed in Windows Terminal command-line arguments.
- [x] Production event/control handshakes validate protocol version, session ID, nonce, and allowed
  role (`companion` or `control`).
- [x] JSON Lines reads and writes enforce the 32 KiB message bound.
- [x] Malformed, oversized, wrong-role, wrong-session, wrong-nonce, or wrong-protocol input closes
  only the offending connection and is never echoed.
- [x] Control/status responses contain only state, provider host/model, generation, queue counts,
  and aggregate privacy/overload counts.

The older `MinimalControlPipeServer` is an MVP test seam and is not instantiated by production
HostCommand; production uses the authenticated `ControlPipeServer`.

### Translation side-path isolation — PASS

- [x] `ConsoleOutputRelay` writes and flushes raw ConPTY bytes before making a synchronous
  non-blocking `TryOffer`; all analysis-offer exceptions are isolated.
- [x] The analysis channel is bounded and uses drop-write mode. Translation queues are thread-safe,
  non-blocking, capped at 16 high + 48 normal and 256 KiB retained source text, with expiry,
  replacement, and eviction.
- [x] Provider calls, worker consumption, event-pipe publication, and companion writes never run on
  the ConPTY drain path.
- [x] Event publication uses a bounded drop-write channel and a 250 ms writer deadline; disconnects
  discard unavailable-period events and future reconnects do not replay history.
- [x] Disable/teardown clears pending work, cancels the generation, detaches providers that ignore
  cancellation, observes detached faults, and rejects stale results before display.
- [x] Provider/worker/companion failure cannot inject child input or change raw output, shell
  lifecycle, final drain, or exit code.

### Settings and privacy — PASS

- [x] Translation starts disabled; no analysis or provider transmission occurs before explicit
  per-generation consent for the current provider fingerprint.
- [x] API-key values are resolved from the named environment variable only at request time and are
  never persisted or printed.
- [x] Secret detection is fail-closed before classification/enqueue and again before provider
  serialization; a skip emits only a generic reason and count.
- [x] Session-owned segments/translations and queue content are cleared on disable and teardown.
- [x] No product content cache, history file, database, telemetry, or background service exists.

### Issues found and corrections

1. **Queue status placeholder — FIXED.** `SessionControlHandler.StatusAsync` returned hard-coded
   `high=0 normal=0`, contrary to `contracts/cli.md`. The worker now exposes bounded queue counts to
   the production pipeline, and status reports the actual snapshot. A deterministic regression test
   holds the provider, queues normal work, and proves a non-zero bounded value is returned.
2. **Provider wire DTO visibility — FIXED.** Chat-completion request/response DTOs and their JSON
   source-generation context were public despite being adapter-internal. They are now internal.
3. **PSReadLine prompt-tail echo variant — FIXED during T071.** The first final-regression run
   captured an intermittent real ConPTY shape in which only a prompt tail such as `$>` preceded a
   fragmented `Read-Host` assignment. The tracker recognized full `PS path>` and `>` prefixes but
   not this tail, so the command fragment reached the provider. The filter now removes a prompt tail
   only when the remaining text equals or prefixes an actually submitted command. A focused unit
   regression and three consecutive real-ConPTY reruns passed; unrelated stdout remains eligible.
4. **Resize-monitor test cancellation race — TEST FIXED during final verification.** A timer could
   cancel between the monitor's delay and loop condition, making the valid production method return
   normally while the test required `OperationCanceledException`. The test now cancels
   deterministically from the callback after observing the required `100x40` resize. Production
   behavior was unchanged and the scenario is not excluded.

Targeted verification after both changes: 28 passed, 0 failed, 0 skipped.

### Timeout deviation: 10 seconds versus 1.5 seconds

Conclusion: **justified implementation evolution plus stale non-normative data-model text; not a
normative spec violation**.

- `spec.md` does not prescribe a provider default timeout.
- `contracts/cli.md` normatively permits 100–10000 ms but does not prescribe a default.
- The 1.5-second value in `data-model.md` is therefore an outdated design default, while the separate
  1.5-second queue-expiry rule in research/tasks remains current and implemented.
- Production deliberately migrates legacy persisted 1.5-second defaults to the current reliable
  10-second default while preserving explicitly configured timeouts.
- README documents the actual 10-second default. Quickstart explicitly passes `--timeout-ms 1500`;
  that is a valid test override, not a statement of the default.

No production timeout value was changed during this audit.

## T071 — Final Validation Matrix

All commands used Release output, deterministic fakes/loopback services, and no public provider.

| Command / suite | Result | Count / diagnostics | Elapsed |
|-----------------|--------|---------------------|---------|
| Offline `dotnet restore` with package sources cleared | PASS | 7 projects | 1.6 s |
| `dotnet build TerminalTranslator.sln -c Release --no-restore` | PASS | 7 projects; 0 warnings; 0 errors | 1.7 s |
| Core tests | PASS | 67 passed; 0 failed; 0 skipped | 1.2 s |
| Windows tests | PASS | 30 passed; 0 failed; 0 skipped | 6.3 s |
| CLI tests | PASS | 74 passed; 0 failed; 0 skipped | 9.8 s |
| Deterministic local provider stub | PASS | 11 passed; 0 failed; 0 skipped | 1.5 s |
| Translation corpus | PASS | 3 passed; 0 failed; 0 skipped | 4.2 s |
| Interactive matrix | PASS | 9 passed; 0 failed; 0 skipped | 1.5 s |
| Security/lifecycle matrix | PASS | 20 passed; 0 failed; 0 skipped | 4.5 s |
| Queue/status/provider/overload/failure matrix | PASS | 41 passed; 0 failed; 0 skipped | 4.0 s |
| US1–US4 automated acceptance | PASS | 14 passed; 0 failed; 0 skipped | 5.9 s |
| Full `TerminalTranslator.sln` regression | PASS | 171 passed; 0 failed; 0 skipped | 9.9 s |
| `scripts/publish.ps1` plus published `--help` smoke | PASS | `win-x64`, self-contained, single-file, untrimmed | 7.2 s |
| `scripts/validate.ps1` overall | PASS | 13/13 steps | 57.3 s |

The first full-regression attempt exposed the PSReadLine prompt-tail issue described under T070.
It was reproduced, fixed, and rerun rather than classified as flaky or excluded. The final matrix
has **no justified test exclusions**.

## T072 — Quickstart Execution

### Actually executed — PASS

- [x] Prerequisite discovery: .NET SDK 10.0.400, Windows build 10.0.26200, Windows Terminal alias,
  Windows PowerShell 5.1.26100.9168, Git, Python, npm, and Codex were found.
- [x] Quickstart restore/build/test workflow was executed by the offline validation runner.
- [x] The published `tt.exe --help` was executed and listed `configure`, `start`, `on`, `off`, and
  `status`.
- [x] Published `tt configure` was executed with loopback HTTP, explicit
  `--allow-loopback-http`, a synthetic environment-variable name, and 1500 ms timeout. Because
  Windows `SpecialFolder.LocalApplicationData` ignores an attempted environment override, this
  temporarily replaced the user's non-sensitive settings. The previously documented DeepSeek
  endpoint/model/environment-variable-name configuration was immediately restored using the
  published command. No credential value was read or printed.
- [x] The restored settings file was inspected without printing its content: explicit field
  allowlist passed, credential-value absence passed, and default timeout was `00:00:10`.
- [x] The deterministic local provider command in the corrected quickstart ran 11 success/failure
  cases without public-network access.
- [x] Corpus, ConPTY/interactive, privacy/security, disable/generation, provider degradation,
  overload, companion reconnect, teardown, and exit-code automation were actually executed by the
  recorded suites—not merely inferred from earlier results.
- [x] Published `status` and `off` were executed outside a managed session and correctly returned
  exit code 5 with content-free `No live translation session.` output.
- [x] Persistence inspection found only `provider-settings.json` in the product settings directory
  and no terminal/translation history, transcript, log, temporary content, or credential file in
  the repository.

### Not executable from this environment — manual validation still required

- [ ] Launch `tt start` and visually confirm the new Windows Terminal window, exact two-pane layout,
  focused interactive Windows PowerShell program pane, and companion `translation: disabled`.
- [ ] Inside that GUI pane, execute `tt status`, decline and accept `tt on`, observe real companion
  translations, verify the privacy-skip notice, run the Python prompt and representative
  Codex/npm/Git interactions, then exercise `tt off`, re-consent, companion closure, normal exit,
  and forced termination.

Technical reason: the current command executor can launch `wt.exe` but cannot capture, focus, type
into, or visually inspect the separate Windows Terminal GUI panes it creates. Launching an
unobservable session would not establish any quickstart expectation and could leave an unmanaged
host process. Automated real-ConPTY tests support the guarantees but are not claimed as execution
of these GUI observations.

Therefore T072 remains incomplete pending the listed manual run. This is a T072 execution boundary,
not a T071 test exclusion.

### Quickstart corrections

- Publishing now uses `scripts/publish.ps1` and the verified
  `artifacts/publish/win-x64/tt.exe` path.
- The configure section now distinguishes its explicit 1500 ms test override from the current
  10-second production default.
- The deterministic stub is accurately described as process-local to tests, with an executable
  test command. Manual two-pane fault injection now explicitly requires a separately controlled
  compatible test endpoint rather than implying the fixture is a standalone server.
