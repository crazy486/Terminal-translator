using System.Text.Json;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Windows.Capture;

// PowerShell-facing bootstrap state remains in the Windows capture namespace so the
// maintenance bridge and lifecycle components share one provider-neutral contract.
public enum CaptureBootstrapStatus
{
    Active,
    Disabled,
    HostedFeature001Excluded,
    Unavailable,
}

public sealed record CaptureOwnerManifest(
    int Version,
    CaptureSessionProof Proof,
    long NextSequence,
    CaptureHealthState HealthState);

public sealed record CaptureBootstrapResult(
    CaptureBootstrapStatus Status,
    CaptureSessionProof? Proof,
    string? SessionDirectory,
    string? StagingPath,
    IReadOnlyDictionary<string, string> Environment,
    CaptureFailureReason FailureReason,
    CaptureHealthNotification Notification);

public sealed class CaptureSessionBootstrap
{
    public const string CurrentIntegrationVersion = "2.0";
    public const string OwnerManifestFileName = "owner-manifest.json";
    public const string OpeningBoundaryFileName = "opening-boundary.json";
    private const int ManifestVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Action<string>? _afterSessionDirectoryCreated;
    private readonly Action<string>? _beforeMetadataWritten;

    public CaptureSessionBootstrap(
        string captureRoot,
        Action<string>? afterSessionDirectoryCreated = null,
        Action<string>? beforeMetadataWritten = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureRoot);
        CaptureRoot = Path.GetFullPath(captureRoot);
        _afterSessionDirectoryCreated = afterSessionDirectoryCreated;
        _beforeMetadataWritten = beforeMetadataWritten;
    }

    public string CaptureRoot { get; }

    public async Task<CaptureBootstrapResult> InitializeAsync(
        CapturePreference preference,
        CaptureOwnerIdentity owner,
        string integrationVersion,
        bool isHostedFeature001Session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (string.IsNullOrWhiteSpace(integrationVersion))
        {
            throw new ArgumentException("Integration version is required.", nameof(integrationVersion));
        }

        if (isHostedFeature001Session)
        {
            return NonActive(CaptureBootstrapStatus.HostedFeature001Excluded);
        }

        if (preference != CapturePreference.Enabled)
        {
            return NonActive(CaptureBootstrapStatus.Disabled);
        }

        CaptureSessionProof? proof = null;
        string? sessionDirectory = null;
        CaptureFailureReason failureReason = CaptureFailureReason.SessionIdentity;
        try
        {
            proof = CaptureSessionIdentity.Create(owner, integrationVersion);
            failureReason = CaptureFailureReason.StorageCreation;
            if (OperatingSystem.IsWindows())
            {
                ProtectedCaptureStorage storage = new(CaptureRoot);
                sessionDirectory = storage.CreateSessionDirectory(proof.SessionId);
            }
            else
            {
                Directory.CreateDirectory(CaptureRoot);
                sessionDirectory = Path.Combine(CaptureRoot, proof.SessionId.ToString("N"));
                Directory.CreateDirectory(sessionDirectory);
            }
            _afterSessionDirectoryCreated?.Invoke(sessionDirectory);

            failureReason = CaptureFailureReason.MetadataWrite;
            _beforeMetadataWritten?.Invoke(sessionDirectory);
            CaptureOwnerManifest manifest = new(
                ManifestVersion,
                proof,
                NextSequence: 1,
                CaptureHealthState.Starting);
            await WriteJsonAtomicAsync(
                Path.Combine(sessionDirectory, OwnerManifestFileName),
                manifest,
                cancellationToken).ConfigureAwait(false);
            await WriteJsonAtomicAsync(
                Path.Combine(sessionDirectory, OpeningBoundaryFileName),
                new OpeningBoundaryManifest(ManifestVersion, proof.SessionId, 1),
                cancellationToken).ConfigureAwait(false);

            return new CaptureBootstrapResult(
                CaptureBootstrapStatus.Active,
                proof,
                sessionDirectory,
                GetStagingPath(sessionDirectory, 1),
                CaptureSessionIdentity.ToEnvironment(proof),
                CaptureFailureReason.None,
                CaptureHealthNotification.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (sessionDirectory is not null)
            {
                TryDeleteDirectory(sessionDirectory);
            }

            CaptureHealth health = new();
            CaptureHealthNotification notification = health.MarkUnavailable(failureReason);
            return new CaptureBootstrapResult(
                CaptureBootstrapStatus.Unavailable,
                null,
                null,
                null,
                new Dictionary<string, string>(),
                failureReason,
                notification);
        }
    }

    public static string GetStagingPath(string sessionDirectory, long sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        return Path.Combine(sessionDirectory, $"staging-{sequence:D20}.txt");
    }

    public static async Task<CaptureOwnerManifest> ReadOwnerManifestAsync(
        string sessionDirectory,
        CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(sessionDirectory, OwnerManifestFileName);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        CaptureOwnerManifest manifest = await JsonSerializer.DeserializeAsync<CaptureOwnerManifest>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Capture owner manifest is empty.");
        if (manifest.Version != ManifestVersion || manifest.Proof is null ||
            manifest.NextSequence <= 0 ||
            !string.Equals(Path.GetFileName(Path.GetFullPath(sessionDirectory)), manifest.Proof.SessionId.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Capture owner manifest identity is invalid.");
        }

        return manifest;
    }

    public static Task WriteOwnerManifestAsync(
        string sessionDirectory,
        CaptureOwnerManifest manifest,
        CancellationToken cancellationToken = default) =>
        WriteJsonAtomicAsync(Path.Combine(sessionDirectory, OwnerManifestFileName), manifest, cancellationToken);

    private static CaptureBootstrapResult NonActive(CaptureBootstrapStatus status) => new(
        status,
        null,
        null,
        null,
        new Dictionary<string, string>(),
        CaptureFailureReason.None,
        CaptureHealthNotification.None);

    private static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Capture metadata requires a parent directory.");
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record OpeningBoundaryManifest(int Version, Guid SessionId, long Sequence);
}
