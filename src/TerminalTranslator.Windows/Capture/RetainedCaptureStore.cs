using System.Security.Cryptography;
using System.Text.Json;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Windows.Capture;

public enum RetainedPublicationPhase
{
    CandidateWritten,
    ManifestTemporaryWritten,
    BeforeManifestSwap,
    AfterManifestSwap,
    SupersededDelete,
}

public sealed record RetainedRecordWrite(string RecordId, long Sequence, byte[] Content, byte[] Metadata);

public sealed record RetainedRecordDescriptor(
    string RecordId,
    long Sequence,
    string ContentFile,
    string MetadataFile,
    long ContentLength,
    long MetadataLength,
    string ContentSha256,
    string MetadataSha256);

public sealed record RetainedGeneration(long Generation, long RetainedBytes, IReadOnlyList<RetainedRecordDescriptor> Records);

public sealed class RetainedPublicationException(
    string message,
    bool commitOccurred,
    IReadOnlyList<string> residuePaths,
    Exception innerException) : IOException(message, innerException)
{
    public bool CommitOccurred { get; } = commitOccurred;

    public IReadOnlyList<string> ResiduePaths { get; } = residuePaths;
}

public sealed class RetainedCaptureStore
{
    public const long MaximumRetainedBytes = CaptureRetentionPolicy.MaximumRetainedBytes;
    private const int ManifestVersion = 1;
    private const string ManifestFileName = "committed-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public RetainedCaptureStore(string sessionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        SessionDirectory = sessionDirectory;
        Directory.CreateDirectory(SessionDirectory);
    }

    public string SessionDirectory { get; }

    public Action<RetainedPublicationPhase>? PublicationObserver { get; set; }

    public Func<string, bool>? ArtifactDelete { get; set; }

    public async Task<RetainedGeneration?> LoadCommittedAsync(CancellationToken cancellationToken = default)
    {
        string manifestPath = Path.Combine(SessionDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        PersistedManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PersistedManifest>(manifestBytes, JsonOptions) ??
                throw new InvalidDataException("Retained manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Retained manifest is corrupt.", exception);
        }

        if (manifest.Version != ManifestVersion || manifest.Generation <= 0 || manifest.Records is null)
        {
            throw new InvalidDataException("Retained manifest version or generation is invalid.");
        }

        long measured = manifestBytes.LongLength;
        foreach (RetainedRecordDescriptor record in manifest.Records)
        {
            ValidateRelativeFileName(record.ContentFile);
            ValidateRelativeFileName(record.MetadataFile);
            string contentPath = Path.Combine(SessionDirectory, record.ContentFile);
            string metadataPath = Path.Combine(SessionDirectory, record.MetadataFile);
            measured = checked(measured + ValidateFile(contentPath, record.ContentLength, record.ContentSha256));
            measured = checked(measured + ValidateFile(metadataPath, record.MetadataLength, record.MetadataSha256));
        }

        if (measured != manifest.RetainedBytes || measured > MaximumRetainedBytes)
        {
            throw new InvalidDataException("Retained byte accounting is inconsistent.");
        }

        return new RetainedGeneration(manifest.Generation, measured, manifest.Records);
    }

    public async Task<byte[]> ReadContentAsync(RetainedRecordDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateRelativeFileName(descriptor.ContentFile);
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(SessionDirectory, descriptor.ContentFile), cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength != descriptor.ContentLength ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), descriptor.ContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Retained content no longer matches its committed descriptor.");
        }

        return bytes;
    }

    public async Task<byte[]> ReadMetadataAsync(RetainedRecordDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateRelativeFileName(descriptor.MetadataFile);
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(SessionDirectory, descriptor.MetadataFile), cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength != descriptor.MetadataLength ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), descriptor.MetadataSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Retained metadata no longer matches its committed descriptor.");
        }

        return bytes;
    }

    public long EstimateRetainedBytes(IReadOnlyList<RetainedRecordWrite> records, long generation)
    {
        IReadOnlyList<RetainedRecordDescriptor> descriptors = BuildDescriptors(records, generation);
        byte[] manifest = SerializeManifest(generation, descriptors);
        return checked(descriptors.Sum(record => record.ContentLength + record.MetadataLength) + manifest.LongLength);
    }

    public async Task<RetainedGeneration> PublishAsync(
        IReadOnlyList<RetainedRecordWrite> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        RetainedGeneration? previous = await LoadCommittedAsync(cancellationToken).ConfigureAwait(false);
        long generation = checked((previous?.Generation ?? 0) + 1);
        List<string> candidatePaths = [];
        List<string> supersededPaths = previous?.Records
            .SelectMany(record => new[] { record.ContentFile, record.MetadataFile })
            .Select(file => Path.Combine(SessionDirectory, file))
            .ToList() ?? [];
        string manifestPath = Path.Combine(SessionDirectory, ManifestFileName);
        string manifestTemporary = Path.Combine(SessionDirectory, $".{ManifestFileName}.{Guid.NewGuid():N}.tmp");
        bool committed = false;
        try
        {
            List<RetainedRecordDescriptor> descriptors = [];
            foreach (RetainedRecordWrite record in records.OrderBy(record => record.Sequence))
            {
                ValidateRecordWrite(record);
                string baseName = $"g{generation:D20}-record-{record.Sequence:D20}-{record.RecordId}";
                string contentFile = baseName + ".content";
                string metadataFile = baseName + ".metadata";
                string contentPath = Path.Combine(SessionDirectory, contentFile);
                string metadataPath = Path.Combine(SessionDirectory, metadataFile);
                await WriteNewAsync(contentPath, record.Content, cancellationToken).ConfigureAwait(false);
                candidatePaths.Add(contentPath);
                await WriteNewAsync(metadataPath, record.Metadata, cancellationToken).ConfigureAwait(false);
                candidatePaths.Add(metadataPath);
                descriptors.Add(new RetainedRecordDescriptor(
                    record.RecordId,
                    record.Sequence,
                    contentFile,
                    metadataFile,
                    record.Content.LongLength,
                    record.Metadata.LongLength,
                    Convert.ToHexString(SHA256.HashData(record.Content)),
                    Convert.ToHexString(SHA256.HashData(record.Metadata))));
            }

            PublicationObserver?.Invoke(RetainedPublicationPhase.CandidateWritten);
            byte[] manifestBytes = SerializeManifest(generation, descriptors);
            long total = descriptors.Sum(record => checked(record.ContentLength + record.MetadataLength)) + manifestBytes.LongLength;
            if (total > MaximumRetainedBytes)
            {
                throw new InvalidDataException("Candidate retained generation exceeds the hard limit.");
            }

            await WriteNewAsync(manifestTemporary, manifestBytes, cancellationToken).ConfigureAwait(false);
            candidatePaths.Add(manifestTemporary);
            PublicationObserver?.Invoke(RetainedPublicationPhase.ManifestTemporaryWritten);
            PublicationObserver?.Invoke(RetainedPublicationPhase.BeforeManifestSwap);
            File.Move(manifestTemporary, manifestPath, overwrite: true);
            candidatePaths.Remove(manifestTemporary);
            committed = true;
            PublicationObserver?.Invoke(RetainedPublicationPhase.AfterManifestSwap);

            foreach (string path in supersededPaths)
            {
                if (File.Exists(path))
                {
                    PublicationObserver?.Invoke(RetainedPublicationPhase.SupersededDelete);
                    DeletePath(path);
                }
            }

            return await LoadCommittedAsync(cancellationToken).ConfigureAwait(false) ??
                throw new InvalidDataException("Committed manifest disappeared after publication.");
        }
        catch (Exception exception) when (exception is not RetainedPublicationException)
        {
            List<string> residue = [];
            IEnumerable<string> cleanupTargets = committed ? supersededPaths : candidatePaths;
            foreach (string path in cleanupTargets.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(path))
                    {
                        DeletePath(path);
                    }
                }
                catch
                {
                    residue.Add(path);
                }
            }

            throw new RetainedPublicationException(
                committed ? "Retained generation committed but cleanup failed." : "Retained generation was not committed.",
                committed,
                residue,
                exception);
        }
    }

    internal static byte[] SerializeManifest(long generation, IReadOnlyList<RetainedRecordDescriptor> records)
    {
        long dataBytes = records.Sum(record => checked(record.ContentLength + record.MetadataLength));
        long retainedBytes = 0;
        byte[] serialized = [];
        for (int attempt = 0; attempt < 8; attempt++)
        {
            serialized = JsonSerializer.SerializeToUtf8Bytes(
                new PersistedManifest(ManifestVersion, generation, retainedBytes, records),
                JsonOptions);
            long measured = checked(dataBytes + serialized.LongLength);
            if (measured == retainedBytes)
            {
                return serialized;
            }

            retainedBytes = measured;
        }

        throw new InvalidDataException("Retained manifest accounting did not converge.");
    }

    private static async Task WriteNewAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void DeletePath(string path)
    {
        if (ArtifactDelete is not null)
        {
            if (!ArtifactDelete(path))
            {
                throw new IOException("Retained artifact deletion was rejected.");
            }

            return;
        }

        File.Delete(path);
    }

    private static IReadOnlyList<RetainedRecordDescriptor> BuildDescriptors(
        IReadOnlyList<RetainedRecordWrite> records,
        long generation) => records
        .OrderBy(record => record.Sequence)
        .Select(record =>
        {
            ValidateRecordWrite(record);
            string baseName = $"g{generation:D20}-record-{record.Sequence:D20}-{record.RecordId}";
            return new RetainedRecordDescriptor(
                record.RecordId,
                record.Sequence,
                baseName + ".content",
                baseName + ".metadata",
                record.Content.LongLength,
                record.Metadata.LongLength,
                Convert.ToHexString(SHA256.HashData(record.Content)),
                Convert.ToHexString(SHA256.HashData(record.Metadata)));
        })
        .ToArray();

    private static void ValidateRecordWrite(RetainedRecordWrite record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Sequence <= 0 || record.Content is null || record.Metadata is null ||
            string.IsNullOrWhiteSpace(record.RecordId) || record.RecordId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Retained record identity or content is invalid.", nameof(record));
        }
    }

    private static void ValidateRelativeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName) ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Retained manifest references an invalid path.");
        }
    }

    private static long ValidateFile(string path, long expectedLength, string expectedHash)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("A committed retained record is unavailable.", exception);
        }

        if (bytes.LongLength != expectedLength ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A committed retained record is corrupt.");
        }

        return bytes.LongLength;
    }

    private sealed record PersistedManifest(
        int Version,
        long Generation,
        long RetainedBytes,
        IReadOnlyList<RetainedRecordDescriptor> Records);
}
