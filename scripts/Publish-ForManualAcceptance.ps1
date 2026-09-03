[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-z0-9][a-z0-9.-]+$')]
    [string]$ArtifactName,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

try {
    $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $solution = Join-Path $repositoryRoot 'TerminalTranslator.sln'
    $cliTests = Join-Path $repositoryRoot 'tests\TerminalTranslator.Cli.Tests\TerminalTranslator.Cli.Tests.csproj'
    $coreTests = Join-Path $repositoryRoot 'tests\TerminalTranslator.Core.Tests\TerminalTranslator.Core.Tests.csproj'
    $windowsTests = Join-Path $repositoryRoot 'tests\TerminalTranslator.Windows.Tests\TerminalTranslator.Windows.Tests.csproj'
    $publishScript = Join-Path $PSScriptRoot 'publish.ps1'
    $publishDirectory = Join-Path $repositoryRoot "artifacts\publish\$ArtifactName"
    $executable = Join-Path $publishDirectory 'tt.exe'
    $loader = Join-Path $env:LOCALAPPDATA 'TerminalTranslator\PowerShell\TerminalTranslator.Profile.ps1'
    $installedVersions = Join-Path $env:LOCALAPPDATA 'TerminalTranslator\Versions'
    $capturePreference = Join-Path $env:LOCALAPPDATA 'TerminalTranslator\capture-preference.json'

    if (Test-Path -LiteralPath $publishDirectory) {
        throw "Refusing to overwrite existing manual-acceptance artifact '$publishDirectory'. Choose a new ArtifactName."
    }

    Push-Location $repositoryRoot
    try {
        Write-Host '[manual-publish] Building Release solution...'
        Invoke-DotNet build $solution --configuration Release --no-restore

        if (-not $SkipTests) {
            Write-Host '[manual-publish] Running CLI tests...'
            Invoke-DotNet test $cliTests --configuration Release --no-build --no-restore

            Write-Host '[manual-publish] Running Core tests...'
            Invoke-DotNet test $coreTests --configuration Release --no-build --no-restore

            Write-Host '[manual-publish] Running PowerShell command, capture, and exit-attribution regressions...'
            Invoke-DotNet test $windowsTests --configuration Release --no-build --no-restore --filter `
                'FullyQualifiedName~PowerShellPromptWrapperTests|FullyQualifiedName~PowerShellCommandDiscoverabilityTests|FullyQualifiedName~PowerShellLoaderContractTests|FullyQualifiedName~TranscriptEnvelopeExtractionTests|FullyQualifiedName~RetainedCommandRecord|FullyQualifiedName~CaptureBoundary'
        }

        Write-Host "[manual-publish] Publishing unique artifact '$ArtifactName'..."
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $publishScript -OutputDirectory $publishDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "publish.ps1 failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path -LiteralPath $capturePreference -PathType Leaf)) {
        throw "Capture preference is unavailable at '$capturePreference'; loader installation was not attempted."
    }
    $preference = Get-Content -LiteralPath $capturePreference -Raw | ConvertFrom-Json
    if ($preference.state -ne 'enabled') {
        throw "Capture preference is '$($preference.state)', not 'enabled'; refusing to change it for manual acceptance."
    }

    Write-Host '[manual-publish] Installing the published executable into the PowerShell integration...'
    & $executable configure --capture enabled
    if ($LASTEXITCODE -ne 0) {
        throw "Published executable failed to update PowerShell integration (exit $LASTEXITCODE)."
    }

    $loaderMatch = Select-String -LiteralPath $loader -Pattern "^\`$script:TtExecutablePath = '((?:''|[^'])*)'\s*$"
    if ($null -eq $loaderMatch -or $loaderMatch.Matches.Count -ne 1) {
        throw "Installed loader does not contain exactly one TtExecutablePath assignment."
    }
    $loaderExecutable = $loaderMatch.Matches[0].Groups[1].Value.Replace("''", "'")
    $resolvedExecutable = [IO.Path]::GetFullPath($executable)
    $resolvedLoaderExecutable = [IO.Path]::GetFullPath($loaderExecutable)
    $resolvedInstalledVersions = [IO.Path]::GetFullPath($installedVersions).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedLoaderExecutable.StartsWith($resolvedInstalledVersions, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Loader target is not a product-owned installed version. Loader='$resolvedLoaderExecutable'; versions='$resolvedInstalledVersions'."
    }
    if ($resolvedExecutable.Equals($resolvedLoaderExecutable, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Loader still targets the development acceptance artifact instead of the installed product binary."
    }
    if (-not (Test-Path -LiteralPath $resolvedLoaderExecutable -PathType Leaf)) {
        throw "Installed product executable is unavailable at '$resolvedLoaderExecutable'."
    }

    $publishedHash = Get-FileHash -LiteralPath $resolvedExecutable -Algorithm SHA256
    $installedHash = Get-FileHash -LiteralPath $resolvedLoaderExecutable -Algorithm SHA256
    if (-not $publishedHash.Hash.Equals($installedHash.Hash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installed product hash does not match the published binary. Published='$($publishedHash.Hash)'; installed='$($installedHash.Hash)'."
    }

    $repositoryLoader = Join-Path $repositoryRoot 'src\TerminalTranslator.Windows\PowerShell\TerminalTranslator.Profile.ps1'
    $repositoryLoaderText = Get-Content -LiteralPath $repositoryLoader -Raw
    $installedLoaderText = Get-Content -LiteralPath $loader -Raw
    $expectedLoaderText = $repositoryLoaderText.Replace(
        '__TT_EXECUTABLE_PATH__',
        $resolvedLoaderExecutable.Replace("'", "''"))
    if (-not $expectedLoaderText.Equals($installedLoaderText, [StringComparison]::Ordinal)) {
        throw "Installed PowerShell loader differs from the repository producer-drain contract."
    }
    foreach ($requiredLoaderTerm in @(
        'TranscribePipelineComplete',
        'FlushContentToDisk',
        'OutputToLog',
        'OutputBeingLogged',
        'AlwaysCaptureApplicationIO',
        'Register-TtNativeCaptureCommandHook',
        'TtNativeCaptureHistoryHandler',
        'AddToHistoryHandler',
        'NativeCaptureSelector',
        'ApplyLastAccepted',
        'Complete-TtTranscriptProducer',
        'producer-pass-one',
        'producer-pass-two',
        'native-file-redirection=',
        'FileRedirectionAst',
        'transcript-drained=')) {
        if ($installedLoaderText.IndexOf($requiredLoaderTerm, [StringComparison]::Ordinal) -lt 0) {
            throw "Installed PowerShell loader is missing required producer-drain term '$requiredLoaderTerm'."
        }
    }

    Write-Host '[manual-publish] Verifying fresh Windows PowerShell command resolution...'
    $probeScript = @'
$command = Get-Command tt -ErrorAction Stop
'COMMAND_TYPE=' + $command.CommandType
'COMMAND_MANAGED=' + ($global:TtManagedCommandOwner -eq 'TerminalTranslator.ManagedCommand.v1')
'COMMAND_DEFINITION=' + $command.Definition
'COMMAND_TARGET=' + $script:TtExecutablePath
tt status 2>&1
'STATUS_EXIT=' + $LASTEXITCODE
'@
    $probeErrorActionPreference = $ErrorActionPreference
    try {
        # The probe intentionally exercises a nonzero native status. PowerShell 5.1 represents a
        # redirected native stderr stream as non-terminating NativeCommandError records.
        $ErrorActionPreference = 'Continue'
        $probeOutput = @(& powershell.exe -NoLogo -NonInteractive -ExecutionPolicy Bypass -Command $probeScript 2>&1)
        $probeExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $probeErrorActionPreference
    }
    if ($probeExit -ne 0) {
        throw "Fresh Windows PowerShell command probe failed with exit code $probeExit."
    }
    if ($probeOutput -notcontains 'COMMAND_TYPE=Alias' -or $probeOutput -notcontains 'COMMAND_MANAGED=True') {
        throw "Fresh Windows PowerShell did not resolve the TT-managed command. Output: $($probeOutput -join ' | ')"
    }
    $targetLine = $probeOutput | Where-Object { $_ -like 'COMMAND_TARGET=*' } | Select-Object -Last 1
    if ($null -eq $targetLine) {
        throw "Fresh Windows PowerShell did not report the command target."
    }
    $resolvedCommandTarget = [IO.Path]::GetFullPath($targetLine.Substring(15))
    if (-not $resolvedCommandTarget.Equals($resolvedLoaderExecutable, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Command target mismatch. Loader='$resolvedLoaderExecutable'; command='$resolvedCommandTarget'."
    }
    $definitionLine = $probeOutput | Where-Object { $_ -like 'COMMAND_DEFINITION=*' } | Select-Object -Last 1
    if ($null -eq $definitionLine -or
        -not ([IO.Path]::GetFullPath($definitionLine.Substring(19))).Equals($resolvedLoaderExecutable, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Resolved alias does not point directly to the loader executable. Output: $($probeOutput -join ' | ')"
    }
    if ($probeOutput -notcontains 'STATUS_EXIT=5') {
        throw "Fresh-shell tt status did not preserve its expected no-session exit code. Output: $($probeOutput -join ' | ')"
    }

    Write-Host '[manual-publish] Running actual installed-loader native capture stress...'
    $priorInstalledAcceptance = $env:TT_RUN_INSTALLED_CAPTURE_ACCEPTANCE
    $priorInstalledStressCount = $env:TT_INSTALLED_CAPTURE_STRESS_COUNT
    try {
        $env:TT_RUN_INSTALLED_CAPTURE_ACCEPTANCE = '1'
        $env:TT_INSTALLED_CAPTURE_STRESS_COUNT = '100'
        Invoke-DotNet test $windowsTests --configuration Release --no-build --no-restore --filter `
            'FullyQualifiedName~InstalledProductNativeCaptureAcceptanceTests'
    }
    finally {
        $env:TT_RUN_INSTALLED_CAPTURE_ACCEPTANCE = $priorInstalledAcceptance
        $env:TT_INSTALLED_CAPTURE_STRESS_COUNT = $priorInstalledStressCount
    }

    $artifact = Get-Item -LiteralPath $resolvedExecutable
    Write-Host ''
    Write-Host '[manual-publish] PASS'
    Write-Host "Executable: $($artifact.FullName)"
    Write-Host "Size: $($artifact.Length) bytes"
    Write-Host "LastWriteTime: $($artifact.LastWriteTime.ToString('o'))"
    Write-Host "SHA256: $($publishedHash.Hash)"
    Write-Host "Loader: $loader"
    Write-Host "Loader executable: $resolvedLoaderExecutable"
    Write-Host "Installed SHA256: $($installedHash.Hash)"
    Write-Host "Resolved command: Alias tt -> $resolvedCommandTarget"
    Write-Host ''
    Write-Host 'Manual acceptance:'
    Write-Host 'Get-Command tt | Format-List Name,CommandType,Definition'
    Write-Host '$script:TtExecutablePath'
    Write-Host 'tt status; $LASTEXITCODE'
    Write-Host 'codex doctor --json  # stdin/stdout/stderr must each be terminal'
    Write-Host 'codex                # TUI must start normally'
    Write-Host 'Write-Output "The package installation completed successfully."'
    Write-Host 'tt last; $LASTEXITCODE'
    Write-Host 'tt last > result.txt; $LASTEXITCODE'
}
catch {
    Write-Error "[manual-publish] FAIL: $($_.Exception.Message)"
    exit 1
}
