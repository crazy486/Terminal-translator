using System.Runtime.Versioning;

namespace TerminalTranslator.Windows.Capture;

public static class ContextualInvocationMarker
{
    private const string Prefix = "contextual-invocation-";

    public static bool IsContextualInvocation(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return (arguments.Count >= 1 && arguments[0].Equals("last", StringComparison.OrdinalIgnoreCase)) ||
            (arguments.Count >= 2 && arguments[0].Equals("ask", StringComparison.OrdinalIgnoreCase) &&
                arguments[1].Equals("last", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<bool> TryMarkCurrentAsync(
        string? captureRoot = null,
        Func<string, string?>? environmentReader = null,
        Func<(bool Success, CaptureOwnerIdentity? Owner)>? ownerReader = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            environmentReader ??= Environment.GetEnvironmentVariable;
            string? sessionText = environmentReader(CaptureSessionIdentity.SessionEnvironmentVariable);
            string? nonce = environmentReader(CaptureSessionIdentity.NonceEnvironmentVariable);
            if (!Guid.TryParse(sessionText, out Guid sessionId) || string.IsNullOrWhiteSpace(nonce)) return false;
            ownerReader ??= ReadDirectParentOwner;
            (bool success, CaptureOwnerIdentity? owner) = ownerReader();
            if (!success || owner is null) return false;

            captureRoot ??= ProtectedCaptureStorage.DefaultRoot;
            string sessionDirectory = Path.Combine(Path.GetFullPath(captureRoot), sessionId.ToString("N"));
            CaptureOwnerManifest manifest = await CaptureSessionBootstrap.ReadOwnerManifestAsync(
                sessionDirectory, cancellationToken).ConfigureAwait(false);
            if (!CaptureSessionIdentity.Validate(
                    manifest.Proof, sessionId, nonce, owner, CaptureSessionBootstrap.CurrentIntegrationVersion))
                return false;

            string marker = GetPath(sessionDirectory, manifest.NextSequence);
            await using FileStream stream = new(marker, FileMode.Create, FileAccess.Write, FileShare.Read, 1,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    public static bool IsMarked(string sessionDirectory, long sequence) =>
        File.Exists(GetPath(sessionDirectory, sequence));

    public static void TryDelete(string sessionDirectory, long sequence)
    {
        try { File.Delete(GetPath(sessionDirectory, sequence)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static string GetPath(string sessionDirectory, long sequence) =>
        Path.Combine(sessionDirectory, $"{Prefix}{sequence:D20}.marker");

    [SupportedOSPlatform("windows")]
    private static (bool Success, CaptureOwnerIdentity? Owner) ReadDirectParentOwner()
    {
        bool success = CaptureSessionIdentity.TryGetDirectParentOwner(out CaptureOwnerIdentity? owner);
        return (success, owner);
    }
}
