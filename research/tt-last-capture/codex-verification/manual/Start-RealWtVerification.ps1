[CmdletBinding()]
param(
    [string]$ResultsRoot
)

$ErrorActionPreference = 'Stop'
$verificationRoot = Split-Path -Parent $PSScriptRoot
$probe = Join-Path $verificationRoot 'ConsoleBufferProbe\bin\Release\net10.0\TT.CaptureVerification.ConsoleBufferProbe.exe'
if (-not (Test-Path -LiteralPath $probe)) {
    throw "Probe executable not found. Build ConsoleBufferProbe in Release first: $probe"
}

if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) {
    throw 'This manual verification must run in Windows PowerShell 5.1 (Desktop edition).'
}

if ($Host.Name -ne 'ConsoleHost' -or [string]::IsNullOrWhiteSpace($env:WT_SESSION)) {
    throw 'This manual verification must run interactively in Windows Terminal ConsoleHost.'
}

if ([string]::IsNullOrWhiteSpace($ResultsRoot)) {
    $ResultsRoot = Join-Path $verificationRoot 'results'
}

$sessionId = [guid]::NewGuid().ToString('N')
$folderName = 'real-wt-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $sessionId.Substring(0, 8)
$sessionResults = Join-Path $ResultsRoot $folderName
New-Item -ItemType Directory -Force -Path $sessionResults | Out-Null

$global:TT_CAPTURE_SESSION_ID = $sessionId
$global:TT_CAPTURE_RESULTS = $sessionResults
$global:TT_CAPTURE_PROBE = $probe
$global:TT_CAPTURE_TRANSCRIPT = Join-Path $sessionResults 'transcript.txt'
$global:TT_CAPTURE_METADATA = Join-Path $sessionResults ("metadata-$sessionId.jsonl")
$global:TT_CAPTURE_PROMPT_ORDINAL = 0
$global:TT_CAPTURE_PROMPT_STYLE = 'default'
$global:TT_CAPTURE_UTF8 = New-Object System.Text.UTF8Encoding($false)
$global:TT_CAPTURE_ORIGINAL_PROMPT = (Get-Command prompt -CommandType Function -ErrorAction SilentlyContinue).ScriptBlock

& $probe environment --output (Join-Path $sessionResults 'environment-native-probe.json')
if ($LASTEXITCODE -ne 0) {
    throw "Native environment probe failed with exit code $LASTEXITCODE."
}

$psEnvironment = [ordered]@{
    Schema = 'tt-capture-verification/manual-powershell-environment/v1'
    CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    PSVersionTable = ($PSVersionTable | Out-String)
    HostName = $Host.Name
    WtSession = $env:WT_SESSION
    ProcessId = $PID
    ProcessName = (Get-Process -Id $PID).ProcessName
    CurrentDirectory = (Get-Location).Path
    ProbeExeSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $probe).Hash
    ProbeDllSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath ([System.IO.Path]::ChangeExtension($probe, '.dll'))).Hash
    GitBranch = (& git branch --show-current)
    GitHead = (& git rev-parse HEAD)
    InitialGitStatus = @(& git status --short)
}
$wtProcess = Get-Process -Name WindowsTerminal -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -ne $wtProcess) {
    try {
        $wtItem = Get-Item -LiteralPath $wtProcess.Path
        $psEnvironment.WindowsTerminal = [ordered]@{
            ProcessId = $wtProcess.Id
            Path = $wtProcess.Path
            FileVersion = $wtItem.VersionInfo.FileVersion
            ProductVersion = $wtItem.VersionInfo.ProductVersion
        }
    } catch {
        $psEnvironment.WindowsTerminal = [ordered]@{ ProcessId = $wtProcess.Id; Error = $_.Exception.Message }
    }
}
[System.IO.File]::WriteAllText(
    (Join-Path $sessionResults 'environment-powershell.json'),
    ($psEnvironment | ConvertTo-Json -Depth 8),
    $global:TT_CAPTURE_UTF8)

Start-Transcript -LiteralPath $global:TT_CAPTURE_TRANSCRIPT -Force | Out-Null

function global:prompt {
    $successStatus = $?
    $nativeExitCode = $global:LASTEXITCODE
    $history = @(Get-History -Count 1 -ErrorAction SilentlyContinue) | Select-Object -Last 1
    $global:TT_CAPTURE_PROMPT_ORDINAL++
    $ordinal = $global:TT_CAPTURE_PROMPT_ORDINAL
    $cursor = $Host.UI.RawUI.CursorPosition
    $buffer = $Host.UI.RawUI.BufferSize
    $window = $Host.UI.RawUI.WindowSize
    $historyId = $null
    $commandText = $null
    if ($null -ne $history) {
        $historyId = $history.Id
        $commandText = $history.CommandLine
    }

    $record = [ordered]@{
        Schema = 'tt-capture-verification/prompt-boundary/v1'
        SessionId = $global:TT_CAPTURE_SESSION_ID
        PromptOrdinal = $ordinal
        Phase = 'CommandEnd'
        HistoryId = $historyId
        CommandText = $commandText
        TimestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
        LastExitCode = $nativeExitCode
        SuccessStatus = $successStatus
        ConsoleCursor = [ordered]@{ X = $cursor.X; Y = $cursor.Y }
        ConsoleBuffer = [ordered]@{ Width = $buffer.Width; Height = $buffer.Height }
        ConsoleWindow = [ordered]@{ Width = $window.Width; Height = $window.Height }
        StartBoundary = 'NOT PROVEN: no independent PS5.1 command-start hook is used'
    }
    $json = $record | ConvertTo-Json -Compress -Depth 8
    [System.IO.File]::AppendAllText($global:TT_CAPTURE_METADATA, $json + [Environment]::NewLine, $global:TT_CAPTURE_UTF8)

    $marker = 'TT_BOUNDARY:{0}:{1:D4}' -f $global:TT_CAPTURE_SESSION_ID, $ordinal
    Write-Host $marker
    $global:LASTEXITCODE = $nativeExitCode
    if ($global:TT_CAPTURE_PROMPT_STYLE -eq 'custom') {
        return 'CUSTOM> '
    }
    return ('PS {0}> ' -f $executionContext.SessionState.Path.CurrentLocation)
}

function global:Get-TtVerificationResultPath {
    param([Parameter(Mandatory = $true)][string]$Name, [Parameter(Mandatory = $true)][string]$Suffix)
    $safe = $Name -replace '[^A-Za-z0-9_.-]', '_'
    return Join-Path $global:TT_CAPTURE_RESULTS ("$safe-$Suffix")
}

function global:Capture-TtConsoleSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Name)
    $path = Get-TtVerificationResultPath -Name $Name -Suffix 'console.json'
    & $global:TT_CAPTURE_PROBE capture --session $global:TT_CAPTURE_SESSION_ID --output $path
    if ($LASTEXITCODE -ne 0) { throw "Console capture failed with exit code $LASTEXITCODE." }
    Write-Host "CONSOLE_RESULT=$path"
}

function global:Read-TtTranscriptShared {
    $stream = New-Object System.IO.FileStream(
        $global:TT_CAPTURE_TRANSCRIPT,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally {
        $stream.Dispose()
    }
}

function global:Get-TtTranscriptPreviousSegment {
    param([Parameter(Mandatory = $true)][string]$Text)
    $pattern = 'TT_BOUNDARY:' + [regex]::Escape($global:TT_CAPTURE_SESSION_ID) + ':(?<ordinal>[0-9]+)'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -lt 2) {
        return [ordered]@{ Found = $false; Reason = 'Fewer than two completed prompt sentinels were readable'; BoundaryCount = $matches.Count }
    }
    $start = $matches[$matches.Count - 2]
    $end = $matches[$matches.Count - 1]
    $segmentStart = $start.Index + $start.Length
    $segmentLength = $end.Index - $segmentStart
    return [ordered]@{
        Found = $true
        StartOrdinal = [int]$start.Groups['ordinal'].Value
        EndOrdinal = [int]$end.Groups['ordinal'].Value
        BoundaryCount = $matches.Count
        Text = $Text.Substring($segmentStart, $segmentLength)
    }
}

function global:Capture-TtTranscriptLast {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Name)
    $measurements = @()
    $previousTarget = 0
    foreach ($target in @(0, 50, 200)) {
        $delay = $target - $previousTarget
        if ($delay -gt 0) { Start-Sleep -Milliseconds $delay }
        $text = Read-TtTranscriptShared
        $rawPath = Get-TtVerificationResultPath -Name $Name -Suffix ("transcript-{0}ms.txt" -f $target)
        [System.IO.File]::WriteAllText($rawPath, $text, $global:TT_CAPTURE_UTF8)
        $parsed = Get-TtTranscriptPreviousSegment -Text $text
        $measurements += [pscustomobject]@{
            DelayMs = $target
            FileLengthBytes = (Get-Item -LiteralPath $global:TT_CAPTURE_TRANSCRIPT).Length
            PreviousSegment = $parsed
            RawSnapshot = $rawPath
        }
        $previousTarget = $target
    }
    $resultPath = Get-TtVerificationResultPath -Name $Name -Suffix 'transcript.json'
    [System.IO.File]::WriteAllText(
        $resultPath,
        ([ordered]@{
            Schema = 'tt-capture-verification/transcript-read/v1'
            CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            SessionId = $global:TT_CAPTURE_SESSION_ID
            TranscriptPath = $global:TT_CAPTURE_TRANSCRIPT
            Measurements = $measurements
        } | ConvertTo-Json -Depth 12),
        $global:TT_CAPTURE_UTF8)
    Write-Host "TRANSCRIPT_RESULT=$resultPath"
}

function global:Capture-TtBoth {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Name)
    Capture-TtConsoleSnapshot -Name $Name
    Capture-TtTranscriptLast -Name $Name
}

function global:Set-TtVerificationBufferHeight {
    [CmdletBinding()]
    param([int]$Height = 1800)
    $path = Get-TtVerificationResultPath -Name ("buffer-height-$Height") -Suffix 'json'
    & $global:TT_CAPTURE_PROBE set-height --height $Height --output $path
    if ($LASTEXITCODE -ne 0) { throw "Set-height probe failed with exit code $LASTEXITCODE." }
    Get-Content -Raw -LiteralPath $path
}

function global:Set-TtVerificationCustomPrompt {
    $global:TT_CAPTURE_PROMPT_STYLE = 'custom'
    Write-Host 'PROMPT_STYLE=custom'
}

function global:Set-TtVerificationDefaultPrompt {
    $global:TT_CAPTURE_PROMPT_STYLE = 'default'
    Write-Host 'PROMPT_STYLE=default'
}

function global:Switch-TtVerificationSession {
    $old = $global:TT_CAPTURE_SESSION_ID
    $global:TT_CAPTURE_SESSION_ID = [guid]::NewGuid().ToString('N')
    $global:TT_CAPTURE_PROMPT_ORDINAL = 0
    $global:TT_CAPTURE_METADATA = Join-Path $global:TT_CAPTURE_RESULTS ("metadata-$($global:TT_CAPTURE_SESSION_ID).jsonl")
    Write-Host "OLD_SESSION=$old"
    Write-Host "NEW_SESSION=$($global:TT_CAPTURE_SESSION_ID)"
}

function global:Invoke-TtCoreCorpus {
    Write-Output 'TT_T_WRITE_OUTPUT'
    Write-Host 'TT_T_WRITE_HOST'
    cmd.exe /d /c 'echo TT_T_NATIVE_STDOUT'
    cmd.exe /d /c 'echo TT_T_NATIVE_STDERR 1>&2'
    Get-ChildItem 'Z:\__TT_TRANSCRIPT_NONEXISTENT__' -ErrorAction Continue
    git status
    git foo
    if (Get-Command npm.exe -ErrorAction SilentlyContinue) {
        npm --version
        npm help __TT_NONEXISTENT_HELP_TOPIC__
    }
    if (Get-Command python.exe -ErrorAction SilentlyContinue) {
        python -c 'raise RuntimeError("TT_T_PYTHON_ERROR")'
    }
    if (Get-Command dotnet.exe -ErrorAction SilentlyContinue) {
        dotnet --version
    }
    cmd.exe /d /c '(echo TT_ORDER_1_STDOUT& echo TT_ORDER_2_STDERR 1>&2& echo TT_ORDER_3_STDOUT)'
    $unicodeMarker = 'TT_UNICODE_' + [char]0x4E2D + [char]0x6587 + '_' + [char]0x03A9 + '_' + [char]0xD83D + [char]0xDE42
    Write-Output $unicodeMarker
}

function global:Write-TtLongCorpus {
    1..1000 | ForEach-Object { Write-Output ('TT_LINE_{0:D4}' -f $_) }
}

function global:Write-TtReflowCorpus {
    1..20 | ForEach-Object {
        $payload = ('X' * 132)
        Write-Output ('TT_REFLOW_{0:D4}_{1}_END_{0:D4}' -f $_, $payload)
    }
}

function global:Complete-TtVerification {
    $completion = [ordered]@{
        Schema = 'tt-capture-verification/manual-completion/v1'
        CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        FinalSessionId = $global:TT_CAPTURE_SESSION_ID
        FinalGitStatus = @(& git status --short)
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $global:TT_CAPTURE_RESULTS 'completion.json'),
        ($completion | ConvertTo-Json -Depth 6),
        $global:TT_CAPTURE_UTF8)
    try { Stop-Transcript | Out-Null } catch { Write-Warning $_.Exception.Message }
    if ($null -ne $global:TT_CAPTURE_ORIGINAL_PROMPT) {
        Set-Item -Path Function:\global:prompt -Value $global:TT_CAPTURE_ORIGINAL_PROMPT
    } else {
        Remove-Item -Path Function:\global:prompt -ErrorAction SilentlyContinue
    }
    Write-Host "TT verification complete. Results: $($global:TT_CAPTURE_RESULTS)"
}

$manifest = @"
TT_CAPTURE_SESSION_ID=$sessionId
TT_CAPTURE_RESULTS=$sessionResults
TT_CAPTURE_TRANSCRIPT=$($global:TT_CAPTURE_TRANSCRIPT)
TT_CAPTURE_METADATA=$($global:TT_CAPTURE_METADATA)
START_BOUNDARY=NOT_PROVEN
END_BOUNDARY=prompt sentinel + sidecar metadata
"@
[System.IO.File]::WriteAllText((Join-Path $sessionResults 'session-manifest.txt'), $manifest, $global:TT_CAPTURE_UTF8)

Write-Host "TT_CAPTURE_SESSION_ID=$sessionId"
Write-Host "TT_CAPTURE_RESULTS=$sessionResults"
Write-Host 'Session-scoped prompt sentinel and transcript are active. No profile was modified.'
Write-Host 'Follow manual\README.md exactly. Run Complete-TtVerification when finished.'
