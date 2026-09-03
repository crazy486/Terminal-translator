# Terminal Translator managed PowerShell 5.1 capture integration.
$script:TtCaptureIntegrationVersion = '2.0'
$script:TtExecutablePath = '__TT_EXECUTABLE_PATH__'

if ($env:TT_HOSTED_SESSION_ID) {
    return
}

# Public product command. The managed loader and this wrapper intentionally share the same bound
# executable path, so an upgrade cannot leave capture maintenance and interactive commands on
# different binaries. Existing user commands are preserved and reported instead of overwritten.
$script:TtManagedCommandMarker = 'TerminalTranslator.ManagedCommand.v1'
$ttExistingCommand = Get-Command -Name tt -ErrorAction SilentlyContinue | Select-Object -First 1
$ttExistingIsManaged = $null -ne $ttExistingCommand -and
    $ttExistingCommand.CommandType -eq [Management.Automation.CommandTypes]::Alias -and
    $global:TtManagedCommandOwner -eq $script:TtManagedCommandMarker
if ($null -eq $ttExistingCommand -or $ttExistingIsManaged) {
    Set-Alias -Name tt -Value $script:TtExecutablePath -Scope Global -Force
    $global:TtManagedCommandOwner = $script:TtManagedCommandMarker
}
else {
    Write-Warning "A command named 'tt' already exists. Terminal Translator did not replace it; use the installed executable path or resolve the collision before enabling the integration."
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
$script:TtCaptureOpeningHistoryId = 0L
$script:TtCaptureInitialPromptPending = $true
$script:TtCaptureCompletionDiagnostics =
    $env:TT_CAPTURE_COMPLETION_DIAGNOSTICS -eq '1' -and
    -not [string]::IsNullOrWhiteSpace($env:TT_CAPTURE_COMPLETION_DIAGNOSTIC_PATH)
$script:TtCaptureDiagnosticEvents = New-Object 'Collections.Generic.List[object]'
$script:TtNativeCaptureModeProperty = $null
$script:TtNativeCaptureModeOriginal = $null
$script:TtNativeCaptureHistoryHandler = $null
$script:TtPriorAddToHistoryHandler = $null
$script:TtNativeApplicationNames = $null
$script:TtPSReadLineSingleton = $null
$script:TtPSReadLineInitializationField = $null
$script:TtNativeCaptureDiagnosticVersion = 0L

function script:Get-TtCaptureSequence {
    if ($script:TtCaptureStagingPath -match 'staging-(\d+)\.txt$') {
        return [long] $matches[1]
    }
    return 0L
}

function script:Get-TtCaptureStagingLength {
    try {
        if ($script:TtCaptureStagingPath -and (Test-Path -LiteralPath $script:TtCaptureStagingPath -PathType Leaf)) {
            return [long] (Get-Item -LiteralPath $script:TtCaptureStagingPath -ErrorAction Stop).Length
        }
    }
    catch { }
    return $null
}

function script:Add-TtCaptureDiagnosticEvent([string] $Stage, [hashtable] $State = @{}) {
    if (-not $script:TtCaptureCompletionDiagnostics) { return }
    $eventState = [ordered] @{
        timestampUtc = [DateTime]::UtcNow.ToString('o')
        processId = $PID
        sessionId = [string] $env:TT_CAPTURE_SESSION_ID
        sequence = Get-TtCaptureSequence
        stage = $Stage
        structuralState = $State
    }
    $null = $script:TtCaptureDiagnosticEvents.Add([pscustomobject] $eventState)
}

function script:Publish-TtCaptureDiagnosticEvents {
    if (-not $script:TtCaptureCompletionDiagnostics -or $script:TtCaptureDiagnosticEvents.Count -eq 0) { return }
    try {
        $lines = @($script:TtCaptureDiagnosticEvents | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 4 })
        [IO.File]::AppendAllLines(
            $env:TT_CAPTURE_COMPLETION_DIAGNOSTIC_PATH,
            [string[]] $lines)
    }
    catch { }
    finally { $script:TtCaptureDiagnosticEvents.Clear() }
}

function script:Initialize-TtNativeCaptureMode {
    try {
        if ($null -eq $script:TtNativeCaptureModeProperty) {
            $flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
            $type = [Management.Automation.PSObject].Assembly.GetType(
                'System.Management.Automation.ConsoleVisibility',
                $false)
            if ($null -eq $type) { return $false }
            $script:TtNativeCaptureModeProperty = $type.GetProperty(
                'AlwaysCaptureApplicationIO',
                $flags)
            if ($null -eq $script:TtNativeCaptureModeProperty) { return $false }
            $script:TtNativeCaptureModeOriginal =
                [bool] $script:TtNativeCaptureModeProperty.GetValue($null, $null)
        }
        return $true
    }
    catch { return $false }
}

function script:Set-TtNativeCaptureMode([bool] $Enabled) {
    if (-not (Initialize-TtNativeCaptureMode)) { return $false }
    try {
        $script:TtNativeCaptureModeProperty.SetValue($null, $Enabled, $null)
        return $true
    }
    catch { return $false }
}

function script:Enable-TtNativeCaptureMode {
    return Set-TtNativeCaptureMode $true
}

function script:Restore-TtNativeCaptureMode {
    if ($null -eq $script:TtNativeCaptureModeProperty -or $null -eq $script:TtNativeCaptureModeOriginal) {
        return
    }
    try {
        $script:TtNativeCaptureModeProperty.SetValue(
            $null,
            [bool] $script:TtNativeCaptureModeOriginal,
            $null)
    }
    catch { }
}

function script:Initialize-TtNativeCaptureSelectorType {
    $selectorType = 'TerminalTranslator.PowerShellIntegration.NativeCaptureSelector' -as [type]
    if ($null -ne $selectorType) { return $selectorType }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Management.Automation.Language;

namespace TerminalTranslator.PowerShellIntegration
{
    public static class NativeCaptureSelector
    {
        private static readonly HashSet<string> AlwaysTty = new HashSet<string>(
            new[] { "codex", "claude", "opencode", "vi", "vim", "nvim", "nano", "emacs",
                    "less", "more", "ssh", "sftp", "telnet", "ftp", "top", "htop", "watch",
                    "wsl", "bash", "zsh", "fish" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Conditional = new HashSet<string>(
            new[] { "cmd", "powershell", "pwsh", "python", "python3", "py", "node" },
            StringComparer.OrdinalIgnoreCase);

        private static PropertyInfo modeProperty;
        private static bool originalMode;
        private static HashSet<string> nativeNames;
        private static HashSet<string> configuredTty;
        private static Func<string, object> priorHandler;
        private static object psReadLineSingleton;
        private static FieldInfo initializationField;
        private static string lastAcceptedCommandLine;
        private static long classificationVersion;

        public static bool LastCaptureSafeNative { get; private set; }
        public static bool LastTtySensitiveNative { get; private set; }
        public static bool LastJoinedReaderEnabled { get; private set; }
        public static long ClassificationVersion { get { return Interlocked.Read(ref classificationVersion); } }

        public static Func<string, object> Configure(
            PropertyInfo property,
            bool original,
            IEnumerable<string> applications,
            string ttyOverrides,
            Func<string, object> prior,
            object singleton,
            FieldInfo initialized,
            string initialCommandLine)
        {
            modeProperty = property;
            originalMode = original;
            nativeNames = new HashSet<string>(applications ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            configuredTty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!String.IsNullOrWhiteSpace(ttyOverrides))
            {
                foreach (string item in ttyOverrides.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    configuredTty.Add(Leaf(item.Trim()));
            }
            priorHandler = prior;
            psReadLineSingleton = singleton;
            initializationField = initialized;
            lastAcceptedCommandLine = initialCommandLine;
            return HandleAcceptedHistory;
        }

        public static void ApplyLastAccepted()
        {
            if (!String.IsNullOrWhiteSpace(lastAcceptedCommandLine))
                Apply(lastAcceptedCommandLine, false);
        }

        public static void Restore()
        {
            if (modeProperty != null)
                modeProperty.SetValue(null, originalMode, null);
        }

        public static string ReadLastHistoryCommand(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var continued = new System.Text.StringBuilder();
                string last = null;
                foreach (string line in File.ReadLines(path))
                {
                    if (line.EndsWith("`", StringComparison.Ordinal))
                    {
                        continued.Append(line, 0, line.Length - 1).Append('\n');
                    }
                    else if (continued.Length > 0)
                    {
                        continued.Append(line);
                        last = continued.ToString();
                        continued.Clear();
                    }
                    else last = line;
                }
                return last;
            }
            catch { return null; }
        }

        private static object HandleAcceptedHistory(string line)
        {
            object decision = priorHandler == null ? (object)"MemoryAndFile" : priorHandler(line);
            bool initialized = true;
            try
            {
                if (initializationField != null && psReadLineSingleton != null)
                    initialized = (bool)initializationField.GetValue(psReadLineSingleton);
            }
            catch { initialized = true; }
            if (initialized)
            {
                lastAcceptedCommandLine = line;
                Apply(line, true);
            }
            return decision;
        }

        private static void Apply(string line, bool record)
        {
            bool captureSafe = false;
            bool ttySensitive = false;
            try
            {
                Token[] tokens;
                ParseError[] errors;
                ScriptBlockAst ast = Parser.ParseInput(line, out tokens, out errors);
                if (errors.Length == 0)
                {
                    foreach (CommandAst command in ast.FindAll(
                        delegate(Ast node) { return node is CommandAst; }, true).OfType<CommandAst>())
                    {
                        string name = command.GetCommandName();
                        if (String.IsNullOrWhiteSpace(name)) continue;
                        string leaf = Leaf(name);
                        if (IsTtySensitive(command, leaf))
                        {
                            ttySensitive = true;
                            continue;
                        }
                        string extension = Path.GetExtension(name);
                        if ((nativeNames != null && (nativeNames.Contains(name) || nativeNames.Contains(leaf))) ||
                            extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".com", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                            Conditional.Contains(leaf))
                            captureSafe = true;
                    }
                }
            }
            catch
            {
                captureSafe = false;
                ttySensitive = true;
            }
            bool joined = captureSafe && !ttySensitive;
            modeProperty.SetValue(null, joined, null);
            if (record)
            {
                LastCaptureSafeNative = captureSafe;
                LastTtySensitiveNative = ttySensitive;
                LastJoinedReaderEnabled = joined;
                Interlocked.Increment(ref classificationVersion);
            }
        }

        private static bool IsTtySensitive(CommandAst command, string leaf)
        {
            if (AlwaysTty.Contains(leaf) || (configuredTty != null && configuredTty.Contains(leaf))) return true;
            string[] args = command.CommandElements.Skip(1)
                .Select(element => element.Extent.Text.Trim('\'', '"').ToLowerInvariant())
                .ToArray();
            if (leaf == "cmd") return !args.Contains("/c");
            if (leaf == "powershell" || leaf == "pwsh")
                return !args.Any(arg => arg == "-command" || arg == "-c" || arg == "-encodedcommand" ||
                                        arg == "-enc" || arg == "-file" || arg == "-f" || arg == "-noninteractive");
            if (leaf == "python" || leaf == "python3" || leaf == "py")
                return !(args.Contains("-c") || args.Contains("-m"));
            if (leaf == "node")
                return !(args.Contains("-e") || args.Contains("--eval") || args.Contains("-p") || args.Contains("--print"));
            return false;
        }

        private static string Leaf(string commandName)
        {
            if (String.IsNullOrWhiteSpace(commandName)) return String.Empty;
            try
            {
                string leaf = Path.GetFileName(commandName.Trim('\'', '"'));
                return Path.GetFileNameWithoutExtension(leaf).ToLowerInvariant();
            }
            catch { return commandName.ToLowerInvariant(); }
        }
    }
}
'@ -ErrorAction Stop
    return ('TerminalTranslator.PowerShellIntegration.NativeCaptureSelector' -as [type])
}

function script:Register-TtNativeCaptureCommandHook {
    if ($null -ne $script:TtNativeCaptureHistoryHandler) { return $true }
    try {
        if ($null -eq (Get-Module -Name PSReadLine)) {
            Import-Module PSReadLine -ErrorAction Stop
        }
        $script:TtNativeApplicationNames = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($application in @(Get-Command -CommandType Application -ErrorAction Stop)) {
            $null = $script:TtNativeApplicationNames.Add([string] $application.Name)
            try {
                $applicationLeaf = [IO.Path]::GetFileNameWithoutExtension(
                    [IO.Path]::GetFileName(([string] $application.Name)))
                $null = $script:TtNativeApplicationNames.Add($applicationLeaf)
            }
            catch { }
            if (-not [string]::IsNullOrWhiteSpace([string] $application.Source)) {
                $null = $script:TtNativeApplicationNames.Add([string] $application.Source)
            }
        }
        $option = Get-PSReadLineOption -ErrorAction Stop
        if ($null -eq $option -or
            -not ($option.PSObject.Properties.Name -contains 'AddToHistoryHandler')) {
            return $false
        }
        $script:TtPriorAddToHistoryHandler = $option.AddToHistoryHandler
        $selectorType = Initialize-TtNativeCaptureSelectorType
        if ($null -eq $selectorType) { return $false }
        $lastHistoryCommand =
            [TerminalTranslator.PowerShellIntegration.NativeCaptureSelector]::ReadLastHistoryCommand(
                [string] $option.HistorySavePath)
        $psReadLineType = [Microsoft.PowerShell.PSConsoleReadLine]
        $singletonField = $psReadLineType.GetField(
            '_singleton',
            [Reflection.BindingFlags]'Static,NonPublic')
        if ($null -ne $singletonField) {
            $script:TtPSReadLineSingleton = $singletonField.GetValue($null)
            $script:TtPSReadLineInitializationField = $psReadLineType.GetField(
                '_delayedOneTimeInitCompleted',
                [Reflection.BindingFlags]'Instance,NonPublic')
        }
        $script:TtNativeCaptureHistoryHandler =
            [TerminalTranslator.PowerShellIntegration.NativeCaptureSelector]::Configure(
                $script:TtNativeCaptureModeProperty,
                [bool] $script:TtNativeCaptureModeOriginal,
                $script:TtNativeApplicationNames,
                [string] $env:TT_TTY_SENSITIVE_APPLICATIONS,
                $script:TtPriorAddToHistoryHandler,
                $script:TtPSReadLineSingleton,
                $script:TtPSReadLineInitializationField,
                $lastHistoryCommand)
        Set-PSReadLineOption -AddToHistoryHandler $script:TtNativeCaptureHistoryHandler -ErrorAction Stop
        return $true
    }
    catch { return $false }
}

function script:Restore-TtNativeCaptureCommandHook {
    if ($null -eq $script:TtNativeCaptureHistoryHandler) { return }
    try {
        Set-PSReadLineOption `
            -AddToHistoryHandler $script:TtPriorAddToHistoryHandler `
            -ErrorAction SilentlyContinue
    }
    catch { }
    try { [TerminalTranslator.PowerShellIntegration.NativeCaptureSelector]::Restore() }
    catch { }
    $script:TtNativeCaptureHistoryHandler = $null
    $script:TtPriorAddToHistoryHandler = $null
    $script:TtPSReadLineSingleton = $null
    $script:TtPSReadLineInitializationField = $null
}

function script:Publish-TtNativeCaptureClassification {
    try {
        $version = [TerminalTranslator.PowerShellIntegration.NativeCaptureSelector]::ClassificationVersion
        if ($version -le $script:TtNativeCaptureDiagnosticVersion) { return }
        Add-TtCaptureDiagnosticEvent 'native-command-classification' @{
            captureSafeNative = [TerminalTranslator.PowerShellIntegration.NativeCaptureSelector]::LastCaptureSafeNative
            ttySensitiveNative = [TerminalTranslator.PowerShellIntegration.NativeCaptureSelector]::LastTtySensitiveNative
            joinedReaderEnabled = [TerminalTranslator.PowerShellIntegration.NativeCaptureSelector]::LastJoinedReaderEnabled
        }
        $script:TtNativeCaptureDiagnosticVersion = $version
    }
    catch { }
}

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
        'boundary-sessionassociationmismatch' { 'BoundaryValidationFailed/SessionAssociationMismatch' }
        'boundary-intervalstatemismatch' { 'BoundaryValidationFailed/IntervalStateMismatch' }
        'boundary-historyidentitymismatch' { 'BoundaryValidationFailed/HistoryIdentityMismatch' }
        'boundary-commandidentitymismatch' { 'BoundaryValidationFailed/CommandIdentityMismatch' }
        'boundary-transcriptenvelopemismatch' { 'BoundaryValidationFailed/TranscriptEnvelopeMismatch' }
        'boundary-boundaryreliabilityrejected' { 'BoundaryValidationFailed/BoundaryReliabilityRejected' }
        'snapshot' { 'SnapshotFailed' }
        'retention' { 'RetentionFailed' }
        'cleanup' { 'CleanupFailed' }
        'storage' { 'StorageFailed' }
        default { 'UnexpectedBootstrapFailure' }
    }
}

function script:Get-TtReliableNativeExitCode([string] $CommandText, [object] $ObservedLastExitCode) {
    if ([string]::IsNullOrWhiteSpace($CommandText) -or $null -eq $ObservedLastExitCode) {
        return $null
    }

    try {
        $tokens = $null
        $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseInput(
            $CommandText,
            [ref] $tokens,
            [ref] $parseErrors)
        if ($parseErrors.Count -ne 0 -or
            $ast.EndBlock.Statements.Count -ne 1 -or
            $ast.EndBlock.Statements[0] -isnot [Management.Automation.Language.PipelineAst]) {
            return $null
        }

        $pipeline = [Management.Automation.Language.PipelineAst] $ast.EndBlock.Statements[0]
        if ($pipeline.PipelineElements.Count -ne 1 -or
            $pipeline.PipelineElements[0] -isnot [Management.Automation.Language.CommandAst]) {
            return $null
        }

        $commandName = ([Management.Automation.Language.CommandAst] $pipeline.PipelineElements[0]).GetCommandName()
        if ([string]::IsNullOrWhiteSpace($commandName)) {
            return $null
        }

        $escapedName = [Management.Automation.WildcardPattern]::Escape($commandName)
        $application = @(Get-Command -Name $escapedName -CommandType Application -ErrorAction SilentlyContinue)
        if ($application.Count -ne 1) {
            return $null
        }

        return [int] $ObservedLastExitCode
    }
    catch {
        return $null
    }
}

function script:Test-TtNativeFileRedirection([string] $CommandText, [object] $NativeExitCode) {
    if ([string]::IsNullOrWhiteSpace($CommandText) -or $null -eq $NativeExitCode) { return $false }
    try {
        $tokens = $null
        $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseInput(
            $CommandText,
            [ref] $tokens,
            [ref] $parseErrors)
        if ($parseErrors.Count -ne 0) { return $false }
        return @($ast.FindAll({
            param($node)
            $node -is [Management.Automation.Language.FileRedirectionAst]
        }, $true)).Count -gt 0
    }
    catch { return $false }
}

function script:Set-TtCaptureUnavailable([string] $Reason = 'UnexpectedBootstrapFailure') {
    Restore-TtNativeCaptureMode
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
        if (-not (Initialize-TtNativeCaptureMode) -or
            -not (Set-TtNativeCaptureMode $false) -or
            -not (Register-TtNativeCaptureCommandHook)) {
            throw 'PowerShell native capture mode is unavailable.'
        }
        $openingHistory = Get-History -Count 1 -ErrorAction SilentlyContinue
        $script:TtCaptureOpeningHistoryId = if ($null -eq $openingHistory) { 0L } else { [long] $openingHistory.Id }
        $null = Start-Transcript -LiteralPath $Path -Force -ErrorAction Stop
        $script:TtCaptureStagingPath = $Path
        $script:TtCaptureActive = $true
        [TerminalTranslator.PowerShellIntegration.NativeCaptureSelector]::ApplyLastAccepted()
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
            $openingHistory = Get-History -Count 1 -ErrorAction SilentlyContinue
            $script:TtCaptureOpeningHistoryId = if ($null -eq $openingHistory) { 0L } else { [long] $openingHistory.Id }
            $script:TtCaptureActive = $true
            [TerminalTranslator.PowerShellIntegration.NativeCaptureSelector]::ApplyLastAccepted()
        }
        return $true
    }
    catch {
        Restore-TtNativeCaptureMode
        Set-TtCaptureUnavailable 'transcriptstart'
        return $false
    }
}

function script:Get-TtActiveTranscriptOptions {
    try {
        $flags = [Reflection.BindingFlags]'Instance,NonPublic'
        $runspace = [Management.Automation.Runspaces.Runspace]::DefaultRunspace
        if ($null -eq $runspace) { return @() }
        $property = $runspace.GetType().GetProperty('TranscriptionData', $flags)
        if ($null -eq $property) { return @() }
        $data = $property.GetValue($runspace, $null)
        if ($null -eq $data) { return @() }
        $allFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
        $transcriptsProperty = $data.GetType().GetProperty('Transcripts', $allFlags)
        if ($null -eq $transcriptsProperty) { return @() }
        $transcripts = $transcriptsProperty.GetValue($data, $null)
        if ($null -eq $transcripts) { return @() }
        return $transcripts.GetType().GetMethod('ToArray').Invoke($transcripts, $null)
    }
    catch { return @() }
}

function script:Wait-TtTranscriptDrain(
    [object[]] $TranscriptOptions,
    [int] $QuietMilliseconds = 0,
    [int] $TimeoutMilliseconds = 750,
    [string] $ObservationName = 'producer-drain') {
    if ($null -eq $TranscriptOptions -or $TranscriptOptions.Count -eq 0) { return $false }
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    $started = [DateTime]::UtcNow
    $quietSince = $null
    $lastState = $null
    Add-TtCaptureDiagnosticEvent "$ObservationName-start" @{
        quietMilliseconds = $QuietMilliseconds
        timeoutMilliseconds = $TimeoutMilliseconds
        stagingLength = Get-TtCaptureStagingLength
    }
    do {
        try {
            $flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
            $pending = 0
            foreach ($option in $TranscriptOptions) {
                $toLogProperty = $option.GetType().GetProperty('OutputToLog', $flags)
                $beingLoggedProperty = $option.GetType().GetProperty('OutputBeingLogged', $flags)
                if ($null -eq $toLogProperty -or $null -eq $beingLoggedProperty) { return $false }
                $toLog = $toLogProperty.GetValue($option, $null)
                $beingLogged = $beingLoggedProperty.GetValue($option, $null)
                if ($null -eq $toLog -or $null -eq $beingLogged) { return $false }
                $toLogCount = $toLog.GetType().GetProperty('Count').GetValue($toLog, $null)
                $beingLoggedCount = $beingLogged.GetType().GetProperty('Count').GetValue($beingLogged, $null)
                if ($toLogCount -ne 0 -or $beingLoggedCount -ne 0) { $pending++ }
            }
            $state = "$pending/$toLogCount/$beingLoggedCount"
            if ($state -ne $lastState) {
                Add-TtCaptureDiagnosticEvent "$ObservationName-transition" @{
                    pendingOptions = $pending
                    outputToLogCount = $toLogCount
                    outputBeingLoggedCount = $beingLoggedCount
                    stagingLength = Get-TtCaptureStagingLength
                }
                $lastState = $state
            }
            if ($pending -eq 0) {
                if ($QuietMilliseconds -le 0) {
                    Add-TtCaptureDiagnosticEvent "$ObservationName-end" @{
                        completed = $true
                        durationMilliseconds = ([DateTime]::UtcNow - $started).TotalMilliseconds
                        stagingLength = Get-TtCaptureStagingLength
                    }
                    return $true
                }
                if ($null -eq $quietSince) { $quietSince = [DateTime]::UtcNow }
                if (([DateTime]::UtcNow - $quietSince).TotalMilliseconds -ge $QuietMilliseconds) {
                    Add-TtCaptureDiagnosticEvent "$ObservationName-end" @{
                        completed = $true
                        durationMilliseconds = ([DateTime]::UtcNow - $started).TotalMilliseconds
                        quietDurationMilliseconds = ([DateTime]::UtcNow - $quietSince).TotalMilliseconds
                        stagingLength = Get-TtCaptureStagingLength
                    }
                    return $true
                }
            }
            else { $quietSince = $null }
        }
        catch { return $false }
        [Threading.Thread]::Sleep(1)
    } while ([DateTime]::UtcNow -lt $deadline)
    Add-TtCaptureDiagnosticEvent "$ObservationName-end" @{
        completed = $false
        durationMilliseconds = ([DateTime]::UtcNow - $started).TotalMilliseconds
        stagingLength = Get-TtCaptureStagingLength
    }
    return $false
}

function script:Flush-TtTranscriptOptionsSynchronously([object[]] $TranscriptOptions) {
    if ($null -eq $TranscriptOptions -or $TranscriptOptions.Count -eq 0) { return $false }
    try {
        Add-TtCaptureDiagnosticEvent 'flush-content-start' @{ stagingLength = Get-TtCaptureStagingLength }
        $flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
        $pipelineComplete = $Host.UI.GetType().GetMethod('TranscribePipelineComplete', $flags)
        if ($null -eq $pipelineComplete) { return $false }
        $null = $pipelineComplete.Invoke($Host.UI, $null)
        foreach ($option in $TranscriptOptions) {
            $flush = $option.GetType().GetMethod('FlushContentToDisk', $flags)
            if ($null -eq $flush) { return $false }
            $null = $flush.Invoke($option, $null)
        }
        Add-TtCaptureDiagnosticEvent 'flush-content-end' @{ stagingLength = Get-TtCaptureStagingLength }
        return $true
    }
    catch {
        Add-TtCaptureDiagnosticEvent 'flush-content-end' @{
            completed = $false
            stagingLength = Get-TtCaptureStagingLength
        }
        return $false
    }
}

function script:Complete-TtTranscriptProducer([object[]] $TranscriptOptions) {
    if ($null -eq $TranscriptOptions -or $TranscriptOptions.Count -eq 0) { return $false }
    $firstFlushed = Flush-TtTranscriptOptionsSynchronously $TranscriptOptions
    $firstDrained = $firstFlushed -and
        (Wait-TtTranscriptDrain $TranscriptOptions -ObservationName 'producer-pass-one')
    $firstLength = Get-TtCaptureStagingLength

    # Windows PowerShell 5.1 joins redirected native stdout/stderr reader threads before the
    # pipeline completes. Two synchronous transcript passes then prove that the completed pipeline
    # has no queued content and that the still-open staging file no longer changes.
    $secondFlushed = $firstDrained -and (Flush-TtTranscriptOptionsSynchronously $TranscriptOptions)
    $secondDrained = $secondFlushed -and
        (Wait-TtTranscriptDrain $TranscriptOptions -ObservationName 'producer-pass-two')
    $secondLength = Get-TtCaptureStagingLength
    $stable = $secondDrained -and $null -ne $firstLength -and $firstLength -eq $secondLength
    Add-TtCaptureDiagnosticEvent 'producer-completion' @{
        firstFlushed = $firstFlushed
        firstDrained = $firstDrained
        firstLength = $firstLength
        secondFlushed = $secondFlushed
        secondDrained = $secondDrained
        secondLength = $secondLength
        stable = $stable
    }
    return $stable
}

function script:Disable-TtCaptureForLoadedSession {
    Restore-TtNativeCaptureCommandHook
    Restore-TtNativeCaptureMode
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
            $script:TtCaptureInitialPromptPending = $true
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
    $ttObservedLastExitCode = $global:LASTEXITCODE
    $ttHistory = Get-History -Count 1 -ErrorAction SilentlyContinue
    $ttPromptText = & $script:TtOriginalPrompt
    Publish-TtNativeCaptureClassification
    Add-TtCaptureDiagnosticEvent 'native-completion-observed' @{
        timestampUtc = [DateTime]::UtcNow.ToString('o')
        nativeExitCode = $ttObservedLastExitCode
        powerShellSucceeded = $ttSucceeded
        stagingLength = Get-TtCaptureStagingLength
    }

    $ttFailedThisPrompt = $false
    if ($script:TtCaptureActive) {
        try {
            $ttCommandText = $null
            $ttNativeExitCode = $null
            if (-not $script:TtCaptureInitialPromptPending -and
                $null -ne $ttHistory -and
                [long] $ttHistory.Id -gt $script:TtCaptureOpeningHistoryId) {
                $ttCommandText = [string] $ttHistory.CommandLine
                $ttNativeExitCode = Get-TtReliableNativeExitCode $ttCommandText $ttObservedLastExitCode
            }
            $ttHasNativeFileRedirection = Test-TtNativeFileRedirection $ttCommandText $ttNativeExitCode
            $ttTranscriptOptions = @(Get-TtActiveTranscriptOptions)
            $ttPreStopFlushed = Complete-TtTranscriptProducer $ttTranscriptOptions
            $ttPreStopDrained = $ttPreStopFlushed
            Add-TtCaptureDiagnosticEvent 'stop-transcript-start' @{
                stagingLength = Get-TtCaptureStagingLength
                preStopFlushed = $ttPreStopFlushed
                preStopDrained = $ttPreStopDrained
            }
            $null = Stop-Transcript -ErrorAction Stop
            $script:TtCaptureActive = $false
            Add-TtCaptureDiagnosticEvent 'stop-transcript-end' @{ stagingLength = Get-TtCaptureStagingLength }
            $ttTranscriptDrained = $ttPreStopDrained -and
                (Wait-TtTranscriptDrain $ttTranscriptOptions -ObservationName 'post-stop-validation')
            if ($script:TtCaptureCompletionDiagnostics) {
                foreach ($ttObservationDelay in @(5, 20, 50)) {
                    [Threading.Thread]::Sleep($ttObservationDelay)
                    Add-TtCaptureDiagnosticEvent "post-stop-observation-$ttObservationDelay" @{
                        stagingLength = Get-TtCaptureStagingLength
                    }
                }
            }

            $ttArguments = @(
                '__capture',
                'boundary',
                "version=$($script:TtCaptureIntegrationVersion)",
                "staging=$($script:TtCaptureStagingPath)",
                "opening-history-id=$($script:TtCaptureOpeningHistoryId)",
                "history-id=$(if ($null -eq $ttHistory) { 0L } else { [long] $ttHistory.Id })",
                "initial-prompt=$($script:TtCaptureInitialPromptPending)",
                "succeeded=$ttSucceeded"
                "transcript-drained=$ttTranscriptDrained"
                "native-file-redirection=$ttHasNativeFileRedirection"
            )
            if (-not $script:TtCaptureInitialPromptPending -and
                $null -ne $ttHistory -and
                [long] $ttHistory.Id -gt $script:TtCaptureOpeningHistoryId) {
                $commandBytes = [Text.Encoding]::UTF8.GetBytes($ttCommandText)
                $ttArguments += "command-base64=$([Convert]::ToBase64String($commandBytes))"
                $ttWasInterrupted = ([string] $ttHistory.ExecutionStatus -eq 'Stopped')
                $ttArguments += "was-interrupted=$ttWasInterrupted"
                if ($null -ne $ttNativeExitCode) {
                    $ttArguments += "native-exit-code=$ttNativeExitCode"
                }
            }

            $ttResult = @(Invoke-TtCaptureBridge $ttArguments)
            Add-TtCaptureDiagnosticEvent 'boundary-returned' @{
                transcriptDrained = $ttTranscriptDrained
                stagingLength = Get-TtCaptureStagingLength
            }
            Publish-TtCaptureDiagnosticEvents
            $nextStaging = $ttResult | Where-Object { $_ -like 'staging=*' } | Select-Object -Last 1
            $disabled = $ttResult | Where-Object { $_ -eq 'disabled=1' } | Select-Object -Last 1
            $unavailable = $ttResult | Where-Object { $_ -like 'unavailable=*' } | Select-Object -Last 1
            $boundaryDetail = $ttResult | Where-Object { $_ -like 'boundary-detail=*' } | Select-Object -Last 1
            if ($disabled) {
                Disable-TtCaptureForLoadedSession
            }
            elseif ($nextStaging) {
                $script:TtCaptureInitialPromptPending = $false
                $null = Start-TtCaptureTranscript $nextStaging.Substring(8)
            }
            else {
                $failure = if ($unavailable) { $unavailable.Substring(12) } else { 'loaderbridge' }
                if ($failure -eq 'boundary' -and $boundaryDetail) {
                    $failure = "boundary-$($boundaryDetail.Substring(16))"
                }
                Set-TtCaptureUnavailable $failure
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
                    $script:TtCaptureInitialPromptPending = $false
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

    if ($null -ne $ttObservedLastExitCode) {
        $global:LASTEXITCODE = $ttObservedLastExitCode
    }
    $ttPromptText
}
