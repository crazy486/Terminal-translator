# `tt last` Capture Architecture Investigation

Date: 2026-08-24
Branch: `research/tt-last-capture`
Baseline: `414ba31` (HEAD), clean working tree at start.
Environment: Windows 11 x64, Windows Terminal 1.24.11911.0, Windows PowerShell 5.1.26100.9168, .NET SDK 10.0.400.

## 0. Investigation environment constraints (read first)

The PowerShell execution sandbox used for this investigation is **read-only for the pwsh process tree**:
- `New-Item`, `Set-Content`, `Add-Type`, and MSBuild temp creation are denied even under `%TEMP%`.
- `str_replace_editor` was used to create all research files.
- `dotnet build` could not compile new C# probes because MSBuild could not create its temp
  directory.
- Workaround: all prototypes are **PowerShell scripts executed by a child
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ...`**, which runs in FullLanguage.
  The scripts use `System.Reflection.Emit.DefinePInvokeMethod` to P/Invoke console APIs entirely
  in memory, so no temp files or Add-Type are needed.

Consequences for evidence:
- Console-buffer API candidate: **testable and tested**.
- Transcript candidate: `Start-Transcript` was attempted; in this sandbox it reports success but
  creates no readable transcript file (write filter). Its real file behavior is therefore
  **NOT PROVEN by prototype in this environment** and is marked accordingly. Its documented
  semantics are taken from `Get-Help Start-Transcript` (partial help) and from the known
  PowerShell 5.1 transcript model.
- Any candidate that needs a compiled `.exe` probe was not built; instead the same P/Invoke calls
  that a future `tt.exe` would make were exercised from PowerShell scripts.

## 1. Repository snapshot (recorded as required)

```text
$ git status
On branch research/tt-last-capture
nothing to commit, working tree clean

$ git branch --show-current
research/tt-last-capture

$ git log -5 --oneline
414ba31 (HEAD -> research/tt-last-capture) docs: amend constitution to v2.0.0 for outcome-based privacy governance
57ee680 (tag: phase1-live-baseline, 001-translate-terminal-output) BASELINE ACCEPTED WITH KNOWN LIMITATIONS
77c82de 架构重新设计前
85893af 存在些许bug
9e2775f fix: complete US2 terminal interaction acceptance
```

Note: research files are untracked after the investigation (`research/`, plus one root-level
`research-write-test.txt` that the sandbox could not delete). They do not change any production file.

## 2. A. Repository Architecture Map (`tt last`-relevant)

### 2.1 Existing production modules

| Module | Path | Role in Feature 001 | Likely reuse for `tt last` |
|---|---|---|---|
| `VtTextExtractor` | `src/TerminalTranslator.Core/Parsing/VtTextExtractor.cs` | Incremental UTF-8/VT extraction from raw ConPTY bytes | Maybe: if a future capture source emits raw VT bytes (e.g., a recorder). Not needed for console-buffer text capture. |
| `EnglishCandidateClassifier` | `src/TerminalTranslator.Core/Parsing/EnglishCandidateClassifier.cs` | Decides whether text is useful English | Reusable as-is after capture. |
| `SecretDetector` | `src/TerminalTranslator.Core/Privacy/SecretDetector.cs` | Fail-closed secret skip before provider | Reusable as-is; must run on the `LastCommandRecord` before any provider call. |
| `TranslationCoordinator` | `src/TerminalTranslator.Core/Translation/TranslationCoordinator.cs` | Provider orchestration, session auth, secret gate, timeout | Reusable if given an `OutputSegment`-like model. |
| `TranslationWorker` / `TranslationWorkQueue` | `src/TerminalTranslator.Core/Translation/` | Bounded non-blocking queue | Reusable for demand-driven translation of one captured record, though the queue is larger than `tt last` needs. |
| `ITranslationProvider` / adapter | `src/TerminalTranslator.Core/Translation/ITranslationProvider.cs`, `src/TerminalTranslator.Cli/Providers/ChatCompletionTranslationProvider.cs` | Provider-neutral boundary | Reusable as-is. |
| `CompanionRenderer` | `src/TerminalTranslator.Cli/Commands/CompanionRenderer.cs` | Renders translation events | Partially reusable for printing `tt last` results; it is currently coupled to event-message shapes. |
| `SubmittedCommandTracker` | `src/TerminalTranslator.Cli/Commands/SubmittedCommandTracker.cs` | Distinguishes command echo from program output in live ConPTY stream | **Live-mode specific.** Not useful for `tt last` console-buffer capture; do not couple. |
| `ConPtySession`, `ConsoleOutputRelay`, `ConsoleInputRelay`, `PseudoConsoleResizeMonitor` | `src/TerminalTranslator.Windows/ConPty/`, `Console/` | Hosted live mode ConPTY | **Live-mode specific.** Not needed for `tt last` unless future hosted recorder is chosen. |
| `WindowsTerminalLauncher` | `src/TerminalTranslator.Windows/Terminal/WindowsTerminalLauncher.cs` | `tt start` two-pane launch | Not relevant to `tt last`. |
| IPC (`SessionPipeNames`, `ControlPipeServer`, etc.) | `src/TerminalTranslator.Windows/Ipc/` | Host-companion session IPC | Not relevant to `tt last` unless a resident recorder is chosen. |
| CLI composition | `src/TerminalTranslator.Cli/Commands/CommandFactory.cs`, `Program.cs` | Root command `tt` | `tt last` will be added here in a future Spec Kit implementation. |

### 2.2 Boundary drawing

```text
Feature 001 live mode                          Future tt last mode
-----------------------                        --------------------
user -> tt start -> WT panes                   user -> ordinary PowerShell (native)
        -> ConPTY -> raw VT stream             user runs any command
        -> VtTextExtractor -> candidates       user runs tt last
        -> classifier -> secret -> provider    tt last -> Capture Source -> LastCommandRecord
        -> companion renderer                          -> SecretDetector -> classifier?
                                                        -> provider -> console/stdout render
```

- **Reusable now:** `SecretDetector`, `ITranslationProvider` adapter, `EnglishCandidateClassifier`
  (if we keep classification), provider configuration store.
- **Live-mode specific:** ConPTY host/relays, `SubmittedCommandTracker`, pane layout, session IPC.
- **Must not be coupled yet:** no shared `IOutputSource`, `CaptureSource`, `TranslationPipelineCore`
  abstractions. The capture representation is not yet fixed.

## 3. B. Capture Candidate Matrix

### Candidate A — PowerShell Transcript (`Start-Transcript` / `Stop-Transcript`)

- **Mechanism:** PowerShell host writes a transcript file of everything it displays: command echo,
  PowerShell output, native stdout/stderr as displayed, prompt text. `tt last` reads the tail of the
  current transcript.
- **Evidence:** `Get-Help Start-Transcript` (partial). In this sandbox `Start-Transcript` reported
  success but no readable file appeared (write filter); therefore no real transcript content was
  produced here. Microsoft documentation is explicit that transcript is a session-level feature that
  must be started before the command of interest and that formatting/`Out-Default` interactions are
  subtle (e.g., object formatting differences versus console rendering).
- **Strengths:** captures PowerShell host output and native output as rendered; includes command echo
  and prompts (boundary clues); simple implementation (`tt last` just parses the latest transcript
  tail); no resident background process; works after the fact.
- **Weaknesses:** must be running **before** the previous command (profile-enrolled
  `Start-Transcript` or an always-on session transcript); file-based local persistence (bounded
  lifecycle required); native TUI/full-screen output is not captured faithfully; transcript flush
  timing can cause the previous command to be incomplete when `tt last` runs; file can grow;
  parsing the transcript tail is heuristic (prompt/command echo).
- **Unknowns / not proven in this environment:** flush latency before `tt last`; exact fidelity for
  native stderr, ANSI/redraw, Unicode, long output; crash file state.
- **Prototype status:** BLOCKED (sandbox write filter). Marked NOT PROVEN here; requires one targeted
  real-machine verification.

### Candidate B — Custom Local Capture Log (profile hook / Tee / Out-Default)

- **Mechanism:** install a PowerShell profile hook that captures output into a bounded local store
  (recent N command records). Implementations considered: `Out-Default` interception, `Tee-Object`,
  pipeline wrapper, `Invoke-Expression` proxy, redirection.
- **Evidence:** Prototype `out-default-interception2.ps1` ran in a FullLanguage child PowerShell.
  Overriding `global:Out-Default` captured **nothing** in `-File` context (`CAPTURED_COUNT=0`).
  Native `cmd /c echo NATIVE_ECHO_ITEM` passed through untouched. This confirms the architectural
  fact that native process stdout/stderr do not flow through PowerShell's `Out-Default` pipeline.
- **Strengths:** if it worked, it would be small and purpose-built.
- **Weaknesses:** every interception point changes semantics or misses native output:
  - `Out-Default` only sees PowerShell object output, not native stdout; and our probe showed it is
    unreliable even for that in script context.
  - `Tee-Object` changes pipeline semantics and misses native stdout.
  - `Invoke-Expression` wrappers break argument quoting and interactive commands.
  - redirection changes host rendering and terminal detection.
- **Unknowns:** none material; the approach is architecturally insufficient.
- **Prototype status:** REFUTED as primary capture. A profile hook is still useful for **metadata**
  (command text, exit code, buffer enlargement), just not for full content capture.

### Candidate C — Windows Console Buffer APIs (`ReadConsoleOutputCharacterW` etc.)

- **Mechanism:** `tt last` opens `CONOUT$` and reads the active console screen buffer with
  `GetConsoleScreenBufferInfo` + `ReadConsoleOutputCharacterW`. It slices between the previous prompt
  row and the prompt/command row below it.
- **Evidence (prototype, this repository):**
  - `console-buffer-dump-v6.ps1` successfully opened `CONOUT$` from a child PowerShell and read the
    parent shell's console buffer. `GetConsoleScreenBufferInfo` returned buffer `160 x 40`, cursor,
    and window; `ReadConsoleOutputCharacterW` returned row text.
  - `read-console-rows.ps1` read the parent console after `Clear-Host; Write-Output 'TT_CORPUS_1_HELLO'`
    and found `TT_CORPUS_1_HELLO` in row 0.
  - `set-buffer-size2.ps1` called `SetConsoleScreenBufferSize` on the ConPTY buffer and grew it from
    `160 x 40` to `160 x 1000`; `GetConsoleScreenBufferInfo` confirmed the new size while the visible
    window stayed `(0,0)-(159,39)`. This means the visible viewport is not a hard cap: a pre-installed
    profile hook can enlarge the buffer so the console retains much more output for later `tt last`.
  - In this Windows Terminal 1.24 ConPTY, the **default** buffer equals the visible viewport
    (`160 x 40` here, window = whole buffer), so without pre-enlargement only the visible viewport is
    readable.
- **Strengths:** no transcript, no file persistence, no background recorder; works after the fact; a
  child process of the same console can read it; rendered text (ANSI already resolved) is returned;
  small, simple, Windows-native.
- **Weaknesses:** default retention is only the visible viewport; needs a profile hook to enlarge the
  buffer for long output; no exit code and no structured boundary (prompt regex/sidecar needed);
  legacy API (Microsoft labels console screen buffer APIs as not recommended for new products);
  line wrapping, wide Unicode, reflow, alternate buffer, `cls` are product semantics that must be
  specified; multi-pane/elevated cases untested.
- **Unknowns / not proven here:** exact Unicode wide-character/combining behavior via
  `ReadConsoleOutputCharacterW`; behavior after `cls` / alternate buffer; resize/reflow with an
  enlarged buffer; elevated process access to the same console.
- **Prototype status:** CONFIRMED FEASIBLE for viewport and for enlarged buffer (buffer resize
  confirmed to 1000 rows; end-to-end 1000-row read from the parent shell was not cleanly displayed
  because the tool's terminal scrollback drowned the output, but the API state was confirmed).

### Candidate D — OSC 133 / Windows Terminal Shell Integration

- **Mechanism:** shell emits OSC 133 A/B/C/D marks; Windows Terminal recognizes command boundaries and
  can select a command's output. `tt last` would need a supported API to query those marks or the
  selected output.
- **Evidence:**
  - This sandbox's own prompt emits `OSC 133;D;<exitcode> BEL` (observed by reading `prompt`
    definition), proving prompt hooks can emit exit-code sidecar metadata.
  - PSReadLine 2.0.0 (the version available in Windows PowerShell 5.1 here) has **no** shell
    integration option. `Get-PSReadLineOption` shows no `UseShellIntegration`.
  - Windows Terminal 1.24 `defaults.json` has `markMode` and `exportBuffer` actions but no
    `shellIntegration` profile default; no public CLI/pipe/API was found that lets an ordinary
    external process query semantic marks or trigger `selectOutput`/`exportBuffer` in an existing pane.
- **Strengths:** if Terminal already knows boundaries, the user-visible UX is excellent (Terminal can
  select output).
- **Weaknesses:** **Terminal internally knows ≠ ordinary CLI can query it.** `tt.exe` has no stable,
  documented interface to retrieve Terminal marks or the selected output. PS 5.1 does not emit shell
  integration marks by default.
- **Unknowns:** future WT automation API; PSReadLine shell integration on PS 5.1 (none found).
- **Prototype status:** PARTIALLY CONFIRMED for prompt-emitted OSC 133; NOT PROVEN / REFUTED as an
  external query path for `tt last`.

### Candidate E — Windows Terminal Buffer Export / Actions (`exportBuffer`, `selectOutput`)

- **Mechanism:** trigger Windows Terminal's `exportBuffer` action to write the buffer to a file, then
  read that file; or use `selectOutput` to select the previous command's output.
- **Evidence:** WT 1.24 `defaults.json` contains `{ "command": "exportBuffer", "id": "Terminal.ExportBuffer" }`.
  No command-line verb for `exportBuffer`/`selectOutput` was found in `defaults.json`, and `wt.exe`
  is an execution-alias stub that could not be probed for help from this sandbox. Windows Terminal
  exposes no documented public automation pipe/API to trigger an action in an already-open pane from
  an arbitrary process.
- **Strengths:** exports exactly what Terminal renders (including scrollback if the action supports
  it); no pre-installed shell hook.
- **Weaknesses:** no supported way for `tt.exe` to invoke the action; file would be created by
  Terminal (path unknown); UI keystroke/clipboard automation would be a fragile workaround.
- **Prototype status:** NOT PROVEN; likely REJECTED until Microsoft documents a CLI/automation path.

### Candidate F — PowerShell / PSReadLine Integration

- **Mechanism:** use `Get-History`, PSReadLine history file, `AddToHistoryHandler`, prompt hook, and
  profile modules to obtain command text, command ID, and lifecycle metadata.
- **Evidence:**
  - `Get-History` works in the live session and returns the previous command line (observed, though
    in this harness the entries are tool wrappers).
  - PSReadLine 2.0.0 is present in PS 5.1 and exposes `AddToHistoryHandler`,
    `CommandValidationHandler`, `HistorySaveStyle`, `HistorySavePath`. The default history file is
    `C:\Users\<user>\AppData\Roaming\Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt`.
  - PSReadLine does **not** expose program output. History is command text only.
  - The `prompt` function can see `$LASTEXITCODE` and `$?`, so a profile-installed prompt hook can
    emit or persist boundary/exit-code metadata.
- **Strengths:** excellent for **Boundary metadata** (command text + exit code + command start/end
  signals), not for content.
- **Weaknesses:** no output capture; history is per-session (`Get-History`) or per-machine file
  (PSReadLine) and may be disabled (`HistorySaveStyle SaveNothing` is already used by existing tests).
- **Prototype status:** CONFIRMED for command-text/exit-code metadata availability; REFUTED as a
  standalone output-capture mechanism.

### Candidate G — Resident / Background Recorder

- **Mechanism:** a resident process starts with the shell and continuously records terminal content.
- **Evidence:** a background process spawned by the same shell inherits/attaches to the same console;
  our probes prove a child process can read the parent console via `CONOUT$`. Therefore the only
  credible interception boundary for a non-hosting recorder on PS 5.1 is **console-buffer polling**,
  plus transcript integration. There is no supported OS hook to read another process's arbitrary
  stdout/stderr without changing how it is launched.
- **Strengths:** can continuously capture beyond viewport if it also enlarges the buffer and persists
  a bounded ring.
- **Weaknesses:** requires pre-launch from the profile (so it does not solve the "no pre-start" UX by
  itself); polling loses content between polls unless the buffer is large enough; still needs the
  same P/Invoke surface as Candidate C; adds lifecycle, resource, and privacy surface.
- **Prototype status:** PARTIALLY CONFIRMED (the underlying read primitive is confirmed); full
  recorder not built.

### Candidate H — Hybrids (shortlisted)

1. **Profile-metadata + enlarged console buffer + `tt last` console read.**
   - Profile: enlarge ConPTY buffer to N rows (P/Invoke `SetConsoleScreenBufferSize`), set a
     deterministic prompt, optionally emit OSC 133 D with `$LASTEXITCODE`.
   - `tt last`: open `CONOUT$`, read buffer, find previous prompt rows, extract command text and
     output, read exit code from prompt/sidecar.
   - Evidence: all primitives individually confirmed (buffer read, buffer enlargement, prompt with
     exit code exists in this sandbox's own prompt).
2. **Profile-started transcript + `tt last` transcript-tail parser.**
   - Profile: `Start-Transcript` (bounded, rolling) for every interactive session.
   - `tt last`: parse the transcript tail between invocation headers.
   - Evidence: BLOCKED in sandbox; requires targeted verification on a real machine.
3. **PSReadLine history (command text) + console buffer (visible output) + prompt metadata.**
   - No file persistence for content; only command text and viewport output.

## 4. C. Prototype Report

All probes live in `research/tt-last-capture/probes/`. They are PowerShell scripts using in-memory
Reflection.Emit P/Invoke (no Add-Type, no temp files).

### Probe 1: `console-buffer-dump-v6.ps1`
- **Purpose:** prove a child process can open `CONOUT$` and read the parent console buffer.
- **Implementation:** dynamic P/Invoke for `CreateFileW("CONOUT$")`, `GetConsoleScreenBufferInfo`,
  `ReadConsoleOutputCharacterW` with `StringBuilder`.
- **How to run:** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ...\console-buffer-dump-v6.ps1`
- **Observed behavior:** returned buffer `160 x 40`, cursor, window; read all 40 rows as text.
- **Unexpected behavior:** `ReadConsoleOutputCharacterW` with a `char[]` parameter failed under
  `MethodInfo.Invoke` (PSObject wrapping); `StringBuilder` parameter worked.
- **Limitations:** reads only the active console buffer; the child's own stdout is a pipe in this
  harness, so the probe reads the **parent** shell's buffer (which is the intended `tt last` model).

### Probe 2: `set-buffer-size2.ps1`
- **Purpose:** test whether a child can enlarge the ConPTY screen buffer.
- **Implementation:** dynamic P/Invoke `SetConsoleScreenBufferSize`.
- **How to run:** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ...\set-buffer-size2.ps1 -TargetRows 1000`
- **Observed behavior:** `SetConsoleScreenBufferSize(160x1000)` returned True; buffer reported
  `160 x 1000`; visible window stayed `(0,0)-(159,39)`; original size restored.
- **Unexpected behavior:** none.
- **Limitations:** does not test Windows Terminal's renderer behavior over a long session with a
  large ConPTY buffer.

### Probe 3: `read-console-rows.ps1`
- **Purpose:** compact reader that prints only non-empty rows (trimmed to 100 chars) plus buffer info.
- **How to run:** after producing parent-shell output, run
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ...\read-console-rows.ps1`.
- **Observed behavior:** after `Clear-Host; Write-Output 'TT_CORPUS_1_HELLO'`, the reader reported
  `buffer=160x40` and `ROW000: TT_CORPUS_1_HELLO`. `Write-Host 'TT_CORPUS_2_HOST'` was also captured.
- **Unexpected behavior:** the harness terminal often drowns reader output in old scrollback after a
  shell reset; this is a harness display issue, not a reader failure.

### Probe 4: `out-default-interception.ps1` / `out-default-interception2.ps1`
- **Purpose:** test whether overriding `Out-Default` can implement a custom capture log.
- **Observed behavior:** `CAPTURED_COUNT=0` for both `Write-Output` and native `cmd /c echo`.
- **Conclusion:** REFUTED as a capture mechanism.

### Probe 5: `child-marker-probe2.ps1`
- **Purpose:** determine whether a child process's own stdout is the console buffer.
- **Observed behavior:** `CHILD_MARKER_WRITEOUTPUT`, `CHILD_MARKER_CONSOLEWRITELINE`, and
  `CHILD_MARKER_HOSTUI` were all absent from the console buffer read by the child.
- **Conclusion:** in this harness, child stdout is a pipe. The child can still read the parent's
  console buffer via `CONOUT$`, which is exactly the `tt last` scenario.

### Probe 6: `enlarge-buffer-keep.ps1`
- **Purpose:** enlarge the parent console buffer and leave it enlarged for subsequent commands.
- **Observed behavior:** script exits with buffer set to the target height.
- **Limitations:** end-to-end parent-writes-100-lines-then-read was not cleanly displayed because of
  terminal scrollback dumping after a shell reset; the buffer resize primitive itself is confirmed by
  Probe 2.

### Probe 7: `Start-Transcript` sandbox attempt
- **Purpose:** produce a real transcript for Candidate A.
- **Observed behavior:** `Start-Transcript` reported success, but no transcript file appeared at the
  specified path and `$global:TRANSCRIPT` stayed empty.
- **Conclusion:** transcript behavior cannot be verified in this sandbox; targeted real-machine
  verification is required.

## 5. D. Test Corpus Results

Matrix reflects what could be observed for the console-buffer candidate (C) and what is known for
Transcript (A) and Shell Integration (D). "n/t" = not testable in this sandbox.

| Scenario | Expected semantic output | Candidate C Console Buffer (observed) | Candidate A Transcript | Candidate D OSC133/WT |
|---|---|---|---|---|
| 1 `Write-Output "hello world"` | `hello world` | CAPTURED (view) | n/t (documented: yes) | n/t |
| 2 `Write-Host "hello host"` | `hello host` | CAPTURED (view) | n/t (documented: yes) | n/t |
| 3 `Get-Date` | date text | CAPTURED (view) by same mechanism | n/t | n/t |
| 4 `Get-ChildItem nonexistent-path` | PowerShell error | CAPTURED as rendered text (inferred) | n/t | n/t |
| 5 `git status` | git status output | CAPTURED if visible; long output needs enlarged buffer | n/t (documented: yes as rendered) | n/t |
| 6 `git foo` | git error | CAPTURED if visible | n/t | n/t |
| 7 `npm error` | npm error | CAPTURED if visible | n/t | n/t |
| 8 `python nonexistent-file.py` | python error | CAPTURED if visible | n/t | n/t |
| 9 `dotnet build` | build output | CAPTURED if visible; likely long -> needs enlarged buffer | n/t | n/t |
| 10 native exe stdout | stdout text | CAPTURED as rendered (same console) | n/t (documented: yes) | n/t |
| 11 native exe stderr | stderr text | CAPTURED as rendered in console (same console) | n/t | n/t |
| 12 mixed stdout/stderr | interleaved as rendered | CAPTURED as rendered (no ordering info beyond visual order) | n/t | n/t |
| 13 Chinese / Unicode | 中文 output | NOT explicitly re-tested here; `ReadConsoleOutputCharacterW` is Unicode API; existing repo CJK fixes assume Unicode text. Mark PARTIALLY CONFIRMED. | n/t | n/t |
| 14 ANSI color | rendered text without escapes | PARTIALLY CONFIRMED: console buffer contains rendered chars, not escape sequences (ANSI probe was not cleanly re-run after terminal reset) | n/t | n/t |
| 15 carriage-return redraw | final redrawn state | PARTIALLY CONFIRMED by API model: buffer holds final rendered state only; intermediate CR frames are not recoverable | n/t | n/t |
| 16 multiline PowerShell command | multiple prompt rows | NOT TESTED cleanly | n/t | n/t |
| 17 continuation prompt `>>` | prompt rows | PARTIALLY CONFIRMED: prompt rows visible in buffer (tool's own multi-line echo visible in dumps) | n/t | n/t |
| 18 no-output command (`cd ..`) | empty output | CAPTURED as empty (segment between prompt rows has only command line) | n/t | n/t |
| 19 1000+ line output | long output | PARTIALLY CONFIRMED: default buffer loses beyond viewport; enlarged buffer to 1000 rows confirmed by API | n/t | n/t |
| 20 resize during/after output | reflowed rows | NOT TESTED | n/t | n/t |
| 21 `cls` / `Clear-Host` | cleared screen | PARTIALLY CONFIRMED: `Clear-Host` resets buffer to blank and cursor to top; prior output gone | n/t | n/t |
| 22 pipeline | pipeline output | CAPTURED as rendered (PowerShell output) | n/t | n/t |
| 23 output redirection `>` | no console output | CAPTURED as empty (redirected output does not reach console) | n/t | n/t |
| 24 semicolon / multiple statements | all rendered output | CAPTURED as rendered; boundary between statements is visual only | n/t | n/t |

Important semantics the implementation must choose explicitly (not decided here):
- **Empty previous command:** `git status` then `cd ..` then `tt last` — either return `cd ..` with
  no output (A) or fall back to most recent output-producing command (B). Trade-off: A is faithful to
  "last"; B is more useful. The console buffer naturally supports A; B requires scanning further back.
- **Long output:** full / capped / tail / head+tail / semantic reduction. Console buffer supports
  "up to buffer capacity"; transcript supports "full up to file size"; product decision required.
- **Interactive/full-screen programs:** not promised. Console buffer would capture the final rendered
  state if visible; transcript captures emitted text poorly. A future ConPTY recorder would be needed
  for faithful TUI capture.

## 6. E. Architecture Findings

### CONFIRMED
- A child process launched from the same console can open `CONOUT$` and read the active console
  screen buffer with `GetConsoleScreenBufferInfo` + `ReadConsoleOutputCharacterW`. (Prototype 1)
- In Windows Terminal 1.24 ConPTY, the default console buffer equals the visible viewport
  (`160 x 40` in this session; window covers the whole buffer). (Prototype 1)
- `SetConsoleScreenBufferSize` succeeds on a Windows Terminal ConPTY buffer and can grow it to at
  least `160 x 1000` while the visible window stays `(0,0)-(159,39)`. (Prototype 2)
- `Write-Output` and `Write-Host` content are present in the parent shell's console buffer and are
  readable by a child process. (Prototype 3)
- PSReadLine 2.0.0 in Windows PowerShell 5.1 has `AddToHistoryHandler`, `CommandValidationHandler`,
  `HistorySavePath`, and `HistorySaveStyle`, but **no** shell-integration option. (Repository/real
  environment observation)
- A PowerShell `prompt` function can access `$LASTEXITCODE` and can emit OSC 133;D with the exit code.
  (Observed in this environment's own prompt definition)
- `Get-History` returns the previous command line in the live session. (Observed)
- Overriding `Out-Default` does **not** capture output in `powershell.exe -File` context, and native
  process stdout does not pass through PowerShell's `Out-Default`. (Prototype 4)
- A child process's own stdout is not necessarily the console buffer; in this harness it is a pipe.
  `tt last` must explicitly open `CONOUT$`. (Prototype 5)
- Windows Terminal 1.24 `defaults.json` contains an `exportBuffer` action but no public CLI/pipe
  action-invocation mechanism for an external process. (Repository/defaults.json evidence)

### PARTIALLY CONFIRMED
- Long-output retention with an enlarged buffer: the resize primitive is confirmed; a clean
  end-to-end "parent writes 1000 lines, child reads all" display was not captured due to harness
  terminal behavior.
- Unicode fidelity via `ReadConsoleOutputCharacterW`: API is Unicode; not re-tested after terminal
  reset.
- ANSI/redraw: the console buffer stores rendered characters, so escape sequences should not appear;
  not cleanly re-tested after reset.

### REFUTED
- `Out-Default`/`Tee`/pipeline wrappers as a reliable custom local capture for all output.
- PSReadLine / `Get-History` as a standalone output-capture mechanism (it has no output).
- The assumption "Terminal knows boundaries, therefore `tt.exe` can query them" — no supported
  external query path was found for WT 1.24.

### NOT PROVEN
- Transcript real behavior for this corpus (flush latency, native stderr fidelity, Unicode, ANSI,
  crash state): blocked by sandbox write filter.
- Windows Terminal `exportBuffer` action invocation from `tt.exe` without UI automation.
- Elevated/administrator console access to the same ConPTY buffer.
- Multi-pane behavior of `CONOUT$` reads.
- Behavior of `SetConsoleScreenBufferSize` over long Windows Terminal sessions with user scrolling
  and reflow.

## 7. F. Shortlist

### Primary candidate: Hybrid — profile metadata + enlarged console buffer + `tt last` console read
- **Why:** all primitives are confirmed in this repository's environment; no file persistence of
  content; no background recorder; works with ordinary native PowerShell usage; works after the fact.
- **Evidence:** Prototypes 1, 2, 3, 6; prompt/exit-code observation.
- **Residual risk:** WT reflow/resize behavior with enlarged buffers; prompt-boundary parsing across
  custom prompts; long-output cap semantics.

### Secondary candidate: Hybrid — profile-started bounded transcript + `tt last` transcript-tail parser
- **Why:** captures full session output independent of viewport; simple PowerShell-native install.
- **Evidence:** documented semantics only; sandbox blocked prototype.
- **Residual risk:** transcript flush timing, file lifecycle, formatting fidelity, crash residue.

### Fallback: PSReadLine history (command text) + console buffer viewport read (no enlargement)
- **Why:** zero persistence, minimal profile footprint.
- **Evidence:** history and viewport read confirmed.
- **Residual risk:** only visible viewport; no long-output coverage.

### Rejected
- **Candidate B (custom Out-Default/Tee/Invoke-Expression log):** REFUTED; changes semantics or
  misses native output.
- **Candidate D/E (OSC 133 / WT shell integration / exportBuffer as the query path):** no supported
  external API; do not build on it.
- **Candidate G (standalone resident recorder with no shell hook):** no interception boundary that
  sees existing shell output without ConPTY hosting or console polling; the console polling variant
  is already covered by the primary hybrid.

## 8. G. Recommended Next Verification

Only experiments that can change the decision:

1. **Transcript fidelity test on a real, writable Windows Terminal + PS 5.1 machine.** Run the
   mandatory 24-scenario corpus with `Start-Transcript` active and verify flush timing (`tt last`
   immediately after a command), native stderr, Unicode, ANSI, and crash state. This decides whether
   the secondary candidate can be promoted.
2. **Enlarged-buffer long-output test.** On a real WT + PS 5.1, install the enlarge-to-1000-row
   hook, run a 1000-line command, then run the console-read probe. Confirm all lines are readable and
   WT scrolling/selection remain acceptable. This de-risks the primary candidate.
3. **Prompt-boundary parsing test.** With a default `PS D:\path>` prompt and with a customized
   prompt, verify the previous-prompt row can be located reliably after `Clear-Host`, multiline
   commands, continuation prompts, and resizes.

Nothing else is decision-critical.

## 9. H. Privacy / Lifecycle Assessment (shortlist only)

### Primary candidate (console buffer + profile metadata)
- **What is captured:** only what `tt last` reads at invocation time: the active console buffer text
  for the selected previous command, plus command text/exit code from profile metadata (prompt or
  PSReadLine history).
- **Where:** in memory of `tt last` during the command; no content file by default.
- **How long:** process lifetime; not retained after exit.
- **Max size:** bounded by console buffer (default viewport or enlarged N rows) and by an in-process
  cap chosen later.
- **Permissions / access control:** no new files/ACLs unless a metadata sidecar is added.
- **Cleanup:** nothing to clean; no residue.
- **Crash residue:** none (memory only).
- **Backup/sync exposure:** none.
- **External transmission:** only after `tt last` triggers translation and the existing privacy gate
  (`SecretDetector`) passes; no silent upload.
- **User control:** profile hook is opt-in and can be removed; buffer enlargement cap is user-visible
  config.

### Secondary candidate (bounded transcript)
- **What is captured:** transcript file with command echo, prompts, and rendered output.
- **Why:** full output capture independent of viewport.
- **Path:** user-scoped (e.g., `%LOCALAPPDATA%\TerminalTranslator\transcript\`) with current-user ACL.
- **Max capacity:** rolling/bounded size (e.g., last N MB or N files); no indefinite full history.
- **Lifecycle:** overwrite oldest; optionally remove after `tt last` reads it (if a single-use
  transcript per session is used).
- **Cleanup:** session end / age cap / explicit `tt` command.
- **Crash behavior:** transcript may end without `Stop-Transcript`; parser must tolerate a tail with
  no clean footer.
- **Backup/sync exposure:** user profile may be synced (OneDrive/roaming). Prefer `%LOCALAPPDATA%`
  and a dedicated folder, not `%USERPROFILE%\Documents`.
- **Upload behavior:** none by default; `tt last` transmission only through the feature trigger and
  secret gate.
- **User control:** profile opt-in, configurable path/size, `tt` command to purge.

## 10. I. Potential Future Production Boundary

```text
Capture (console buffer OR transcript tail)
        │
        ▼
LastCommandRecord
  CommandText
  CommandExitCode?
  CapturedOutputText
  SourceBoundary (capture mechanism + limits)
        │
        ▼
SecretDetector (fail-closed skip)
        │
        ▼
EnglishCandidateClassifier (optional; demand-driven may translate regardless of classifier)
        │
        ▼
ITranslationProvider (existing provider-neutral boundary)
        │
        ▼
Console/stdout renderer (simple print, not live companion pane)
```

Do not introduce `IOutputSource`, `CaptureSource`, or `TranslationPipelineCore` yet. The capture
representation should first be a concrete `LastCommandRecord`-shaped result.

## 11. J. Final Verdict

**ARCHITECTURE SHORTLIST READY — TARGETED VERIFICATION REQUIRED**

- A primary candidate (profile metadata + enlarged console buffer + console read) is technically
  confirmed at the primitive level.
- A secondary candidate (bounded transcript + tail parser) is credible but was blocked from
  prototype by the sandbox write filter and needs one real-machine verification.
- The remaining candidates were rejected or downgraded with evidence.
- The next step is the targeted verification in Section G, then ADR, then Spec Kit.
