# T133 installed native capture validation

Date: 2026-08-30

## Diagnosis

The first real Windows PowerShell 5.1 fresh-shell comparison produced A PASS / B FAIL:

- direct installed executable: native stdout retained and native exit `5` retained;
- managed `tt` Alias: visible native stdout, but `tt last` reported no output.

The reverse-order comparison in a second fresh shell produced B PASS / A PASS. The defect was therefore timing-dependent, not Alias-specific.

The failing retained sequence preserved the native command identity and exit `5`, but published `contentLength=0` / `originalOutputBytes=0`. The contextual `tt last` record was correctly marked and strict previous-command selection reached that empty native record. Output disappeared between visible native emission and transcript staging/final record publication, before `PreviousCommandRetriever`.

The installed loader did not drift. Before remediation, repository source and embedded profile were byte-identical, and the installed profile was the exact source profile with only `__TT_EXECUTABLE_PATH__` replaced. T132 added the managed Alias but did not remove T131 drain functions.

Root cause: the prompt wrapper synchronously flushed transcript options, called `Stop-Transcript`, and only then checked `OutputToLog` / `OutputBeingLogged` with a zero-duration quiet interval. An async native output producer could enqueue after the pre-stop flush but after the sink was stopped. The post-stop drain could then report `transcript-drained=true` even though the visible output never reached the transcript. Stable snapshot reads correctly stabilized on that already-incomplete file.

## Remediation

The installed profile now requires a bounded 50 ms producer-quiet interval after synchronous `FlushContentToDisk` and before `Stop-Transcript`. After Stop it verifies the queues once more. `transcript-drained=true` is propagated only when both checks succeed. Snapshot, retained-record, contextual marker, native-exit attribution, spinner, privacy, and provider contracts were not changed.

The installer/profile contract test compares the installer-generated profile exactly with the repository profile after executable-token replacement and checks the producer-drain ordering. The publish gate performs the same exact installed-loader check, verifies fresh-shell Alias resolution and target consistency, and then runs the real installed-product stress test.

## Results

- Installed fresh-shell direct/Alias matrix after remediation: PASS / PASS.
- Native cases: stdout + exit 0, stdout + exit 5, stderr + exit 7, short stdout, long stdout, and rapid repeated failures: PASS.
- Failed-native stdout + exit 5 stress through the actual installed loader: 50/50 PASS, alternating Alias and direct installed executable invocation.
- Redirected `tt last > file`: PASS; no spinner leakage.
- Native exit values in retained records: PASS.
- T127 stale-exit isolation: PASS.
- T131 contextual self-exclusion: PASS.
- T132 fresh-shell `Get-Command tt` Alias discoverability: PASS.
- Release build: 0 warnings, 0 errors.
- Full automated run: 525 total, 524 passed, 0 failed, 1 skipped. The skipped test is the environment-gated installed-product test; the publish gate enabled it separately and passed 1/1, including the 50/50 stress.

## Artifact and installation consistency

- Artifact: `D:\Projects\Terminal Translator\artifacts\publish\t133-installed-native-capture-acceptance\tt.exe`
- Artifact SHA256: `32896FF015389C352DA21819CC4F5868B55F88B85D8A320BCE09FB96AAB3C91C`
- Installed executable: `C:\Users\gutia\AppData\Local\TerminalTranslator\Versions\32896ff015389c352da21819cc4f5868b55f88b85d8a320bce09fb96aab3c91c\tt.exe`
- Installed executable SHA256: `32896FF015389C352DA21819CC4F5868B55F88B85D8A320BCE09FB96AAB3C91C`
- Installed loader SHA256: `0775B73CD55B71322EBEA3F411D18798D630B867FDC0964EEF79FDE8C5F6C2DF`
- Repository/embedded profile SHA256: `87A3CAF4EE29AC6622AE7860FE70685298894832DAE61D99D39A7ED141E11AA6`
- Installed loader normalized back to the executable token equals repository source ordinally; normalized SHA256 is `87A3CAF4EE29AC6622AE7860FE70685298894832DAE61D99D39A7ED141E11AA6`.
- Fresh PowerShell resolves `tt` as `Alias` and both Alias definition and `$script:TtExecutablePath` point to the installed executable above.
