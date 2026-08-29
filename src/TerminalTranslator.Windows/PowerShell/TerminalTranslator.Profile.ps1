# Terminal Translator managed PowerShell 5.1 capture integration.
$script:TtCaptureIntegrationVersion = '2.0'
$script:TtExecutablePath = '__TT_EXECUTABLE_PATH__'

if ($env:TT_HOSTED_SESSION_ID) {
    return
}

# A shell started from another PowerShell process may inherit the dead parent's opaque capture
# proof. Ordinary integrated shells always bootstrap a fresh identity for their own process.
$env:TT_CAPTURE_SESSION_ID = $null
$env:TT_CAPTURE_SESSION_NONCE = $null

if (-not $script:TtOriginalPrompt) {
    $script:TtOriginalPrompt = (Get-Item Function:\prompt -ErrorAction Stop).ScriptBlock
}

$script:TtCaptureActive = $false
$script:TtCaptureStagingPath = $null
$script:TtCaptureUnavailableNotified = $false
$script:TtCaptureDisabled = $false
$script:TtCaptureWatcherStarted = $false
$script:TtCaptureExitSubscription = $null
$script:TtCaptureFailureReason = $null

function script:ConvertTo-TtCaptureFailureReason([string] $Reason) {
    switch ($Reason) {
        'storagecreation' { 'StorageCreationFailed' }
        'sessionidentity' { 'SessionIdentityFailed' }
        'ownervalidation' { 'OwnerValidationFailed' }
        'transcriptstart' { 'TranscriptStartFailed' }
        'transcriptstop' { 'TranscriptStopFailed' }
        'metadatawrite' { 'MetadataWriteFailed' }
        'metadata' { 'MetadataWriteFailed' }
        'loaderbridge' { 'LoaderBridgeFailed' }
        'boundary' { 'BoundaryValidationFailed' }
        'snapshot' { 'SnapshotFailed' }
        'retention' { 'RetentionFailed' }
        'cleanup' { 'CleanupFailed' }
        'storage' { 'StorageFailed' }
        default { 'UnexpectedBootstrapFailure' }
    }
}

function script:Set-TtCaptureUnavailable([string] $Reason = 'UnexpectedBootstrapFailure') {
    $script:TtCaptureActive = $false
    $script:TtCaptureFailureReason = ConvertTo-TtCaptureFailureReason $Reason
    if (-not $script:TtCaptureUnavailableNotified) {
        [Console]::Error.WriteLine("[tt] Capture unavailable ($($script:TtCaptureFailureReason)). ``tt last`` will not work until capture recovers.")
        $script:TtCaptureUnavailableNotified = $true
    }
}

function script:Invoke-TtCaptureBridge([object[]] $Arguments) {
    try {
        if ([string]::IsNullOrWhiteSpace($script:TtExecutablePath) -or
            -not (Test-Path -LiteralPath $script:TtExecutablePath -PathType Leaf)) {
            Set-TtCaptureUnavailable 'loaderbridge'
            return @()
        }

        $previousNativeExitCode = $global:LASTEXITCODE
        $global:LASTEXITCODE = 0
        $result = @(& $script:TtExecutablePath @Arguments 2>$null)
        $bridgeExitCode = $global:LASTEXITCODE
        $global:LASTEXITCODE = $previousNativeExitCode
        if ($bridgeExitCode -ne 0 -and -not ($result | Where-Object { $_ -like 'unavailable=*' })) {
            Set-TtCaptureUnavailable 'loaderbridge'
        }
        return $result
    }
    catch {
        Set-TtCaptureUnavailable 'loaderbridge'
        return @()
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
            $script:TtCaptureFailureReason = $null
            $null = Start-Transcript -LiteralPath $Path -Force -ErrorAction Stop
            $script:TtCaptureActive = $true
        }
        return $true
    }
    catch {
        Set-TtCaptureUnavailable 'transcriptstart'
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
    # Production integration is installed by tt.exe. Non-executable bridge paths are used only by
    # isolated loader tests and must not be handed to Start-Process via a file association.
    if ([IO.Path]::GetExtension($script:TtExecutablePath) -ine '.exe') {
        return
    }

    if ($null -eq $script:TtCaptureExitSubscription) {
        $script:TtCaptureExitSubscription = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -MessageData $script:TtExecutablePath -Action {
            try {
                $null = Start-Process -FilePath $event.MessageData -ArgumentList @('__capture', 'cleanup') -WindowStyle Hidden
            }
            catch { }
        }
    }

    if (-not $script:TtCaptureWatcherStarted) {
        try {
            $null = Start-Process -FilePath $script:TtExecutablePath -ArgumentList @('__capture', 'watch') -WindowStyle Hidden
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
        $result = @(Invoke-TtCaptureBridge $arguments)
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
            $null = Start-TtCaptureTranscript $staging.Substring(8)
        }
        elseif ($unavailable) {
            Set-TtCaptureUnavailable $unavailable.Substring(12)
        }
        elseif (-not $script:TtCaptureUnavailableNotified) {
            Set-TtCaptureUnavailable 'loaderbridge'
        }
    }
    catch {
        Set-TtCaptureUnavailable 'unexpectedbootstrap'
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

            $ttResult = @(Invoke-TtCaptureBridge $ttArguments)
            $nextStaging = $ttResult | Where-Object { $_ -like 'staging=*' } | Select-Object -Last 1
            $disabled = $ttResult | Where-Object { $_ -eq 'disabled=1' } | Select-Object -Last 1
            $unavailable = $ttResult | Where-Object { $_ -like 'unavailable=*' } | Select-Object -Last 1
            if ($disabled) {
                Disable-TtCaptureForLoadedSession
            }
            elseif ($nextStaging) {
                $null = Start-TtCaptureTranscript $nextStaging.Substring(8)
            }
            else {
                Set-TtCaptureUnavailable $(if ($unavailable) { $unavailable.Substring(12) } else { 'loaderbridge' })
                $ttFailedThisPrompt = $true
            }
        }
        catch {
            Set-TtCaptureUnavailable 'transcriptstop'
            $ttFailedThisPrompt = $true
        }
    }
    elseif (-not $script:TtCaptureDisabled -and $script:TtCaptureUnavailableNotified -and -not $ttFailedThisPrompt) {
        try {
            if ($env:TT_CAPTURE_SESSION_ID -and $env:TT_CAPTURE_SESSION_NONCE) {
                $ttResult = @(Invoke-TtCaptureBridge @('__capture', 'recover', "version=$($script:TtCaptureIntegrationVersion)"))
                $nextStaging = $ttResult | Where-Object { $_ -like 'staging=*' } | Select-Object -Last 1
                $disabled = $ttResult | Where-Object { $_ -eq 'disabled=1' } | Select-Object -Last 1
                $unavailable = $ttResult | Where-Object { $_ -like 'unavailable=*' } | Select-Object -Last 1
                if ($disabled) {
                    Disable-TtCaptureForLoadedSession
                }
                elseif ($nextStaging) {
                    $null = Start-TtCaptureTranscript $nextStaging.Substring(8)
                }
                elseif ($unavailable) {
                    Set-TtCaptureUnavailable $unavailable.Substring(12)
                }
                else {
                    Set-TtCaptureUnavailable 'loaderbridge'
                }
            }
            else {
                Initialize-TtCapture
            }
        }
        catch { Set-TtCaptureUnavailable 'unexpectedbootstrap' }
    }

    if ($null -ne $ttNativeExitCode) {
        $global:LASTEXITCODE = $ttNativeExitCode
    }
    $ttPromptText
}
