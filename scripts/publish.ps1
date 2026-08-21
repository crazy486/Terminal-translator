[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

try {
    $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $projectPath = Join-Path $repositoryRoot "src\TerminalTranslator.Cli\TerminalTranslator.Cli.csproj"
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts\publish"))
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $artifactRoot "win-x64"
    }

    $resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
    $artifactPrefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedOutput.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish output must remain under '$artifactRoot'."
    }

    if (Test-Path -LiteralPath $resolvedOutput) {
        Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

    Write-Host "[publish] Building self-contained single-file win-x64 artifact..."
    & dotnet publish $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        --output $resolvedOutput
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $executable = Join-Path $resolvedOutput "tt.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Expected publish artifact was not created: $executable"
    }

    Write-Host "[publish] Running safe --help smoke test..."
    $helpOutput = (& $executable --help 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Published executable --help failed with exit code $LASTEXITCODE."
    }

    foreach ($commandName in @("configure", "start", "on", "off", "status")) {
        if ($helpOutput.IndexOf($commandName, [StringComparison]::Ordinal) -lt 0) {
            throw "Published executable help does not list '$commandName'."
        }
    }

    $artifact = Get-Item -LiteralPath $executable
    Write-Host "[publish] PASS"
    Write-Host "[publish] Artifact: $($artifact.FullName)"
    Write-Host "[publish] Size: $($artifact.Length) bytes"
}
catch {
    Write-Error "[publish] FAIL: $($_.Exception.Message)"
    exit 1
}
