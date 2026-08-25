# Manual Real-WT Verification

This stage is required because the Codex command runner has `PIPE` stdin/stdout/stderr even though
its ancestor process is Windows Terminal. These instructions must run in a fresh, genuinely
interactive Windows Terminal pane using Windows PowerShell 5.1.

The harness is temporary and session-scoped. It does not read or modify the user's PowerShell
profile, does not call a translation provider, and writes evidence only under `../results/`.

## 1. Start a clean session

Open a new Windows Terminal pane whose shell is `powershell.exe` 5.1. From the repository root:

```powershell
. .\research\tt-last-capture\codex-verification\manual\Start-RealWtVerification.ps1
```

Keep this pane open until all steps are complete. Do not run the steps from PowerShell 7, VS Code's
terminal task runner, or a redirected script host.

## 2. V1 and V4 core corpus

Run these as two separate interactive commands:

```powershell
Invoke-TtCoreCorpus
Capture-TtBoth -Name v1-v4-core
```

The first command emits PowerShell output, host output, native stdout/stderr, a PowerShell error,
git output/error, available npm/python/dotnet evidence, mixed ordering markers, and Unicode. The
second command captures the preceding completed segment from both sources at 0/50/200 ms.

## 3. V3 previous-command boundary scenarios

Run every line below as a separate interactive command. `Capture-TtBoth` is intentionally the next
command: this tests that its own command echo does not replace the preceding command identity.

```powershell
Write-Output "CMD1_UNIQUE"
Write-Output "CMD2_UNIQUE"
Capture-TtBoth -Name v3-simple

Write-Output "HAS_OUTPUT"
cd .
Capture-TtBoth -Name v3-empty
```

For a real continuation prompt, paste this multiline command, wait for it to finish, then capture:

```powershell
Write-Output @(
  "TT_CONTINUATION_A"
  "TT_CONTINUATION_B"
)
Capture-TtBoth -Name v3-continuation
```

Test a custom prompt without changing the prompt-boundary algorithm:

```powershell
Set-TtVerificationCustomPrompt
Write-Output "TT_CUSTOM_PROMPT_OUTPUT"
Capture-TtBoth -Name v3-custom-prompt
Set-TtVerificationDefaultPrompt
```

Test explicit session association. The switch creates a new GUID and a separate metadata sidecar;
the reader filters console/transcript sentinels by that GUID rather than selecting a latest file:

```powershell
Switch-TtVerificationSession
Write-Output "TT_SESSION_B_ONLY"
Capture-TtBoth -Name v3-session-b
```

## 4. V2 default and enlarged 1000-line retention

First record the control using the buffer size inherited by this fresh pane:

```powershell
Write-TtLongCorpus
Capture-TtBoth -Name v2-default
```

Then enlarge the buffer before producing a new 1000-line corpus:

```powershell
Set-TtVerificationBufferHeight -Height 1800
Write-TtLongCorpus
Capture-TtBoth -Name v2-enlarged
```

If the fresh pane already started with a buffer height of 1500 or more, the default-buffer control
is not valid. Do not shrink the buffer; the recorded environment will make that limitation explicit.

## 5. V4 Clear-Host behavior

Run each line separately:

```powershell
Write-Output "BEFORE_CLEAR"
Clear-Host
Capture-TtBoth -Name v4-after-clear
```

This records capability only. It does not decide whether pre-clear output is a product requirement.

## 6. V5 real resize/reflow

Resize the Windows Terminal pane to a comfortably wide width (about 120 columns), then run:

```powershell
Write-TtReflowCorpus
Capture-TtBoth -Name v5-wide-baseline
```

Drag the actual Windows Terminal pane to about 40 columns and run:

```powershell
Capture-TtConsoleSnapshot -Name v5-narrow
```

Expand the pane back to about 120 columns and run:

```powershell
Capture-TtConsoleSnapshot -Name v5-wide-again
```

Do not use a script to fake the window resize; the GUI operation is the evidence being requested.

## 7. Finish

```powershell
Complete-TtVerification
```

This stops the transcript and restores the original in-memory prompt function. Close the temporary
pane afterward; the enlarged screen-buffer setting is scoped to that console session.

Send the assistant the generated directory path printed as `TT_CAPTURE_RESULTS=...`. The assistant
will read the machine-generated JSON/text evidence, classify V1-V5, and create the final report.
