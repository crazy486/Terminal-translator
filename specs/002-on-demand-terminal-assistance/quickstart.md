# Quickstart Validation: On-Demand Terminal Assistance

This is the post-implementation validation guide for Feature 002. It is not an implementation script.
Run automated tests first, then complete the topology-dependent cases in a real Windows Terminal pane
using Windows PowerShell 5.1 Desktop.

## Prerequisites

- Windows Terminal
- Windows PowerShell 5.1 Desktop (`powershell.exe`)
- .NET SDK specified by `global.json`
- A test provider endpoint/configuration that contains no production secrets
- A dedicated test user/profile or a backed-up CurrentUser/CurrentHost PowerShell profile
- Feature 002 build available as `tt`

Do not use sensitive command output in provider-path acceptance tests. Secret-blocking tests should
use synthetic fixtures from the repository test corpus.

## 1. Automated gates

From the repository root:

```powershell
dotnet test TerminalTranslator.sln --configuration Release --no-restore -p:UseAppHost=false
```

The suite must cover:

- exact retained-store accounting at 10,180,000 bytes;
- oldest-complete eviction and individually oversized local HEAD + TAIL records;
- UTF-8-safe local and AI selection;
- session/boundary/self-pollution/corruption outcomes;
- whole-output English eligibility;
- privacy gate and consent fingerprint behavior;
- `tt ask` stateless isolation and `tt ask last` context;
- inline renderer/provider failure behavior; and
- all Feature 001 regression gates for `tt start/on/off/status`, ConPTY, IPC, and live translation.
- configure grammar for capture-only, unchanged provider-only, rejected combined/partial options,
  and rollback after a simulated profile/config write failure.

Publish smoke gate:

```powershell
dotnet publish src/TerminalTranslator.Cli/TerminalTranslator.Cli.csproj --configuration Release
```

## 2. Capture lifecycle setup

In Windows PowerShell 5.1:

```powershell
tt configure --capture enabled
```

Expected:

- Capture purpose is explained before enablement completes.
- Only the marked TT profile integration is added; existing profile content remains unchanged.
- A newly opened Windows PowerShell 5.1 pane initializes Capture automatically without asking again.
- Session content is below `%LOCALAPPDATA%\TerminalTranslator`, not Documents/Desktop.
- Capture directory ACL does not grant broad access to other local users.

Disable/restore check:

```powershell
tt configure --capture disabled
tt last
```

Expected: capture is disabled, future-capture guidance is shown, no provider call occurs, and there is
no fallback. Re-enable and open a fresh pane before continuing.

## 3. Strict previous-command behavior

### Ordinary success

```powershell
git status
tt last
```

Expected: inline `[翻译]` and `[建议]`; no companion pane; command text is not translated; full original
output is not repeated.

### PowerShell error

```powershell
Get-Item Z:\TT_DOES_NOT_EXIST
tt last
```

Expected: the error output is translated with useful exit/termination context; paths/error tokens
remain recognizable.

### Native stdout/stderr observed order

Run the repository's native mixed-stream fixture, then `tt last`. Expected: one combined result using
the order observed in Transcript, not separate stdout/stderr translation sections.

### No-output command never skips backward

```powershell
git status
Set-Location ..
tt last
```

Expected: the product reports that `Set-Location ..`/`cd ..` has no translatable output. It does not
translate `git status`.

### No previous command

Open a freshly integrated pane and run `tt last` before another eligible command. Expected:

```text
No previous command output is available.
```

### Multiline and custom prompt

In the dedicated test profile, define the custom prompt **before** the managed TT block, then enable
capture/open a new pane so the loader wraps the effective custom prompt. Do not redefine `prompt`
after TT initialization for this acceptance case.

```powershell
& {
  Write-Output "first English line"
  Write-Output "second English line"
}
tt last
```

Expected: the multiline submission is one command and custom prompt text remains intact. No fixed
`PS ...>` matching is required.

### Self-pollution

```powershell
Write-Output "Original English diagnostic"
tt last
tt last
tt ask last "再解释一次"
```

Expected: contextual TT command echo/output never replaces the original eligible command identity.

## 4. Presentation, interruption, and async boundaries

### `Clear-Host`

```powershell
Write-Output "English before clear"
Clear-Host
tt last
```

Expected: strict previous command is `Clear-Host`, which has no translatable output. Capture history
still exists for retention/diagnostic integrity, but the product does not skip back to the write.

### Ctrl+C

Start a command that prints a known English marker, then interrupt it with Ctrl+C and run `tt last`.

Expected: if completion/output/interruption boundaries agree, translate captured output and inform the
model it was interrupted. If they do not agree, show reliable-recovery failure and send nothing.

### Background output

Start a background/job scenario whose output appears after prompt return. Expected: later output is
not retroactively attached to the previously closed command. V1 need not explain its provenance.

## 5. Language behavior

```powershell
Write-Output "Short error"
tt last
```

Expected: short English remains eligible; there is no minimum-length threshold.

```powershell
Write-Output "操作已经完成"
tt last
```

Expected: `No translatable English content was found.` and zero provider requests.

```powershell
Write-Output "安装失败: package Contoso.Tools returned error E401 at C:\work\a.txt"
tt last
```

Expected: Chinese remains, English natural language is translated, and package/path/error code remain
recognizable.

## 6. Distinct long-output states

### Complete local Capture, below AI budget

Produce English output that fits the configured AI input policy, then run `tt last`. Expected: the
complete local output is eligible for the privacy gate and no truncation notice appears.

### Complete local Capture, above AI budget

Produce a completed output larger than the provider input policy but smaller than the retained cap.
Run `tt last`.

Expected:

- Local Capture remains complete.
- Only UTF-8-safe HEAD + TAIL is eligible for transmission.
- The result explicitly says the provider saw only beginning/end and produced a summarized
  translation.
- No local-capture-truncation notice appears.

### Single completed command above 10,180,000 retained bytes

Use a deterministic fixture to produce one output whose finalized record is larger than 10,180,000
bytes. While it runs, verify normal output and exit behavior are unaffected even though staging grows
beyond the retained cap. After the prompt returns, run `tt last` and `tt ask last "总结失败原因"`.

Expected:

- Completed-command retained bytes are at most exactly 10,180,000 at the safe boundary.
- The command remains identifiable with valid boundaries.
- The retained representation contains bounded beginning/end and is marked locally truncated.
- Both commands explicitly state that the local Capture middle is no longer retained.
- If provider budget further reduces the retained representation, a second distinct AI-input notice
  appears.

### Rolling retention

Generate multiple complete records whose cumulative retained size crosses the cap. Expected: oldest
complete records are evicted, latest complete records are preferred, no ordinary record is cut, and
the prompt/command exit codes remain unchanged.

## 7. Privacy and consent

For each of `tt last`, `tt ask`, and `tt ask last`, use synthetic secret fixtures in command, output,
question, and HEAD + TAIL positions.

Expected:

- The exact outbound selection is blocked before the provider receives any portion.
- The notice says suspected sensitive content was not sent and does not echo it.
- Enabling Capture alone never grants provider consent.
- Repeated explicit requests may reuse consent while provider/destination/relevant scope is unchanged.
- A trust-boundary-affecting provider/configuration change requires consent again.
- Raw local Capture never appears in normal logs, telemetry, or diagnostics bundles.

## 8. Stateless questions

```powershell
tt ask "Git rebase 是什么？"
tt ask "Answer this question in English: what does git status do?"
```

Expected: first answer defaults to Simplified Chinese; second follows explicit English. Neither reads
Capture or remembers the other request.

Also verify `tt ask last` without a question is a usage error, `tt ask --last ...` is rejected, and
quoted, multi-token, and Unicode questions arrive unchanged at the request-selection boundary.

```powershell
npm install
tt ask last "这个报错为什么发生？"
```

Expected: the exact prior command/output/termination context is used after the same retrieval,
consent, privacy, and truncation rules as `tt last`. The response is stateless.

For all AI answers that include commands, verify the suggested text remains displayed only and no
process/file change occurs automatically.

## 9. Independent sessions

Open two Windows Terminal panes with Windows PowerShell 5.1. Produce unique output in each, with pane
B's capture modified most recently. Run `tt last` in pane A and then B.

Expected: each pane retrieves only its own previous command. Closing/reopening a pane never makes the
old session eligible.

## 10. Failure and cleanup

### Capture failure

Use a controlled test fault to fail transcript start, retention publication, compaction, and next
interval restart separately.

Expected:

- User command input/output/exit behavior and shell lifetime remain unchanged.
- One capture-unavailable notice appears near the prompt, not at every prompt.
- `tt last`/`tt ask last` fail closed and do not return an older record.
- A validated recovery may show one restored notice.

Publication fault tests must cover: incomplete candidate; complete candidate before manifest swap;
manifest temporary write/flush; immediately after atomic swap; superseded deletion failure; next-
transcript restart failure; and oversized staging deletion failure. Expected: exactly one committed
generation is authoritative, its logical retained bytes are within the cap, no partial candidate is
readable, failed newest capture does not cause strict-last to skip backward, and the shell survives.
After any raw-staging, candidate, transaction, or superseded-generation deletion failure, assert
capture opens no new staging, leaves at most one finalization residue set for that command, never
accumulates another set, and resumes only after the entire set is cleaned.

### Corruption

Using test-only fixtures, break a boundary, session association, manifest generation, and retained
record reference. Expected: `[tt] Previous command output could not be recovered reliably.` and zero
provider transmission.

### Cleanup

Close one pane normally and verify its exact session directory is deleted. Then create a test crash
residue, terminate the PowerShell process forcibly, and invoke any TT initialization from a new
session. Expected: stale data is deleted and never returned by the new session. If initialization is
question-only `tt ask`, only content-free owner-manifest cleanup may occur; no command record is
opened or attached to the question.

## 11. Acceptance record

Record Windows Terminal version, Windows build, PowerShell `$PSVersionTable`, published TT build,
provider test adapter, and pass/fail evidence for every scenario. PIPE-only automation is supporting
evidence, not a replacement for the real WT/PS5.1 acceptance record.
