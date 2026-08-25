[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$folder = (Resolve-Path -LiteralPath $ResultsDirectory).Path
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Read-JsonIfPresent {
    param([string]$Name)
    $path = Join-Path $folder $Name
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Get-Content -Raw -Encoding UTF8 -LiteralPath $path | ConvertFrom-Json
}

function Get-ConsoleText {
    param($Capture)
    if ($null -eq $Capture) { return $null }
    return (($Capture.Rows | ForEach-Object { [string]$_ }) -join "`n")
}

function Get-PreviousConsoleText {
    param($Capture)
    if ($null -eq $Capture -or $null -eq $Capture.PreviousCompletedBoundary -or -not $Capture.PreviousCompletedBoundary.Found) { return $null }
    return [string]$Capture.PreviousCompletedBoundary.Text
}

function Get-TranscriptMeasurement {
    param($Capture, [int]$Delay)
    if ($null -eq $Capture) { return $null }
    return @($Capture.Measurements | Where-Object { $_.DelayMs -eq $Delay }) | Select-Object -First 1
}

function Test-Markers {
    param([string]$Text, [string[]]$Markers)
    $result = [ordered]@{}
    foreach ($marker in $Markers) {
        $result[$marker] = ($null -ne $Text -and $Text.Contains($marker))
    }
    return $result
}

function Measure-NumberedMarkers {
    param([string]$Text, [string]$Prefix)
    if ($null -eq $Text) {
        return [ordered]@{ Total = 0; Distinct = 0; Missing = @(1..1000); Duplicates = @(); CorrectOrder = $false }
    }
    $matches = [regex]::Matches($Text, [regex]::Escape($Prefix) + '(?<n>[0-9]{4})')
    $numbers = @($matches | ForEach-Object { [int]$_.Groups['n'].Value })
    $groups = @($numbers | Group-Object)
    $distinct = @($groups | ForEach-Object { [int]$_.Name } | Sort-Object)
    $missing = @(1..1000 | Where-Object { $distinct -notcontains $_ })
    $duplicates = @($groups | Where-Object { $_.Count -gt 1 } | ForEach-Object { [ordered]@{ Number = [int]$_.Name; Count = $_.Count } })
    $correctOrder = $true
    for ($i = 1; $i -lt $numbers.Count; $i++) {
        if ($numbers[$i] -lt $numbers[$i - 1]) { $correctOrder = $false; break }
    }
    return [ordered]@{
        Total = $numbers.Count
        Distinct = $distinct.Count
        First = @($numbers | Select-Object -First 5)
        Last = @($numbers | Select-Object -Last 5)
        Missing = $missing
        Duplicates = $duplicates
        CorrectOrder = $correctOrder
    }
}

function Measure-ReflowMarkers {
    param([string]$Text)
    if ($null -eq $Text) { return [ordered]@{ CompleteLogicalMarkers = 0; MarkerIds = @() } }
    $joined = $Text -replace "`r", '' -replace "`n", ''
    $matches = [regex]::Matches($joined, 'TT_REFLOW_(?<n>[0-9]{4})_X+_END_\k<n>')
    return [ordered]@{
        CompleteLogicalMarkers = $matches.Count
        MarkerIds = @($matches | ForEach-Object { $_.Groups['n'].Value })
    }
}

$coreConsole = Read-JsonIfPresent 'v1-v4-core-console.json'
$coreTranscript = Read-JsonIfPresent 'v1-v4-core-transcript.json'
$unicodeMarker = 'TT_UNICODE_' + [char]0x4E2D + [char]0x6587 + '_' + [char]0x03A9 + '_' + [char]0xD83D + [char]0xDE42
$coreMarkers = @(
    'TT_T_WRITE_OUTPUT', 'TT_T_WRITE_HOST', 'TT_T_NATIVE_STDOUT', 'TT_T_NATIVE_STDERR',
    '__TT_TRANSCRIPT_NONEXISTENT__', 'TT_ORDER_1_STDOUT', 'TT_ORDER_2_STDERR',
    'TT_ORDER_3_STDOUT', $unicodeMarker
)

$analysis = [ordered]@{
    Schema = 'tt-capture-verification/manual-analysis/v1'
    AnalyzedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    ResultsDirectory = $folder
    V1V4Core = [ordered]@{
        Console = Test-Markers -Text (Get-ConsoleText $coreConsole) -Markers $coreMarkers
        Transcript0ms = Test-Markers -Text ([string](Get-TranscriptMeasurement $coreTranscript 0).PreviousSegment.Text) -Markers $coreMarkers
        Transcript50ms = Test-Markers -Text ([string](Get-TranscriptMeasurement $coreTranscript 50).PreviousSegment.Text) -Markers $coreMarkers
        Transcript200ms = Test-Markers -Text ([string](Get-TranscriptMeasurement $coreTranscript 200).PreviousSegment.Text) -Markers $coreMarkers
    }
    V2 = [ordered]@{}
    V3 = [ordered]@{}
    V4 = [ordered]@{}
    V5 = [ordered]@{}
}

foreach ($name in @('v2-default', 'v2-enlarged')) {
    $console = Read-JsonIfPresent "$name-console.json"
    $transcript = Read-JsonIfPresent "$name-transcript.json"
    $analysis.V2[$name] = [ordered]@{
        Console = Measure-NumberedMarkers -Text (Get-ConsoleText $console) -Prefix 'TT_LINE_'
        ConsolePrevious = Measure-NumberedMarkers -Text (Get-PreviousConsoleText $console) -Prefix 'TT_LINE_'
        Transcript0ms = Measure-NumberedMarkers -Text ([string](Get-TranscriptMeasurement $transcript 0).PreviousSegment.Text) -Prefix 'TT_LINE_'
        Transcript50ms = Measure-NumberedMarkers -Text ([string](Get-TranscriptMeasurement $transcript 50).PreviousSegment.Text) -Prefix 'TT_LINE_'
        Transcript200ms = Measure-NumberedMarkers -Text ([string](Get-TranscriptMeasurement $transcript 200).PreviousSegment.Text) -Prefix 'TT_LINE_'
        Buffer = $console.Console.Buffer
        Window = $console.Console.Window
    }
}

$v3Expectations = [ordered]@{
    'v3-simple' = @('CMD2_UNIQUE')
    'v3-empty' = @('cd .')
    'v3-continuation' = @('TT_CONTINUATION_A', 'TT_CONTINUATION_B')
    'v3-custom-prompt' = @('TT_CUSTOM_PROMPT_OUTPUT', 'CUSTOM>')
    'v3-session-b' = @('TT_SESSION_B_ONLY')
}
foreach ($entry in $v3Expectations.GetEnumerator()) {
    $console = Read-JsonIfPresent "$($entry.Key)-console.json"
    $transcript = Read-JsonIfPresent "$($entry.Key)-transcript.json"
    $consolePrevious = Get-PreviousConsoleText $console
    $transcript0 = [string](Get-TranscriptMeasurement $transcript 0).PreviousSegment.Text
    $analysis.V3[$entry.Key] = [ordered]@{
        ConsoleBoundaryFound = ($null -ne $console -and $console.PreviousCompletedBoundary.Found)
        ConsoleExpected = Test-Markers -Text $consolePrevious -Markers $entry.Value
        ConsoleContainsCaptureCommand = ($null -ne $consolePrevious -and $consolePrevious.Contains('Capture-TtBoth'))
        TranscriptBoundaryFoundAt0ms = ($null -ne (Get-TranscriptMeasurement $transcript 0) -and (Get-TranscriptMeasurement $transcript 0).PreviousSegment.Found)
        TranscriptExpectedAt0ms = Test-Markers -Text $transcript0 -Markers $entry.Value
        TranscriptContainsCaptureCommand = ($null -ne $transcript0 -and $transcript0.Contains('Capture-TtBoth'))
    }
}

$afterClearConsole = Read-JsonIfPresent 'v4-after-clear-console.json'
$afterClearTranscript = Read-JsonIfPresent 'v4-after-clear-transcript.json'
$consoleContainsBeforeClear = $false
if ($null -ne $afterClearConsole) {
    $consoleContainsBeforeClear = (Get-ConsoleText $afterClearConsole).Contains('BEFORE_CLEAR')
}
$analysis.V4['ClearHost'] = [ordered]@{
    ConsoleContainsBeforeClear = $consoleContainsBeforeClear
    TranscriptSnapshotContainsBeforeClear = $false
}
if ($null -ne $afterClearTranscript) {
    $snapshotPath = (Get-TranscriptMeasurement $afterClearTranscript 0).RawSnapshot
    if (Test-Path -LiteralPath $snapshotPath) {
        $analysis.V4['ClearHost'].TranscriptSnapshotContainsBeforeClear = (Get-Content -Raw -Encoding UTF8 -LiteralPath $snapshotPath).Contains('BEFORE_CLEAR')
    }
}

foreach ($name in @('v5-wide-baseline', 'v5-narrow', 'v5-wide-again')) {
    $console = Read-JsonIfPresent "$name-console.json"
    $analysis.V5[$name] = [ordered]@{
        Console = $console.Console
        Reflow = Measure-ReflowMarkers -Text (Get-ConsoleText $console)
    }
}

$output = Join-Path $folder 'analysis.json'
[System.IO.File]::WriteAllText($output, ($analysis | ConvertTo-Json -Depth 20), $utf8)
Write-Output "ANALYSIS_RESULT=$output"
