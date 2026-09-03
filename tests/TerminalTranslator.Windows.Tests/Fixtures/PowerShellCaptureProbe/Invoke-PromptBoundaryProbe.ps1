[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$script:ProbeSequence = 1L
$script:ProbeActive = $false
$script:ProbePromptCount = 0
$script:ProbeStaging = Join-Path $OutputDirectory ("staging-{0:D20}.txt" -f $script:ProbeSequence)
$script:ProbeFacts = Join-Path $OutputDirectory 'boundary-facts.tsv'

$null = Start-Transcript -LiteralPath $script:ProbeStaging -Force -ErrorAction Stop
$script:ProbeActive = $true

function global:prompt {
    $history = Get-History -Count 1 -ErrorAction SilentlyContinue
    $promptText = 'CUSTOM> '
    $script:ProbePromptCount++

    if ($script:ProbeActive) {
        $null = Stop-Transcript -ErrorAction Stop
        $script:ProbeActive = $false
        $historyId = if ($null -eq $history) { 0L } else { [long] $history.Id }
        $command = if ($null -eq $history) { '' } else { [string] $history.CommandLine }
        $commandBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($command))
        Add-Content -LiteralPath $script:ProbeFacts -Encoding UTF8 -Value (
            "{0}`t{1}`t{2}`t{3}" -f $script:ProbePromptCount, $script:ProbeSequence, $historyId, $commandBase64)

        if ($historyId -gt 0) {
            $script:ProbeSequence++
        }
        $script:ProbeStaging = Join-Path $OutputDirectory ("staging-{0:D20}.txt" -f $script:ProbeSequence)
        $null = Start-Transcript -LiteralPath $script:ProbeStaging -Force -ErrorAction Stop
        $script:ProbeActive = $true
    }

    $promptText
}

Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action {
    if ($script:ProbeActive) {
        try { $null = Stop-Transcript -ErrorAction SilentlyContinue } catch { }
    }
} | Out-Null
