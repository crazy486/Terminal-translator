# `tt last` Targeted Architecture Verification - Real Windows Terminal Results

Date: 2026-08-25

## Outcome

The manual stage completed in a genuinely interactive Windows Terminal 1.24 pane running Windows
PowerShell 5.1. V1-V5 now have real-terminal evidence.

The strongest technical capture source in the tested scenarios is the PowerShell transcript:

- it contained the full mixed-output corpus, including supplementary-plane Unicode;
- all 1,000 numbered lines were available immediately at the 0 ms measurement;
- the 0, 50, and 200 ms previous-command segments were identical in every measured scenario;
- it retained output removed from the console by `Clear-Host`; and
- the session-specific prompt sentinel and metadata sidecar selected the intended previous command
  without including the capture command.

The enlarged console buffer is viable as a zero-persistence, capacity-bounded source, but it is not
fidelity-equivalent: the default buffer retained only the final 48 of 1,000 lines, an emoji became
`U+FFFD`, `Clear-Host` removed prior output, and real narrowing permanently evicted older reflowed
lines.

**Technical recommendation:** promote a bounded, explicitly governed transcript plus session
metadata to the primary candidate for `tt last`. Retain the enlarged console-buffer design as an
optional zero-persistence fallback for users who accept its documented truncation and Unicode
limitations. This is not approval to ship an unbounded `Start-Transcript` file: the feature spec and
ADR must define capacity, lifetime, cleanup, crash behavior, ACLs, user control, and backup/sync
exposure before implementation, as required by Constitution Principle III.

## Evidence qualification

- Branch: `research/tt-last-capture`
- HEAD at session start: `414ba31171931f1743dc721ed7bf9cd8b69a2df7`
- Windows: `10.0.26200`, x64
- Windows Terminal: package `1.24.11911.0`, product `1.24.260710001`
- Shell: Windows PowerShell `5.1.26100.9168`, Desktop edition, `ConsoleHost`
- Process chain: native probe -> `powershell.exe` -> `WindowsTerminal.exe` -> `explorer.exe`
- Standard handles: all `CHAR`, with successful console-buffer information calls
- `CONOUT$`: opened successfully
- Completion marker: `tt-capture-verification/manual-completion/v1`
- Completed at: `2026-08-25T07:23:39.1658414Z`
- No PowerShell profile was modified; the prompt hook and transcript were session-scoped
- Initial and final Git status both contained only the pre-existing untracked
  `research-write-test.txt` and `research/` paths

Canonical evidence:

- `results/real-wt-20260825-150242-ec8f9623/`
- Machine analysis: `results/real-wt-20260825-150242-ec8f9623/analysis.json`
- Full interactive transcript: `results/real-wt-20260825-150242-ec8f9623/transcript.txt`

The analyzer was corrected to read UTF-8-without-BOM inputs explicitly under Windows PowerShell
5.1. Without `-Encoding UTF8`, its legacy ANSI default produced a false-negative Unicode result.

## Classification

| ID | Hypothesis | Result | Decisive evidence |
|---|---|---|---|
| V0 | Legacy DS `COORD` declaration is invalid | CONFIRMED | ABI-correct isolated probe; carried forward from automated stage |
| V1 | Real-WT `CONOUT$` captures the mixed output corpus | PARTIALLY CONFIRMED | All source/order markers captured, but emoji degraded to `U+FFFD` |
| V2 | Pre-enlarged console buffer retains 1,000 lines | CONFIRMED | Previous console segment contained exactly lines 1-1000 after height 1800 |
| V3 | Previous-command boundary avoids self-pollution | CONFIRMED WITH LIMITATION | All five scenarios passed; independent command-start boundary remains unproven |
| V4 | Transcript is faithful and immediately available | CONFIRMED WITH LIMITATION | Full corpus and 1,000 lines present at 0/50/200 ms; crash/TUI behavior not tested |
| V5 | Real resize/reflow can change retained console history | CONFIRMED | Complete markers fell 13 -> 5 -> 4 across wide/narrow/wide |

## V0 - Measurement instrument

The prior automated controlled reproduction remains valid. Win32 requires a by-value `COORD`
(`SHORT X; SHORT Y`), while the old DeepSeek probes declared the row coordinate as a `uint32`.
Only row zero happened to work through that legacy declaration. The corrected C# executable used by
this manual run has the expected four-byte `COORD` layout and correct row addressing.

Result: **CONFIRMED - OLD DS MULTI-ROW EVIDENCE INVALID**.

## V1 - Real-WT console coverage

The corrected `CONOUT$` reader captured all non-Unicode-fidelity markers from the preceding core
command:

- PowerShell success output (`Write-Output`)
- host output (`Write-Host`)
- native stdout and stderr
- a PowerShell non-terminating error
- Git success and error output
- available Python and .NET output/error evidence
- the ordered stdout/stderr/stdout markers in the expected order
- CJK and Greek BMP characters

The exact expected marker was `TT_UNICODE_中文_Ω_🙂`. The console buffer returned
`TT_UNICODE_中文_Ω_�`: the surrogate-pair emoji was replaced by one `U+FFFD`. The transcript
preserved the exact marker.

Result: **PARTIALLY CONFIRMED**. Coverage is broad enough for ordinary rendered terminal text, but
the console source cannot claim full Unicode fidelity.

## V2 - 1,000-line retention

### Default buffer control

- Buffer/window: `87 x 51`
- Full console snapshot: 48 distinct markers, lines 953-1000
- Previous-command console boundary: no complete 1,000-line segment could be reconstructed
- Transcript at 0/50/200 ms: exactly 1,000 distinct markers, lines 1-1000, in order, no duplicates

The default Windows Terminal ConPTY buffer equaled the visible viewport and did not retain the full
command.

### Enlarged buffer

- Buffer: `87 x 1800`; visible window: `87 x 51`
- Previous-command console boundary: exactly 1,000 distinct markers, lines 1-1000, in order, no
  duplicates
- Transcript at 0/50/200 ms: the same complete ordered 1,000-line segment

The full raw console snapshot contained 1,042 marker occurrences because it also retained 42 tail
lines from the earlier default-buffer control. This is expected whole-buffer history; the
sentinel-bounded previous-command segment correctly isolated exactly 1,000 lines.

Result: **CONFIRMED**. Pre-enlarging the buffer before output makes the complete 1,000-line command
readable by the corrected console probe.

## V3 - Previous-command boundaries

Both the console and transcript readers found the intended previous segment at the immediate 0 ms
measurement in every scenario:

| Scenario | Expected content | Console | Transcript | Capture command leaked into segment |
|---|---|---:|---:|---:|
| Simple previous output | `CMD2_UNIQUE` | Yes | Yes | No |
| Empty-output command | `cd .` | Yes | Yes | No |
| Continuation prompt | both continuation markers | Yes | Yes | No |
| Custom prompt | output marker and `CUSTOM>` | Yes | Yes | No |
| Switched session GUID | session-B-only marker | Yes | Yes | No |

The reader associated sentinels and sidecars with the explicit session GUID, including after the
session switch. It did not select a latest-modified sidecar and did not confuse `Capture-TtBoth` with
the preceding command.

Limitation: the start of a controlled segment is the preceding prompt sentinel. There is still no
independent Windows PowerShell 5.1 command-start hook, as recorded in the manifest. This mechanism
therefore proves the controlled previous-prompt-to-completion boundary, not arbitrary recovery when
the opening sentinel is absent or already evicted.

Result: **CONFIRMED WITH LIMITATION**.

## V4 - Transcript fidelity, timing, and `Clear-Host`

At each 0, 50, and 200 ms measurement, the transcript previous segment contained every core marker,
including native stderr, mixed stdout/stderr order markers, PowerShell errors, and the exact
`TT_UNICODE_中文_Ω_🙂` value. The segment length was stable across all three measurements.

The transcript also returned exactly 1,000 ordered lines at 0 ms in both the default and enlarged
buffer cases. No additional content appeared at 50 or 200 ms. In this environment, there was no
observable transcript flush delay for the tested completed commands.

After `Write-Output "BEFORE_CLEAR"` followed by `Clear-Host`:

- console snapshot contained `BEFORE_CLEAR`: **No**
- transcript snapshot contained `BEFORE_CLEAR`: **Yes**

The PowerShell and Python errors visible in the evidence were intentionally generated corpus items,
not harness failures.

Limitations: the manual corpus did not terminate the shell or simulate a machine/process crash, and
it did not establish fidelity for full-screen TUI redraw state or every ANSI control sequence.

Result: **CONFIRMED WITH LIMITATION** for the tested ordinary command corpus and immediate
availability.

## V5 - Real resize/reflow

The pane was resized through the Windows Terminal GUI, not by a script:

| Snapshot | Buffer/window | Complete logical reflow markers retained |
|---|---:|---:|
| Wide baseline | `153 x 30` | 13 (`0008`-`0020`) |
| Narrow | `65 x 30` | 5 (`0016`-`0020`) |
| Wide again | `135 x 30` | 4 (`0017`-`0020`) |

Because the ConPTY buffer height remained 30 rows, narrowing caused each logical line to consume
more physical rows and evicted older content. Widening did not restore evicted lines; another older
marker was gone by the next snapshot. Width changes therefore alter not only wrapping but the
recoverable history of a viewport-height console buffer.

Result: **CONFIRMED**. Console extraction must treat resize-driven loss as irreversible truncation,
not as a presentation-only reflow.

## Architecture implications

### Primary candidate: bounded transcript plus session metadata

Promote this candidate on technical fidelity and retention evidence. A production design must not
copy the harness's unbounded session transcript unchanged. Before implementation, the spec/ADR must
define:

- the exact capture scope and why it is necessary;
- a hard byte/time/session capacity bound;
- current-user-only access controls;
- cleanup on success, timeout, uninstall, and next startup after a crash;
- behavior for incomplete transcript tails;
- user-visible opt-in/disable/purge controls;
- OneDrive, roaming-profile, backup, diagnostic, and support-bundle exclusion behavior;
- fail-closed secret screening before any provider transmission; and
- no telemetry, sync, or automatic upload merely because local capture exists.

### Fallback candidate: prompt metadata plus enlarged console buffer

Keep this as the zero-persistence option. It proved complete 1,000-line capture when enlarged before
the command and avoids a content file, but its contract must explicitly state:

- capture is limited to still-retained buffer content;
- `Clear-Host`, resizing, long sessions, and capacity pressure can truncate the result;
- opening sentinels can be evicted;
- supplementary Unicode may be degraded; and
- the buffer-size profile hook is opt-in and restored/removed cleanly.

### Rejected as primary

- Default viewport-only console capture: retains too little long output and is resize-sensitive.
- `Out-Default`, Tee, or pipeline wrappers: previously refuted because they alter semantics or miss
  native output.
- PSReadLine/history alone: supplies command text but not output.
- Windows Terminal shell marks/export buffer as an external query API: no supported invocation path
  was found.

## Final verdict

**MANUAL REAL-WT VERIFICATION COMPLETE - BOUNDED TRANSCRIPT TECHNICALLY PREFERRED; RETENTION DESIGN
REQUIRED BEFORE IMPLEMENTATION**

No further real-WT run is required to resolve V1-V5 for this tested environment. Separate testing is
still required if the future product contract promises crash-tail recovery, full-screen TUI fidelity,
multi-pane/elevated-session behavior, or compatibility across other Windows Terminal/PowerShell
versions.
