[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$verificationRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $verificationRoot '..\..\..')).Path
$project = Join-Path $verificationRoot 'ConsoleBufferProbe\ConsoleBufferProbe.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $verificationRoot ('results\automated-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

Push-Location $repoRoot
try {
    $baseline = [ordered]@{
        Schema = 'tt-capture-verification/automated-baseline/v1'
        CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Branch = (& git branch --show-current)
        Head = (& git rev-parse HEAD)
        Log = @(& git log -5 --oneline)
        Status = @(& git status --short)
        PowerShell = ($PSVersionTable | Out-String)
        HostName = $Host.Name
        WtSession = $env:WT_SESSION
        ExactCommands = @(
            "dotnet build `"$project`" --configuration Release",
            'ConsoleBufferProbe.exe environment --output environment.json',
            'ConsoleBufferProbe.exe abi-test --output v0-abi-controlled.json'
        )
    }
    $wtProcess = Get-Process -Name WindowsTerminal -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $wtProcess) {
        try {
            $wtItem = Get-Item -LiteralPath $wtProcess.Path
            $baseline.WindowsTerminal = [ordered]@{
                ProcessId = $wtProcess.Id
                Path = $wtProcess.Path
                FileVersion = $wtItem.VersionInfo.FileVersion
                ProductVersion = $wtItem.VersionInfo.ProductVersion
            }
        } catch {
            $baseline.WindowsTerminal = [ordered]@{ ProcessId = $wtProcess.Id; Error = $_.Exception.Message }
        }
    }
    $baseline | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'baseline.json') -Encoding UTF8

    & dotnet build $project --configuration Release *>&1 |
        Tee-Object -FilePath (Join-Path $OutputDirectory 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Probe build failed with exit code $LASTEXITCODE." }

    $probe = Join-Path $verificationRoot 'ConsoleBufferProbe\bin\Release\net10.0\TT.CaptureVerification.ConsoleBufferProbe.exe'
    & $probe environment --output (Join-Path $OutputDirectory 'environment.json')
    if ($LASTEXITCODE -ne 0) { throw "Environment probe failed with exit code $LASTEXITCODE." }
    & $probe abi-test --output (Join-Path $OutputDirectory 'v0-abi-controlled.json')
    if ($LASTEXITCODE -ne 0) { throw "ABI probe failed with exit code $LASTEXITCODE." }

    $signatureRows = @()
    $probeFiles = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'research\tt-last-capture\probes') -Filter '*.ps1'
    foreach ($file in $probeFiles) {
        $content = Get-Content -LiteralPath $file.FullName
        for ($index = 0; $index -lt $content.Count; $index++) {
            if ($content[$index] -match 'ReadConsoleOutputCharacterW' -or
                $content[$index] -match 'SetValue\(\[uint32\]\$y, 3\)' -or
                $content[$index] -match '\[uint32\]\$y, \[uint32\]0') {
                $signatureRows += [pscustomobject]@{
                    File = $file.FullName.Substring($repoRoot.Length + 1)
                    Line = $index + 1
                    Text = $content[$index].Trim()
                }
            }
        }
    }
    [ordered]@{
        Schema = 'tt-capture-verification/ds-static-inspection/v1'
        CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Finding = 'Every DS ReadConsoleOutputCharacterW probe declares the fourth parameter as uint32 and passes row as uint32; Win32 requires COORD by value.'
        Rows = $signatureRows
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'v0-ds-static-inspection.json') -Encoding UTF8

    [ordered]@{
        Schema = 'tt-capture-verification/probe-manifest/v1'
        CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        ProbePath = $probe
        ProbeExeSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $probe).Hash
        ProbeDllSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath ([System.IO.Path]::ChangeExtension($probe, '.dll'))).Hash
        ExitCodes = [ordered]@{ Build = 0; Environment = 0; AbiTest = 0 }
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'probe-manifest.json') -Encoding UTF8

    Write-Output "AUTOMATED_RESULTS=$OutputDirectory"
} finally {
    Pop-Location
}
