# Real Windows Terminal acceptance fixtures (T126 preparation only)

**Status:** PREPARED — NOT RUN  
**Supported topology:** Windows Terminal + Windows PowerShell 5.1 Desktop  
**Prepared branch / HEAD:** `002-on-demand-terminal-assistance` /
`414ba31171931f1743dc721ed7bf9cd8b69a2df7`  
**Prepared published artifact:**
`src/TerminalTranslator.Cli/bin/Release/net10.0/win-x64/publish/tt.exe`  
**Prepared artifact size / SHA-256:** `74,657,433` bytes /
`C038B4887A494A13420FC52A8029F82ECF4EA8B66BCECF091718CDFEDA738AFD`

This file prepares T127–T129. Nothing below was executed while preparing T126, no box is
pre-checked, and no real profile, Capture installation, session, provider, credential, or API key was
touched. Run these scenarios only in the later real-WT acceptance tasks.

> **SYNTHETIC TEST DATA ONLY.** Every credential-shaped value below is fake and deliberately
> recognizable as synthetic. Never substitute or paste a real OpenAI, DeepSeek, GitHub, Google, or
> other credential. Never dump environment variables or print a configured key. Provider evidence
> records only provider/model/destination and result category.

Use a disposable test profile/user where practical. Commands named `$TtExe` invoke the prepared
published binary directly. Capture profile integration itself resolves `tt`, so the published build
under test must also be the `tt` found on `PATH` before enablement.

### Environment and published-build identity

Purpose:
Collect content-free environment evidence first and prove which published `tt.exe` is under test.

Action:

```powershell
$Repo = 'D:\Projects\Terminal Translator'
$TtExe = (Resolve-Path (Join-Path $Repo 'src\TerminalTranslator.Cli\bin\Release\net10.0\win-x64\publish\tt.exe')).Path
$PathTt = (Get-Command tt -ErrorAction Stop).Source
Get-AppxPackage Microsoft.WindowsTerminal | Select-Object Name, Version
Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture
$PSVersionTable | Select-Object PSVersion, PSEdition, Platform
[pscustomobject]@{
  PublishedTt = $TtExe
  PathTt = $PathTt
  SameExecutable = ([IO.Path]::GetFullPath($TtExe) -eq [IO.Path]::GetFullPath($PathTt))
  TtVersion = (& $TtExe --version)
  Sha256 = (Get-FileHash -LiteralPath $TtExe -Algorithm SHA256).Hash
  Profile = $PROFILE
  Branch = (git -C $Repo branch --show-current)
  Head = (git -C $Repo rev-parse HEAD)
}
$preferencePath = Join-Path $env:LOCALAPPDATA 'TerminalTranslator\capture-preference.json'
if (Test-Path -LiteralPath $preferencePath) {
  Get-Content -Raw -LiteralPath $preferencePath | ConvertFrom-Json | Select-Object state
} else {
  'Capture preference file: absent (defaults to disabled)'
}
```

Expected:

- Windows Terminal version, Windows build, PowerShell `5.1` / `Desktop`, profile path, branch, HEAD,
  executable version, and SHA-256 are visible.
- `SameExecutable` is `True`; the SHA-256 matches the prepared artifact unless a later approved
  rebuild is explicitly recorded.
- No API key name/value or full environment dump appears.

Evidence to record:

- Versions/build, both `tt` paths, SHA-256, branch/HEAD, profile path, and safe preference state.

Result:

- [ ] PASS
- [ ] FAIL

### Profile backup and unrelated-content baseline

Purpose:
Create a recoverable baseline without deleting or rewriting unrelated profile content.

Action:

```powershell
$ProfileBackup = Join-Path $env:TEMP ("tt-profile-before-{0:yyyyMMdd-HHmmss}.ps1" -f (Get-Date))
if (Test-Path -LiteralPath $PROFILE) {
  Copy-Item -LiteralPath $PROFILE -Destination $ProfileBackup
  Get-FileHash -LiteralPath $PROFILE -Algorithm SHA256
} else {
  New-Item -ItemType File -Path $ProfileBackup | Out-Null
  'Profile did not exist before acceptance.'
}
$ProfileBackup
Select-String -LiteralPath $PROFILE -Pattern '# >>> Terminal Translator Capture >>>','# <<< Terminal Translator Capture <<<' -ErrorAction SilentlyContinue
```

Expected:

- The backup path is recorded; existing profile data is not printed unless the Product Owner
  intentionally inspects it locally.
- Any pre-existing TT block state is known before enablement.

Evidence to record:

- Backup path, original profile existence, original hash, and whether a TT-owned block existed.

Result:

- [ ] PASS
- [ ] FAIL

### Capture enablement and managed integration

Purpose:
Verify canonical opt-in, purpose disclosure, TT-owned profile edit, and managed loader installation.

Action:

```powershell
& $TtExe configure --capture enabled
Select-String -LiteralPath $PROFILE -Pattern '# >>> Terminal Translator Capture >>>','# <<< Terminal Translator Capture <<<'
$Loader = Join-Path $env:LOCALAPPDATA 'TerminalTranslator\PowerShell\TerminalTranslator.Profile.ps1'
Get-Item -LiteralPath $Loader | Select-Object FullName, Length, LastWriteTimeUtc
```

Expected:

- Output explains that Capture stores bounded local command output for on-demand assistance in the
  current PowerShell session and is not provider consent.
- Exactly one TT-owned marked block exists; unrelated profile content remains unchanged.
- The managed loader exists below LocalAppData. Capture applies only in a newly opened supported
  shell, not retroactively to the current one.

Evidence to record:

- Disclosure text, marker count, loader path/status, and a local comparison confirming unrelated
  profile content was preserved.

Result:

- [ ] PASS
- [ ] FAIL

### New supported shell activation

Purpose:
Verify a new ordinary Windows PowerShell 5.1 pane loads Capture without disrupting the prompt.

Action:

```powershell
'Open a new Windows Terminal tab using the Windows PowerShell profile, then run:'
$PSVersionTable.PSVersion
$PSVersionTable.PSEdition
Write-Output 'TT_NEW_SHELL_NORMAL_COMMAND'
"capture-session-present=$([bool]$env:TT_CAPTURE_SESSION_ID)"
```

Expected:

- PowerShell is 5.1 Desktop, the normal prompt remains usable, and the ordinary command succeeds.
- The content-free session-presence check is `True`; no nonce value or capture content is printed.
- No repeated maintenance text or unexpected error appears.

Evidence to record:

- Shell version/edition, prompt behavior, marker output, boolean session presence, unexpected output.

Result:

- [ ] PASS
- [ ] FAIL

### Two-pane session isolation

Purpose:
Prove two live PowerShell panes do not select each other's previous command.

Action:

```powershell
# Pane A
Write-Output 'PANE_A_ENGLISH_OUTPUT'

# Open a fresh Pane B, then run this as its first user command
tt last

# Pane B
Write-Output 'PANE_B_ENGLISH_OUTPUT'
tt last

# Return to Pane A
tt last
```

Expected:

- Fresh Pane B reports `No previous command output is available.` and never shows Pane A's marker.
- Pane B later uses only its B marker; Pane A uses only its A marker.
- No session is chosen by most-recent file time.

Evidence to record:

- Pane labels and each result, with confirmation that no cross-pane marker appeared.

Result:

- [ ] PASS
- [ ] FAIL

### Normal `tt last`

Purpose:
Verify the primary inline translation/recommendation experience.

Action:

```powershell
Write-Output 'The package installation completed successfully.'
tt last
```

Expected:

- Inline `[翻译]` and `[建议]` sections appear in the same pane.
- There is one short recommendation, no companion pane, and no replay of the full source output.
- Any suggested command is displayed only.

Evidence to record:

- Result sections, recommendation length, pane behavior, and absence of source replay/execution.

Result:

- [ ] PASS
- [ ] FAIL

### Strict previous command with no output (high priority)

Purpose:
Prove strict-last semantics never search backward for older useful output.

Action:

```powershell
Write-Output 'THIS_OLDER_OUTPUT_SHOULD_NOT_BE_USED'
cd ..
tt last
```

Expected:

- The result is `Previous command has no translatable output.` (or the formally equivalent
  NoOutput message).
- `THIS_OLDER_OUTPUT_SHOULD_NOT_BE_USED` is not translated or sent as fallback context.

Evidence to record:

- Exact message and explicit confirmation that the older marker did not appear.

Result:

- [ ] PASS
- [ ] FAIL

### No previous command in a fresh shell

Purpose:
Verify bootstrap/maintenance does not pollute the first contextual selection.

Action:

```powershell
# Open another fresh supported pane and make this its first user command:
tt last
```

Expected:

- Exact result: `No previous command output is available.`
- Internal bootstrap/prompt maintenance is not selected.

Evidence to record:

- Exact first-command result and whether any internal text appeared.

Result:

- [ ] PASS
- [ ] FAIL

### PowerShell error

Purpose:
Verify a harmless PowerShell error is captured and translated while the shell remains usable.

Action:

```powershell
Get-Item 'Z:\TT_DEFINITELY_MISSING_002'
tt last
Write-Output 'TT_SHELL_STILL_NORMAL'
```

Expected:

- The missing-path error is translated with path/error tokens recognizable and a short recommendation.
- The final marker proves the shell remains normal.

Evidence to record:

- Translation, preserved path/token, recommendation, and final marker.

Result:

- [ ] PASS
- [ ] FAIL

### Native stdout and stderr

Purpose:
Verify both native streams are captured together in transcript-observed order.

Action:

```powershell
cmd /c "echo TT_STDOUT & echo TT_STDERR 1>&2"
tt last
```

Expected:

- Both `TT_STDOUT` and `TT_STDERR` are represented in assistance context/result.
- Their order follows what the Transcript/terminal actually showed; no separate stream panes are promised.

Evidence to record:

- Observed terminal order and assisted result coverage/order.

Result:

- [ ] PASS
- [ ] FAIL

### Safe Git output

Purpose:
Verify repository-local Git output translation without mutating Git state.

Action:

```powershell
git -C 'D:\Projects\Terminal Translator' status
tt last
```

Expected:

- English Git prose is translated; branch/path/status tokens remain recognizable.
- No Git mutation occurs.

Evidence to record:

- Representative translated prose and preserved technical tokens.

Result:

- [ ] PASS
- [ ] FAIL

### Multiline continuation boundary

Purpose:
Verify one multiline logical PowerShell command has one reliable command/output boundary.

Action:

```powershell
$ttLines = @(
  'The first multiline operation completed successfully.'
  'The second multiline operation completed successfully.'
)
$ttLines
tt last
```

Expected:

- Both output lines belong to one previous command and are translated together.
- Command echo/continuation prompts are not treated as output.

Evidence to record:

- Both translated lines and absence of command-echo contamination.

Result:

- [ ] PASS
- [ ] FAIL

### Custom prompt delegation and restoration

Purpose:
Verify the installed wrapper delegates a conspicuous custom prompt without `PS ...>` matching.

Action:

```powershell
$oldPrompt = (Get-Item Function:\prompt).ScriptBlock
$oldTtOriginalPrompt = $script:TtOriginalPrompt
try {
  $script:TtOriginalPrompt = { 'TT-CUSTOM> ' }
  Write-Output 'The custom prompt command completed successfully.'
  tt last
} finally {
  $script:TtOriginalPrompt = $oldTtOriginalPrompt
  Set-Item Function:\prompt -Value $oldPrompt
}
```

Expected:

- `TT-CUSTOM>` appears, normal input continues, and `tt last` translates the intended output.
- Selection does not depend on fixed prompt text; the original wrapper/prompt is restored in `finally`.

Evidence to record:

- Custom prompt, assistance result, and successful restoration.

Result:

- [ ] PASS
- [ ] FAIL

### `Clear-Host` strict semantics

Purpose:
Distinguish presentation clearing from Capture retention while preserving strict-last selection.

Action:

```powershell
Write-Output 'THIS_SHOULD_REMAIN_IN_TRANSCRIPT_HISTORY'
Clear-Host
tt last
```

Expected:

- Strict previous command is `Clear-Host`, so the result is NoOutput.
- The pre-clear marker is not translated as fallback. Clearing presentation does not introduce a
  Console Buffer fallback or delete the underlying bounded transcript history.

Evidence to record:

- NoOutput message and absence of the older marker from assistance.

Result:

- [ ] PASS
- [ ] FAIL

### Ctrl+C bounded interruption

Purpose:
Verify a safely interrupted command either yields reliable bounded output or fails closed.

Action:

```powershell
1..30 | ForEach-Object { Write-Output "TT_INTERRUPT_$_ The operation is still running."; Start-Sleep -Milliseconds 300 }
# Press Ctrl+C after several lines, wait for the prompt, then run:
tt last
```

Expected:

- PASS outcome A: reliable pre-interruption output is translated and an interrupted indication appears.
- PASS outcome B: implementation deems the termination boundary unreliable and clearly fails closed
  without provider output.
- A guessed segment, hang, shell termination, or attribution beyond the boundary is FAIL.

Evidence to record:

- Number of lines before Ctrl+C, which legal outcome occurred, exact notice, shell usability.

Result:

- [ ] PASS
- [ ] FAIL

### Delayed/background output boundary

Purpose:
Verify output arriving after prompt return is not reassigned to the completed foreground command.

Action:

```powershell
$ttTimer = New-Object Timers.Timer
$ttTimer.Interval = 1500
$ttTimer.AutoReset = $false
$ttEvent = Register-ObjectEvent -InputObject $ttTimer -EventName Elapsed -Action { Write-Host 'TT_ASYNC_LATE_OUTPUT' }
$ttTimer.Start()
Write-Output 'TT_FOREGROUND_COMPLETE The foreground operation completed successfully.'
# Wait at the prompt until TT_ASYNC_LATE_OUTPUT appears, then run:
tt last
# Cleanup:
Unregister-Event -SubscriptionId $ttEvent.Id -ErrorAction SilentlyContinue
Remove-Job -Id $ttEvent.Id -Force -ErrorAction SilentlyContinue
$ttTimer.Dispose()
```

Expected:

- Assistance for the completed setup/foreground command does not attribute the later async marker
  backward as its output.
- Cleanup completes and no background event remains.

Evidence to record:

- Timing/order observed, selected content, and cleanup status.

Result:

- [ ] PASS
- [ ] FAIL

### Chinese-only output

Purpose:
Verify Chinese-only output avoids unnecessary translation/provider use.

Action:

```powershell
Write-Output '这是纯中文输出，不应调用翻译服务。'
tt last
```

Expected:

- Exact result: `No translatable English content was found.`
- No consent prompt/provider answer appears. Automated Gate A evidence remains the authoritative
  provider-call-count proof; this manual observation is supporting evidence.

Evidence to record:

- Exact result and absence of consent/provider behavior.

Result:

- [ ] PASS
- [ ] FAIL

### Mixed Chinese and English

Purpose:
Verify language-selective translation and preservation of technical tokens.

Action:

```powershell
Write-Output '安装完成. The package was installed successfully. Error code: E_TEST_42'
tt last
```

Expected:

- Existing Chinese remains, English natural language becomes Chinese, and `E_TEST_42` remains recognizable.

Evidence to record:

- Result text and preservation of the marker/error code.

Result:

- [ ] PASS
- [ ] FAIL

### AI-budget HEAD + TAIL corpus

Purpose:
Generate deterministic output above 8,192 UTF-8 bytes but far below 10,180,000 bytes.

Action:

```powershell
1..300 | ForEach-Object { 'TT_AI_{0:D4} The package installation completed successfully and the bounded diagnostic remains available.' -f $_ }
tt last
```

Expected:

- Local Capture is complete; no local-truncation notice appears.
- `AiHeadTail` disclosure states that the model saw only the beginning and end and the result is a
  summary, not a complete line-by-line translation.

Evidence to record:

- First/last marker coverage, AI-only truncation notice, and absence of local-truncation notice.

Result:

- [ ] PASS
- [ ] FAIL

### Local over-10,180,000-byte corpus

Purpose:
Exercise one bounded, deterministic command slightly above the retained hard cap with HEAD/MIDDLE/TAIL markers.

Action:

```powershell
$ttPayload = 'X' * 1000
1..10400 | ForEach-Object {
  if ($_ -eq 1) { 'TT_HEAD_MARKER The oversized operation started.' }
  elseif ($_ -eq 5200) { 'TT_MIDDLE_MARKER The oversized operation reached its middle.' }
  elseif ($_ -eq 10400) { 'TT_TAIL_MARKER The oversized operation completed.' }
  else { 'TT_LOCAL_{0:D5} {1}' -f $_, $ttPayload }
}
tt last
```

Expected:

- Execution is bounded, local-only, roughly 10.5 MB, and returns to a normal prompt.
- LocalHeadTail disclosure says the local middle was not retained; HEAD and TAIL remain eligible,
  while the MIDDLE marker may be absent locally.
- Because the retained representation also exceeds the AI budget, a distinct AiHeadTail disclosure
  should also appear. The two layers must not be conflated.

Evidence to record:

- Runtime, shell health, both distinct notices, HEAD/TAIL visibility, and whether MIDDLE was absent.

Result:

- [ ] PASS
- [ ] FAIL

### Synthetic secret in `tt ask`

Purpose:
Verify question-only secret screening with fake data and content-free failure output.

Action:

```powershell
$ttSynthetic = ('api' + '_key=TT_SYNTHETIC_ONLY_1234567890')
tt ask "Explain this synthetic test string: $ttSynthetic"
```

Expected:

- Request is blocked with the suspected-sensitive-information notice.
- The warning does not echo the synthetic value and no provider answer appears.

Evidence to record:

- Failure category only; do not copy the synthetic value into the acceptance record.

Result:

- [ ] PASS
- [ ] FAIL

### Synthetic secret in previous command text

Purpose:
Verify contextual command text is part of the exact screened payload.

Action:

```powershell
$null = 'api_key=TT_SYNTHETIC_ONLY_1234567890'; Write-Output 'English safe output for privacy screening.'
tt ask last 'Why did this command run?'
```

Expected:

- The contextual request is blocked before provider access because the captured command text has a
  credential-assignment shape.
- The content-free warning does not echo the value. The assignment is synthetic local test data and
  is not model output.

Evidence to record:

- Failure category only and confirmation of no echoed value/provider answer.

Result:

- [ ] PASS
- [ ] FAIL

### Synthetic secret in previous output

Purpose:
Verify output content is screened independently of command shape.

Action:

```powershell
$ttSynthetic = ('api' + '_key=TT_SYNTHETIC_ONLY_1234567890')
Write-Output $ttSynthetic
tt last
```

Expected:

- Request is blocked before provider access; warning is content-free and does not echo the value.

Evidence to record:

- Failure category only.

Result:

- [ ] PASS
- [ ] FAIL

### Synthetic secrets near selected HEAD and TAIL

Purpose:
Verify both ends of an AI-budget selection pass the same privacy gate.

Action:

```powershell
$ttSynthetic = ('api' + '_key=TT_SYNTHETIC_ONLY_1234567890')
Write-Output $ttSynthetic
1..300 | ForEach-Object { 'TT_PRIVACY_FILL_{0:D4} The diagnostic operation is continuing normally.' -f $_ }
Write-Output $ttSynthetic
tt last
```

Expected:

- The over-budget HEAD + TAIL selection is blocked because synthetic secret-shaped text occurs near
  both selected ends.
- No partial provider request/answer and no echoed secret in the warning.

Evidence to record:

- Failure category and confirmation that neither selected end bypassed screening.

Result:

- [ ] PASS
- [ ] FAIL

### Stateless `tt ask` and language override

Purpose:
Verify inline stateless answers and response-language policy.

Action:

```powershell
tt ask '什么是 Git detached HEAD？'
tt ask 'Please answer in English: what is Git detached HEAD?'
```

Expected:

- First answer defaults to Simplified Chinese; second honors explicit English.
- Both are inline, open no companion pane, and retain no conversation state.

Evidence to record:

- Languages, inline/pane behavior, and absence of conversational carry-over.

Result:

- [ ] PASS
- [ ] FAIL

### `tt ask` Capture independence

Purpose:
Provide manual supporting evidence that question-only assistance does not attach terminal context.

Action:

```powershell
Write-Output 'SECRET_CONTEXT_MARKER_SHOULD_NOT_APPEAR'
tt ask '解释什么是 TCP'
```

Expected:

- The answer explains TCP without referencing the prior marker.
- Production-root automated tests remain the strong proof that Capture retrieval is never constructed.

Evidence to record:

- Whether the marker/context appeared; no payload/debug logging.

Result:

- [ ] PASS
- [ ] FAIL

### Contextual `tt ask last`

Purpose:
Verify focused one-time assistance using the strict previous error context.

Action:

```powershell
Get-Item 'Z:\TT_DEFINITELY_MISSING_ASKLAST'
tt ask last '这个错误为什么发生？'
```

Expected:

- Inline Chinese answer uses the missing-path error as context rather than forcing translation format.
- No companion pane or automatic corrective action appears.

Evidence to record:

- Context relevance, answer language/format, and display-only behavior.

Result:

- [ ] PASS
- [ ] FAIL

### Contextual self-pollution rules

Purpose:
Verify contextual TT commands do not replace the underlying target, while ordinary `tt ask` remains ordinary history.

Action:

```powershell
Write-Output 'COMMAND_A The operation failed with E_SELF_42.'
tt last
tt last
Write-Output 'COMMAND_B The operation failed with E_SELF_43.'
tt ask last '解释它'
tt ask last '再解释一次'
tt ask 'What is TCP?'
tt last
```

Expected:

- Repeated `tt last` continues to target command A; repeated `tt ask last` continues to target command B.
- The final `tt last` treats ordinary question-only `tt ask` as the strict previous ordinary command;
  it does not silently skip it as contextual self-pollution.

Evidence to record:

- Target marker/error for each contextual result and final ordinary-ask behavior.

Result:

- [ ] PASS
- [ ] FAIL

### Generated command remains display-only

Purpose:
Use a harmless marker file as auxiliary manual evidence that model suggestions are never executed.

Action:

```powershell
$marker = Join-Path $env:TEMP 'tt-ai-must-not-create.txt'
Remove-Item -LiteralPath $marker -ErrorAction SilentlyContinue
tt ask "建议一条创建测试文件 '$marker' 的 PowerShell 命令，但不要执行，只把命令作为文本显示。"
Test-Path -LiteralPath $marker
```

Expected:

- Any suggested command/code appears only as answer text; `Test-Path` returns `False`.
- A real model may not return the requested exact command, so this is supporting evidence. Gate F's
  static call path and deterministic automated test are authoritative.

Evidence to record:

- Displayed answer category and `Test-Path=False`; do not execute the suggestion.

Result:

- [ ] PASS
- [ ] FAIL

### Provider consent decline, accept, and scope separation

Purpose:
Verify external transmission disclosure is separate from Capture and separately scoped for question-only/contextual use.

Action:

```powershell
# Use only an already configured credential source. Do not print or inspect its value.
tt ask 'Explain TCP briefly.'
# At the first question-only disclosure, record provider/model/destination and answer N.
tt ask 'Explain TCP briefly.'
# Repeat and answer Y only after reviewing the same disclosure.
Write-Output 'The contextual operation failed with E_CONSENT_42.'
tt ask last '为什么失败？'
# Review the distinct previous-command-context disclosure; decline once, then repeat and accept.
```

Expected:

- Decline sends nothing and returns a content-free unauthorized message.
- Acceptance permits only the disclosed scope. Question-only says no Capture/history; contextual
  scope lists selected command/output/termination/question facts.
- Unchanged provider/destination/model does not repeatedly prompt for an already granted same scope.
- Capture permission is explicitly stated not to authorize provider transmission.

Evidence to record:

- Provider adapter/model/destination (never key), scope wording, decline/accept outcomes, repeat behavior.

Result:

- [ ] PASS
- [ ] FAIL

### Material provider-change re-consent (automated-evidence accepted)

Purpose:
Avoid changing real credentials/configuration merely to prove fingerprint invalidation.

Action:

```powershell
# Do not mutate a real provider or credential for this scenario.
dotnet test 'D:\Projects\Terminal Translator\tests\TerminalTranslator.Cli.Tests\TerminalTranslator.Cli.Tests.csproj' --configuration Release --no-restore -p:UseAppHost=false --filter "FullyQualifiedName~AssistanceConsentPersistenceTests|FullyQualifiedName~AssistanceConsentContractTests"
```

Expected:

- Automated evidence proves provider/destination/relevant-policy changes require a new matching grant.
- Manual acceptance records this as automated-evidence-only unless a disposable provider config exists.

Evidence to record:

- Test totals/result and whether manual config mutation was intentionally skipped.

Result:

- [ ] PASS
- [ ] FAIL

### Capture disablement and optional re-enable

Purpose:
Verify disablement takes effect at a safe prompt, removes future integration, and has no hidden fallback.

Action:

```powershell
tt configure --capture disabled
Write-Output 'The shell continues after capture disablement.'
tt last
# If more acceptance scenarios remain, restore the desired test state:
tt configure --capture enabled
# Open a new supported shell before continuing capture scenarios.
```

Expected:

- Current command/shell continues; the loaded wrapper observes disablement at the next safe prompt.
- `tt last` reports capture disabled and suggests enabling future capture; it does not scrape terminal history.
- Re-enable reinstalls the managed integration for a new shell.

Evidence to record:

- Disclosure/preference output, shell marker, disabled result, and final desired state.

Result:

- [ ] PASS
- [ ] FAIL

### Normal-exit session deletion

Purpose:
Verify the exact session Capture directory is deleted after normal shell exit without opening content.

Action:

```powershell
# Shell A
$ttSessionDir = Join-Path (Join-Path $env:LOCALAPPDATA 'TerminalTranslator\Capture') $env:TT_CAPTURE_SESSION_ID
[pscustomobject]@{ SessionDirectory = $ttSessionDir; ExistsBeforeExit = (Test-Path -LiteralPath $ttSessionDir) }
Write-Output 'The normal-exit capture record completed successfully.'
exit

# In a separate control shell, paste the exact recorded SessionDirectory path:
$endedSessionDir = '<EXACT_RECORDED_SESSION_DIRECTORY>'
1..20 | ForEach-Object { if (-not (Test-Path -LiteralPath $endedSessionDir)) { break }; Start-Sleep -Milliseconds 250 }
Test-Path -LiteralPath $endedSessionDir
```

Expected:

- Exact directory exists before exit and becomes absent after normal cleanup/watcher completion.
- No transcript or retained content is opened or copied.

Evidence to record:

- Exact TT-owned path/status only and cleanup latency.

Result:

- [ ] PASS
- [ ] FAIL

### Exact-PID forced termination and stale cleanup

Purpose:
Verify dead-owner residue is ineligible and cleaned without killing Windows Terminal or another shell.

Action:

```powershell
# Dedicated disposable Shell A
$ttStatePath = Join-Path $env:TEMP 'tt-forced-shell-state.json'
[pscustomobject]@{
  Pid = $PID
  Started = (Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks
  SessionDirectory = (Join-Path (Join-Path $env:LOCALAPPDATA 'TerminalTranslator\Capture') $env:TT_CAPTURE_SESSION_ID)
} | ConvertTo-Json | Set-Content -LiteralPath $ttStatePath -Encoding UTF8
Write-Output 'The forced-termination capture record completed successfully.'

# Separate control pane: verify the exact saved PID/start time before stopping only that process.
$ttDead = Get-Content -Raw -LiteralPath (Join-Path $env:TEMP 'tt-forced-shell-state.json') | ConvertFrom-Json
$ttProcess = Get-Process -Id ([int]$ttDead.Pid) -ErrorAction Stop
if ($ttProcess.StartTime.ToUniversalTime().Ticks -ne [long]$ttDead.Started) { throw 'PID identity changed; do not stop it.' }
Stop-Process -Id ([int]$ttDead.Pid) -Force
# Open one new supported TT shell to trigger stale initialization, then return here:
1..40 | ForEach-Object { if (-not (Test-Path -LiteralPath $ttDead.SessionDirectory)) { break }; Start-Sleep -Milliseconds 250 }
Test-Path -LiteralPath $ttDead.SessionDirectory
```

Expected:

- Only the exact disposable PowerShell PID is stopped; Windows Terminal and other panes remain.
- New TT initialization/watcher cleanup makes the exact dead-session directory absent and never exposes it to `tt last`.

Evidence to record:

- Exact PID/start verification, directory status, cleanup latency, and isolation of other panes.

Result:

- [ ] PASS
- [ ] FAIL

### Capture failure isolation (automated-evidence only)

Purpose:
Cover fault modes without dangerous ACL changes, system-directory deletion, or profile corruption.

Action:

```powershell
# Production has no safe public fault-injection switch. Do not damage ACLs/profile/storage.
dotnet test 'D:\Projects\Terminal Translator\tests\TerminalTranslator.Windows.Tests\TerminalTranslator.Windows.Tests.csproj' --configuration Release --no-restore -p:UseAppHost=false --filter "FullyQualifiedName~CaptureFinalizationFailureIsolationTests|FullyQualifiedName~CaptureForegroundFailureIsolationTests|FullyQualifiedName~CaptureHealthNotificationTests|FullyQualifiedName~RetainedCapturePublicationFailureTests"
```

Expected:

- Automated tests prove shell fail-open behavior, bounded unavailable/restored notices, and candidate/publication isolation.
- Manual destructive fault injection is explicitly skipped because no safe production injection exists.

Evidence to record:

- Test totals/result and explicit `manual fault injection not run for safety` note.

Result:

- [ ] PASS
- [ ] FAIL

### Profile reversal and unrelated-content preservation

Purpose:
Return the profile/integration to the chosen final state without deleting unrelated data.

Action:

```powershell
tt configure --capture disabled
$Loader = Join-Path $env:LOCALAPPDATA 'TerminalTranslator\PowerShell\TerminalTranslator.Profile.ps1'
[pscustomobject]@{
  LoaderExists = (Test-Path -LiteralPath $Loader)
  BeginMarkers = @((Select-String -LiteralPath $PROFILE -Pattern '# >>> Terminal Translator Capture >>>' -ErrorAction SilentlyContinue)).Count
  EndMarkers = @((Select-String -LiteralPath $PROFILE -Pattern '# <<< Terminal Translator Capture <<<' -ErrorAction SilentlyContinue)).Count
}
# Compare locally with the exact $ProfileBackup recorded at the beginning; never delete $PROFILE.
Compare-Object (Get-Content -LiteralPath $ProfileBackup -ErrorAction SilentlyContinue) (Get-Content -LiteralPath $PROFILE -ErrorAction SilentlyContinue)
```

Expected:

- TT block markers and managed loader are absent after disable/reversal.
- Unrelated pre-existing profile content matches the backup. The profile itself is never wholesale deleted.

Evidence to record:

- Loader/marker status and content-free summary of profile comparison.

Result:

- [ ] PASS
- [ ] FAIL

### Final test-data cleanup

Purpose:
Remove only acceptance-owned temporary objects/files and leave Capture in the Product Owner's chosen final state.

Action:

```powershell
if ($oldTtOriginalPrompt) { $script:TtOriginalPrompt = $oldTtOriginalPrompt }
if ($oldPrompt) { Set-Item Function:\prompt -Value $oldPrompt }
Get-EventSubscriber -ErrorAction SilentlyContinue | Where-Object SourceIdentifier -like '*Elapsed*' | Unregister-Event -ErrorAction SilentlyContinue
Get-Job -ErrorAction SilentlyContinue | Where-Object Name -like '*Elapsed*' | Remove-Job -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $env:TEMP 'tt-ai-must-not-create.txt') -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $env:TEMP 'tt-forced-shell-state.json') -ErrorAction SilentlyContinue
Remove-Variable ttPayload,ttSynthetic,ttTimer,ttEvent,ttLines,ttName -ErrorAction SilentlyContinue
# Choose exactly one final Capture state; do not run both:
# tt configure --capture enabled
# tt configure --capture disabled
```

Expected:

- Custom prompt/event/job/marker fixtures are gone, unrelated jobs/files/profile data remain untouched,
  and the selected final Capture state is recorded.
- The profile backup is retained until reversal comparison is approved, then may be removed manually.

Evidence to record:

- Cleanup checklist, chosen Capture state, remaining backup path, and any cleanup failure.

Result:

- [ ] PASS
- [ ] FAIL

## Scenario allocation and evidence limits

This guide contains **37 manual checklist scenarios**. Suggested later-task allocation:

- T127 functional/topology: environment through async/background boundary (16 scenarios).
- T128 assistance/language/privacy: Chinese-only through provider consent (14 scenarios).
- T129 lifecycle/failure/reversal: provider-change evidence through cleanup (7 scenarios).

The following claims intentionally rely on automated evidence as the authoritative proof:

- exact provider call count for Chinese-only/no-output/privacy-blocked paths;
- capture independence of question-only `tt ask`;
- deterministic model-command display-only data flow (Gate F); real-model marker check is auxiliary;
- re-consent after a material provider configuration change when no disposable provider is used;
- storage/publication/finalization fault injection because production exposes no safe test switch.

Do not begin T127–T129 merely by checking this preparation file. Their separate evidence artifacts
must record the real GUI topology and actual PASS/FAIL outcomes.
