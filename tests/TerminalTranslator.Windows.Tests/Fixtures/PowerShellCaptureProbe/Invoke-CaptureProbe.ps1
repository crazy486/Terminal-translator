[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$originalPrompt = Get-Item Function:\prompt -ErrorAction SilentlyContinue
function global:prompt { 'TT-CUSTOM> ' }

try {
    1..3 | ForEach-Object {
        $path = Join-Path $OutputDirectory ("cycle-{0}.txt" -f $_)
        $null = Start-Transcript -LiteralPath $path -Force
        Write-Output "PS-OUT-$_"
        & $env:ComSpec /d /c "echo NATIVE-OUT-$_ & echo NATIVE-ERR-$_ 1>&2"
        Write-Error "PS-ERR-$_" -ErrorAction Continue
        $null = Stop-Transcript
    }
}
finally {
    if ($null -ne $originalPrompt) {
        Set-Item Function:\prompt $originalPrompt.ScriptBlock
    }
}
