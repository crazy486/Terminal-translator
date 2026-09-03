# T133 native completion race acceptance — 2026-08-31

## Result

PASS. The 50 ms producer-quiet rule was removed. The accepted Windows PowerShell 5.1 rule combines
engine-joined native readers, two synchronous transcript flush/drain passes, unchanged open-staging
length, post-Stop drain validation, and a stable stopped snapshot. Capture fails closed if those
signals cannot be established.

## Preserved failure evidence

- Runtime: Windows PowerShell `5.1.26100.9168`, real installed loader, ConPTY buffer `240 x 500`.
- Immutable reproductions: `artifacts/publish/t133-race-evidence-20260831-a` and
  `artifacts/publish/t133-race-evidence-20260831-b`.
- Clean structural trace:
  `artifacts/diagnostics/t133-capture-stress-20260831-104546-de8d0e79df614c9c9c9e0d4cbfa8238f.jsonl`.
- Trace SHA-256: `4827A2EBDDF6CF3E285078CA45DD3957B84E16297B4199D7A5B6EC9F640EF05F`.
- Both runs failed at sequence 58 after visible 4 KiB stdout. Before Stop, staging was 4,827 bytes;
  `OutputToLog=0` and `OutputBeingLogged=0` remained empty for 51.24 ms. Stop added only the footer
  (4,938 bytes total); 5/20/50 ms observations showed no growth. The stable snapshot was 4,938 bytes,
  extraction returned zero output, and exit code 5 remained correct.

## Hypothesis disposition

- H1 — rejected for the reproduced failure. There was no producer activity or file growth after the
  observed quiet interval; increasing the constant would not restore bytes already missed.
- H2 — confirmed. The inspected collections cover transcript writer buffers, not the separate native
  console-buffer scrape. Installed-runtime inspection showed `NativeCommandProcessor` waits for the
  process, then reads `RawUI.GetBufferContents`; its bottom-of-buffer cursor case can read a blank row.
- H3 — rejected for the reproduced failure. Stop added the footer and no later enqueue/file growth
  occurred.
- H4 — rejected downstream of acquisition. The stopped staging file was already incomplete before
  snapshot acquisition; boundary selection and publication faithfully retained that incomplete file.

## Implemented invariant

1. While a managed transcript interval is active, PowerShell's captured-application-I/O mode routes
   native stdout/stderr through reader threads that are joined before pipeline completion.
2. Prompt snapshots completion facts, restores the prior host mode, and synchronously invokes
   pipeline completion plus `FlushContentToDisk` twice.
3. Each pass requires both transcript collections empty; the two passes require equal staging length.
4. Stop-Transcript, post-Stop queue validation, stopped-file stability, envelope identity, sequence,
   history, exit metadata, and atomic retention must all agree or capture becomes unavailable.
5. Native file redirection is AST-proven. PowerShell's internal `ParameterBinding(Out-File)` transcript
   record is removed only for that case; redirected bytes remain in the destination file.

PowerShell 5.1 has no public deterministic native-transcription completion signal. The internal mode
and structural checks are therefore a bounded, fail-closed protocol; there is no delay fallback.

## Acceptance evidence

- Final immutable artifact:
  `artifacts/publish/t133-bounded-native-completion-20260831-acceptance-d/tt.exe`
- Artifact and installed executable SHA-256:
  `BA078241BCC3B81CF99CAEDDFC267C6E7F17447B6190EFB4C768375686BB18A0`
- Installed loader SHA-256:
  `8164A1847DCA5F424CAA745FAA04F457AC14BD03AD3BE792178AF2EBCC7A2DD2`
- Final structural trace:
  `artifacts/diagnostics/t133-capture-stress-20260831-123301-c74d5c2c9045480bb08af81972ed3d48.jsonl`
- Trace SHA-256:
  `2931CCAD52519EA53E41AD5E5BF0A3F33364C7333AE95372ABCDEEC90F3470BA`
- Trace totals: 505 selected/current records (one redirect, 500 native stress, two NoOutput
  transitions, two following failed-native commands), 505 published finalizations, 506 producer
  completions including initial prompt, zero retention failures, zero unstable completion passes.
- Mix: short stdout + exit 5; stdout + exit 0; stderr + exit 7; 4 KiB stdout; rapid short processes;
  realistic 8/35 ms gaps and command variation.
- Transition results: comment NoOutput -> failed native PASS; `Set-Location .` NoOutput -> failed
  native PASS. Native stdout redirection wrote the file, retained empty output, and preserved exit 5.
- Provider calls: zero.

## Regression results

- Immutable artifact gate: CLI 237/237, Core 147/147, focused Windows capture 35/35.
- Installed-product acceptance: 1/1 in 22m 11s.
- Complete Windows suite serialized for timing-sensitive ConPTY/global-state isolation: 142 passed,
  0 failed, 1 skipped (the separately passed environment-gated installed acceptance).
- Complete CLI and Core suites passed in the artifact gate. Earlier all-project parallel runs exposed
  only test-resource timing collisions; no product assertion from those runs was accepted as a pass.

## Very short manual smoke

In one fresh Windows PowerShell 5.1 window:

```powershell
cmd /d /c "echo Native capture smoke. & exit 5"
tt last
```

Expected: `tt last` translates `Native capture smoke.` and retains native exit code 5 internally.
