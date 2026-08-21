[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:NUGET_XMLDOC_MODE = "skip"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solution = Join-Path $repositoryRoot "TerminalTranslator.sln"
$coreTests = Join-Path $repositoryRoot "tests\TerminalTranslator.Core.Tests\TerminalTranslator.Core.Tests.csproj"
$windowsTests = Join-Path $repositoryRoot "tests\TerminalTranslator.Windows.Tests\TerminalTranslator.Windows.Tests.csproj"
$cliTests = Join-Path $repositoryRoot "tests\TerminalTranslator.Cli.Tests\TerminalTranslator.Cli.Tests.csproj"
$publishScript = Join-Path $PSScriptRoot "publish.ps1"
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ("tt-validation-" + [Guid]::NewGuid().ToString("N"))))
$completedSteps = [Collections.Generic.List[string]]::new()
$startedAt = [DateTimeOffset]::UtcNow

function Invoke-ValidationStep {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-Host "[validate] START $Name"
    $stepStarted = [Diagnostics.Stopwatch]::StartNew()
    & $Action
    $stepStarted.Stop()
    $script:completedSteps.Add($Name)
    Write-Host ("[validate] PASS  {0} ({1:n1}s)" -f $Name, $stepStarted.Elapsed.TotalSeconds)
}

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $offlineNuGetConfig = Join-Path $temporaryRoot "NuGet.Config"
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $offlineNuGetConfig -Encoding UTF8

    Push-Location $repositoryRoot
    try {
        Invoke-ValidationStep "Offline restore" {
            Invoke-DotNet restore $solution --configfile $offlineNuGetConfig --disable-parallel
        }
        Invoke-ValidationStep "Release solution build" {
            Invoke-DotNet build $solution -c Release --no-restore
        }
        Invoke-ValidationStep "Core tests" {
            Invoke-DotNet test $coreTests -c Release --no-build --no-restore
        }
        Invoke-ValidationStep "Windows tests" {
            Invoke-DotNet test $windowsTests -c Release --no-build --no-restore
        }
        Invoke-ValidationStep "CLI tests" {
            Invoke-DotNet test $cliTests -c Release --no-build --no-restore
        }
        Invoke-ValidationStep "Deterministic local provider stub" {
            Invoke-DotNet test $cliTests -c Release --no-build --no-restore `
                --filter "FullyQualifiedName~StubTranslationServerTests"
        }
        Invoke-ValidationStep "Translation corpus" {
            Invoke-DotNet test $cliTests -c Release --no-build --no-restore `
                --filter "FullyQualifiedName~UserStory1AcceptanceTests"
        }
        Invoke-ValidationStep "Interactive matrix" {
            Invoke-DotNet test $windowsTests -c Release --no-build --no-restore --filter `
                "FullyQualifiedName~InteractiveConsoleTests|FullyQualifiedName~UserStory2AcceptanceTests"
        }
        Invoke-ValidationStep "Security and lifecycle matrix" {
            Invoke-DotNet test $windowsTests -c Release --no-build --no-restore --filter `
                "FullyQualifiedName~SessionPipeSecurityTests"
            Invoke-DotNet test $coreTests -c Release --no-build --no-restore --filter `
                "FullyQualifiedName~SecretDetectorTests|FullyQualifiedName~SessionLifecycleTests"
            Invoke-DotNet test $cliTests -c Release --no-build --no-restore --filter `
                "FullyQualifiedName~UserStory3AcceptanceTests"
        }
        Invoke-ValidationStep "Failure and overload matrix" {
            Invoke-DotNet test $coreTests -c Release --no-build --no-restore --filter `
                "FullyQualifiedName~TranslationWorkQueueTests|FullyQualifiedName~StatusAggregatorTests"
            Invoke-DotNet test $cliTests -c Release --no-build --no-restore --filter `
                "FullyQualifiedName~ProviderFailureContractTests|FullyQualifiedName~UserStory4AcceptanceTests"
            Invoke-DotNet test $windowsTests -c Release --no-build --no-restore --filter `
                "FullyQualifiedName~FailureIsolationTests"
        }
        Invoke-ValidationStep "All automated user-story acceptance" {
            Invoke-DotNet test $cliTests -c Release --no-build --no-restore --filter `
                "FullyQualifiedName~AcceptanceTests"
            Invoke-DotNet test $windowsTests -c Release --no-build --no-restore --filter `
                "FullyQualifiedName~AcceptanceTests"
        }
        Invoke-ValidationStep "Full solution tests" {
            Invoke-DotNet test $solution -c Release --no-build --no-restore
        }
        Invoke-ValidationStep "Self-contained publish and smoke test" {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $publishScript
            if ($LASTEXITCODE -ne 0) {
                throw "publish.ps1 failed with exit code $LASTEXITCODE."
            }
        }
    }
    finally {
        Pop-Location
    }

    $elapsed = [DateTimeOffset]::UtcNow - $startedAt
    Write-Host ""
    Write-Host "[validate] PASS: $($completedSteps.Count) steps completed in $([Math]::Round($elapsed.TotalSeconds, 1))s."
    foreach ($step in $completedSteps) {
        Write-Host "[validate]   $step"
    }
}
catch {
    Write-Error "[validate] FAIL after $($completedSteps.Count) completed steps: $($_.Exception.Message)"
    exit 1
}
finally {
    $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ((Test-Path -LiteralPath $temporaryRoot) -and
        $temporaryRoot.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
