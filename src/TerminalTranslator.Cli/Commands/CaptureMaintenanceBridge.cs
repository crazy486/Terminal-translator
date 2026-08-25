using System.Globalization;
using System.Security.Principal;
using System.Text;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Cli.Commands;

internal sealed class CaptureMaintenanceBridge(
    CapturePreferenceStore? preferenceStore = null,
    string? captureRoot = null,
    TextWriter? output = null,
    TextWriter? error = null,
    Func<string, string?>? environmentReader = null)
{
    private readonly CapturePreferenceStore _preferenceStore = preferenceStore ?? new CapturePreferenceStore();
    private readonly string _captureRoot = captureRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TerminalTranslator",
        "Capture");
    private readonly TextWriter _output = output ?? Console.Out;
    private readonly TextWriter _error = error ?? Console.Error;
    private readonly Func<string, string?> _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;

    public async Task<int> HandleAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count == 0)
        {
            return 2;
        }

        try
        {
            return arguments[0] switch
            {
                "initialize" => await InitializeAsync(Parse(arguments), cancellationToken).ConfigureAwait(false),
                "boundary" => await BoundaryAsync(Parse(arguments), cancellationToken).ConfigureAwait(false),
                "recover" => await RecoverAsync(Parse(arguments), cancellationToken).ConfigureAwait(false),
                "cleanup" => await CleanupAsync(cancellationToken).ConfigureAwait(false),
                "watch" => await WatchAsync(cancellationToken).ConfigureAwait(false),
                _ => 2,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or FormatException or OverflowException)
        {
            await _error.WriteLineAsync("Capture maintenance failed (local lifecycle state unavailable).").ConfigureAwait(false);
            return 6;
        }
    }

    private async Task<int> InitializeAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        CaptureOwnerIdentity suppliedOwner = ParseOwner(values);
        if (!OperatingSystem.IsWindows() ||
            !CaptureSessionIdentity.TryGetDirectParentOwner(out CaptureOwnerIdentity? observedOwner) ||
            observedOwner != suppliedOwner)
        {
            return 5;
        }

        CapturePreference preference = await _preferenceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _ = await new StaleCaptureCleaner(_captureRoot).CleanupAsync(suppliedOwner.UserSid, cancellationToken).ConfigureAwait(false);
        CaptureBootstrapResult result = await new CaptureSessionBootstrap(_captureRoot).InitializeAsync(
            preference,
            suppliedOwner,
            Required(values, "version"),
            !string.IsNullOrWhiteSpace(_environmentReader("TT_HOSTED_SESSION_ID")),
            cancellationToken).ConfigureAwait(false);

        if (result.Status == CaptureBootstrapStatus.Disabled)
        {
            await _output.WriteLineAsync("disabled=1").ConfigureAwait(false);
            return 0;
        }

        if (result.Status == CaptureBootstrapStatus.HostedFeature001Excluded)
        {
            await _output.WriteLineAsync("hosted-excluded=1").ConfigureAwait(false);
            return 0;
        }

        if (result.Status != CaptureBootstrapStatus.Active)
        {
            await _output.WriteLineAsync("unavailable=storage").ConfigureAwait(false);
            return 6;
        }

        await _output.WriteLineAsync($"session={result.Proof!.SessionId:N}").ConfigureAwait(false);
        await _output.WriteLineAsync($"nonce={result.Proof.Nonce}").ConfigureAwait(false);
        await _output.WriteLineAsync($"staging={result.StagingPath}").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> BoundaryAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() ||
            !TryReadCaptureEnvironment(out Guid sessionId, out string? nonce) ||
            !CaptureSessionIdentity.TryGetDirectParentOwner(out CaptureOwnerIdentity? owner))
        {
            return 5;
        }

        CapturePreference preference = await _preferenceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        string? command = values.TryGetValue("command-base64", out string? encoded)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(encoded))
            : null;
        CaptureBoundaryRequest request = new(
            sessionId,
            nonce!,
            owner!,
            Required(values, "version"),
            Required(values, "staging"),
            values.TryGetValue("history-id", out string? history) ? long.Parse(history, CultureInfo.InvariantCulture) : 0,
            command,
            bool.Parse(Required(values, "succeeded")),
            values.TryGetValue("native-exit-code", out string? exit) ? int.Parse(exit, CultureInfo.InvariantCulture) : null);
        CaptureBoundaryProcessingResult result = await new CaptureBoundaryProcessor(_captureRoot).ProcessAsync(
            request,
            preference,
            cancellationToken).ConfigureAwait(false);
        return await WriteBoundaryResultAsync(result).ConfigureAwait(false);
    }

    private async Task<int> RecoverAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() ||
            !TryReadCaptureEnvironment(out Guid sessionId, out string? nonce) ||
            !CaptureSessionIdentity.TryGetDirectParentOwner(out CaptureOwnerIdentity? owner))
        {
            return 5;
        }

        if (await _preferenceStore.LoadAsync(cancellationToken).ConfigureAwait(false) != CapturePreference.Enabled)
        {
            await _output.WriteLineAsync("disabled=1").ConfigureAwait(false);
            return 0;
        }

        CaptureBoundaryProcessingResult result = await new CaptureBoundaryProcessor(_captureRoot).RecoverAsync(
            sessionId,
            nonce!,
            owner!,
            Required(values, "version"),
            cancellationToken).ConfigureAwait(false);
        return await WriteBoundaryResultAsync(result).ConfigureAwait(false);
    }

    private async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        if (!TryReadCaptureEnvironment(out Guid sessionId, out string? nonce))
        {
            return 0;
        }

        bool cleaned = await new CaptureBoundaryProcessor(_captureRoot).CleanupAuthorizedAsync(
            sessionId,
            nonce!,
            cancellationToken).ConfigureAwait(false);
        return cleaned ? 0 : 6;
    }

    private async Task<int> WatchAsync(CancellationToken cancellationToken)
    {
        if (!TryReadCaptureEnvironment(out Guid sessionId, out string? nonce))
        {
            return 5;
        }

        string sessionDirectory = Path.Combine(_captureRoot, sessionId.ToString("N"));
        CaptureOwnerManifest manifest = await CaptureSessionBootstrap.ReadOwnerManifestAsync(sessionDirectory, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(manifest.Proof.Nonce, nonce, StringComparison.Ordinal))
        {
            return 5;
        }

        CaptureCleanupWatcherOutcome outcome = await new CaptureCleanupWatcher(_captureRoot).RunAsync(
            sessionDirectory,
            manifest.Proof,
            TimeSpan.FromDays(7),
            TimeSpan.FromSeconds(1),
            cancellationToken).ConfigureAwait(false);
        return outcome is CaptureCleanupWatcherOutcome.Cleaned or CaptureCleanupWatcherOutcome.OwnerStillAlive
            ? 0
            : 6;
    }

    private async Task<int> WriteBoundaryResultAsync(CaptureBoundaryProcessingResult result)
    {
        if (result.Status == CaptureBoundaryStatus.Disabled)
        {
            await _output.WriteLineAsync("disabled=1").ConfigureAwait(false);
            return 0;
        }

        if (result.Status != CaptureBoundaryStatus.ReadyForNextInterval || result.NextStagingPath is null)
        {
            await _output.WriteLineAsync($"unavailable={result.FailureReason.ToString().ToLowerInvariant()}").ConfigureAwait(false);
            return 6;
        }

        await _output.WriteLineAsync($"staging={result.NextStagingPath}").ConfigureAwait(false);
        return 0;
    }

    private bool TryReadCaptureEnvironment(out Guid sessionId, out string? nonce)
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        string? session = _environmentReader(CaptureSessionIdentity.SessionEnvironmentVariable);
        nonce = _environmentReader(CaptureSessionIdentity.NonceEnvironmentVariable);
        if (session is not null) environment[CaptureSessionIdentity.SessionEnvironmentVariable] = session;
        if (nonce is not null) environment[CaptureSessionIdentity.NonceEnvironmentVariable] = nonce;
        return CaptureSessionIdentity.TryReadEnvironment(environment, out sessionId, out nonce);
    }

    private static CaptureOwnerIdentity ParseOwner(IReadOnlyDictionary<string, string> values) => new(
        Required(values, "owner-sid"),
        int.Parse(Required(values, "owner-pid"), CultureInfo.InvariantCulture),
        long.Parse(Required(values, "owner-start"), CultureInfo.InvariantCulture));

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing capture maintenance field: {key}.");

    private static IReadOnlyDictionary<string, string> Parse(IReadOnlyList<string> arguments)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string argument in arguments.Skip(1))
        {
            int separator = argument.IndexOf('=');
            if (separator <= 0 || separator == argument.Length - 1)
            {
                throw new ArgumentException("Capture maintenance argument is invalid.");
            }

            values[argument[..separator]] = argument[(separator + 1)..];
        }
        return values;
    }
}
