# Terminal Translator managed PowerShell 5.1 capture integration.
$script:TtCaptureIntegrationVersion = '2.0'

if ($env:TT_HOSTED_SESSION_ID) {
    return
}

if (-not $script:TtOriginalPrompt) {
    $script:TtOriginalPrompt = (Get-Item Function:\prompt -ErrorAction Stop).ScriptBlock
}

$script:TtCaptureActive = $false
$script:TtCaptureStagingPath = $null
$script:TtCaptureUnavailableNotified = $false
$script:TtCaptureDisabled = $false
$script:TtCaptureWatcherStarted = $false
$script:TtCaptureExitSubscription = $null

function script:Set-TtCaptureUnavailable {
    $script:TtCaptureActive = $false
    if (-not $script:TtCaptureUnavailableNotified) {
        [Console]::Error.WriteLine('[tt] Capture unavailable. `tt last` will not work until capture recovers.')
        $script:TtCaptureUnavailableNotified = $true
    }
}

function script:Start-TtCaptureTranscript([string] $Path) {
    try {
        $null = Start-Transcript -LiteralPath $Path -Force -ErrorAction Stop
        $script:TtCaptureStagingPath = $Path
        $script:TtCaptureActive = $true
        if ($script:TtCaptureUnavailableNotified) {
            # Validate transcript start, then close the probe interval so the integration-only
            # restoration notice cannot become user command output. Restart overwrites the empty
            # probe transcript before any user command runs.
            $null = Stop-Transcript -ErrorAction Stop
            $script:TtCaptureActive = $false
            [Console]::Error.WriteLine('[tt] Capture restored.')
            $script:TtCaptureUnavailableNotified = $false
            $null = Start-Transcript -LiteralPath $Path -Force -ErrorAction Stop
            $script:TtCaptureActive = $true
        }
        return $true
    }
    catch {
        Set-TtCaptureUnavailable
        return $false
    }
}

function script:Disable-TtCaptureForLoadedSession {
    $script:TtCaptureActive = $false
    $script:TtCaptureStagingPath = $null
    $script:TtCaptureDisabled = $true
    $env:TT_CAPTURE_SESSION_ID = $null
    $env:TT_CAPTURE_SESSION_NONCE = $null
    if ($null -ne $script:TtCaptureExitSubscription) {
        Unregister-Event -SubscriptionId $script:TtCaptureExitSubscription.Id -ErrorAction SilentlyContinue
        $script:TtCaptureExitSubscription = $null
    }
}

function script:Register-TtCaptureCleanup {
    if ($null -eq $script:TtCaptureExitSubscription) {
        $script:TtCaptureExitSubscription = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action {
            try {
                $ttExecutable = (Get-Command tt -ErrorAction Stop).Source
                $null = Start-Process -FilePath $ttExecutable -ArgumentList @('__capture', 'cleanup') -WindowStyle Hidden
            }
            catch { }
        }
    }

    if (-not $script:TtCaptureWatcherStarted) {
        try {
            $ttExecutable = (Get-Command tt -ErrorAction Stop).Source
            $null = Start-Process -FilePath $ttExecutable -ArgumentList @('__capture', 'watch') -WindowStyle Hidden
            $script:TtCaptureWatcherStarted = $true
        }
        catch { }
    }
}

function script:Initialize-TtCapture {
    try {
        $owner = Get-Process -Id $PID -ErrorAction Stop
        $ownerSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $arguments = @(
            '__capture',
            'initialize',
            "owner-pid=$PID",
            "owner-start=$($owner.StartTime.ToUniversalTime().Ticks)",
            "owner-sid=$ownerSid",
            "version=$($script:TtCaptureIntegrationVersion)"
        )
        $result = @(& tt @arguments 2>$null)
        $session = $result | Where-Object { $_ -like 'session=*' } | Select-Object -Last 1
        $nonce = $result | Where-Object { $_ -like 'nonce=*' } | Select-Object -Last 1
        $staging = $result | Where-Object { $_ -like 'staging=*' } | Select-Object -Last 1
        $disabled = $result | Where-Object { $_ -eq 'disabled=1' } | Select-Object -Last 1
        $unavailable = $result | Where-Object { $_ -like 'unavailable=*' } | Select-Object -Last 1
        if ($disabled) {
            Disable-TtCaptureForLoadedSession
            return
        }
        if ($session -and $nonce -and $staging) {
            $env:TT_CAPTURE_SESSION_ID = $session.Substring(8)
            $env:TT_CAPTURE_SESSION_NONCE = $nonce.Substring(6)
            Register-TtCaptureCleanup
            if (-not (Start-TtCaptureTranscript $staging.Substring(8))) {
                Set-TtCaptureUnavailable
            }
        }
        elseif ($unavailable) {
            Set-TtCaptureUnavailable
        }
    }
    catch {
        Set-TtCaptureUnavailable
    }
}

Initialize-TtCapture

function global:prompt {
    # Snapshot command facts before original prompt or TT maintenance can modify automatic variables.
    $ttSucceeded = $?
    $ttNativeExitCode = $global:LASTEXITCODE
    $ttHistory = Get-History -Count 1 -ErrorAction SilentlyContinue
    $ttPromptText = & $script:TtOriginalPrompt

    $ttFailedThisPrompt = $false
    if ($script:TtCaptureActive) {
        try {
            $null = Stop-Transcript -ErrorAction Stop
            $script:TtCaptureActive = $false

            $ttArguments = @(
                '__capture',
                'boundary',
                "version=$($script:TtCaptureIntegrationVersion)",
                "staging=$($script:TtCaptureStagingPath)",
                "succeeded=$ttSucceeded"
            )
            if ($null -ne $ttHistory) {
                $commandBytes = [Text.Encoding]::UTF8.GetBytes([string]$ttHistory.CommandLine)
                $ttArguments += "history-id=$($ttHistory.Id)"
                $ttArguments += "command-base64=$([Convert]::ToBase64String($commandBytes))"
            }
            if ($null -ne $ttNativeExitCode) {
                $ttArguments += "native-exit-code=$ttNativeExitCode"
            }

            $ttResult = @(& tt @ttArguments 2>$null)
            $nextStaging = $ttResult | Where-Object { $_ -like 'staging=*' } | Select-Object -Last 1
            $disabled = $ttResult | Where-Object { $_ -eq 'disabled=1' } | Select-Object -Last 1
            if ($disabled) {
                Disable-TtCaptureForLoadedSession
            }
            elseif ($nextStaging) {
                $null = Start-TtCaptureTranscript $nextStaging.Substring(8)
            }
            else {
                Set-TtCaptureUnavailable
                $ttFailedThisPrompt = $true
            }
        }
        catch {
            Set-TtCaptureUnavailable
            $ttFailedThisPrompt = $true
        }
    }
    elseif (-not $script:TtCaptureDisabled -and $script:TtCaptureUnavailableNotified -and -not $ttFailedThisPrompt) {
        try {
            if ($env:TT_CAPTURE_SESSION_ID -and $env:TT_CAPTURE_SESSION_NONCE) {
                $ttResult = @(& tt __capture recover "version=$($script:TtCaptureIntegrationVersion)" 2>$null)
                $nextStaging = $ttResult | Where-Object { $_ -like 'staging=*' } | Select-Object -Last 1
                $disabled = $ttResult | Where-Object { $_ -eq 'disabled=1' } | Select-Object -Last 1
                if ($disabled) {
                    Disable-TtCaptureForLoadedSession
                }
                elseif ($nextStaging) {
                    $null = Start-TtCaptureTranscript $nextStaging.Substring(8)
                }
            }
            else {
                Initialize-TtCapture
            }
        }
        catch { Set-TtCaptureUnavailable }
    }

    if ($null -ne $ttNativeExitCode) {
        $global:LASTEXITCODE = $ttNativeExitCode
    }
    $ttPromptText
}
