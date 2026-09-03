using System.Diagnostics;
using System.Security.Cryptography;

namespace TerminalTranslator.Windows.Capture;

public sealed record StableTranscriptSnapshotResult(
    bool IsStable,
    long Length,
    byte[]? Sha256,
    byte[]? Content)
{
    public static StableTranscriptSnapshotResult Unavailable() => new(false, 0, null, null);

    public async Task<bool> MatchesFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsStable || Sha256 is null)
        {
            return false;
        }

        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != Length)
            {
                return false;
            }

            byte[] currentHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(Sha256, currentHash);
        }
        catch
        {
            return false;
        }
    }
}

public static class StableTranscriptSnapshot
{
    private const int SmallContentLimit = 1024 * 1024;

    public static async Task<StableTranscriptSnapshotResult> AcquireAsync(
        string path,
        TimeSpan timeout,
        TimeSpan? retryDelay = null,
        Func<bool>? producerCompleted = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || timeout < TimeSpan.Zero)
        {
            return StableTranscriptSnapshotResult.Unavailable();
        }

        TimeSpan delay = retryDelay ?? TimeSpan.FromMilliseconds(15);
        producerCompleted ??= static () => true;
        Stopwatch stopwatch = Stopwatch.StartNew();
        // An exclusive read proves only that no writer owns the file at this instant. PowerShell
        // can schedule its pending transcript drain asynchronously, leaving a readable gap before
        // the writer opens the file. Treat the first image as an observation and finalize only
        // after a second exclusive observation proves identical length and content.
        StableTranscriptSnapshotResult? previousObservation = null;
        do
        {
            if (!producerCompleted())
            {
                previousObservation = null;
                if (!await DelayForRetryAsync(delay, cancellationToken).ConfigureAwait(false))
                    return StableTranscriptSnapshotResult.Unavailable();
                continue;
            }

            try
            {
                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                long initialLength = stream.Length;
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                MemoryStream? smallContent = initialLength <= SmallContentLimit ? new MemoryStream((int)initialLength) : null;
                try
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        smallContent?.Write(buffer, 0, read);
                    }

                    if (stream.Length != initialLength || stream.Position != initialLength || !producerCompleted())
                    {
                        previousObservation = null;
                        continue;
                    }

                    StableTranscriptSnapshotResult observation = new(
                        true,
                        initialLength,
                        hash.GetHashAndReset(),
                        smallContent?.ToArray());
                    if (previousObservation is not null &&
                        previousObservation.Length == observation.Length &&
                        previousObservation.Sha256 is not null &&
                        observation.Sha256 is not null &&
                        CryptographicOperations.FixedTimeEquals(previousObservation.Sha256, observation.Sha256))
                    {
                        return observation;
                    }

                    previousObservation = observation;
                }
                finally
                {
                    smallContent?.Dispose();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
            {
                previousObservation = null;
                if (stopwatch.Elapsed >= timeout)
                {
                    return StableTranscriptSnapshotResult.Unavailable();
                }
            }

            if (!await DelayForRetryAsync(delay, cancellationToken).ConfigureAwait(false))
                return StableTranscriptSnapshotResult.Unavailable();
        }
        while (stopwatch.Elapsed < timeout);

        return StableTranscriptSnapshotResult.Unavailable();
    }

    private static async Task<bool> DelayForRetryAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
