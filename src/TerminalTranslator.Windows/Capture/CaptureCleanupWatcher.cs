using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;

namespace TerminalTranslator.Windows.Capture;

public enum CaptureOwnerStatus
{
    ExactOwnerAlive,
    Dead,
    ReusedOrDifferentOwner,
    Uncertain,
}

public interface ICaptureOwnerProcessProbe
{
    CaptureOwnerStatus Observe(CaptureOwnerIdentity owner);
}

public enum CaptureCleanupWatcherOutcome
{
    Cleaned,
    OwnerStillAlive,
    OwnerUncertain,
    AlreadyRunning,
    InvalidProof,
    Failed,
}

public sealed class CaptureCleanupWatcher
{
    private static readonly ConcurrentDictionary<string, byte> ActiveSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICaptureOwnerProcessProbe _processProbe;
    private readonly Action<string> _deleteDirectory;

    public CaptureCleanupWatcher(
        string captureRoot,
        ICaptureOwnerProcessProbe? processProbe = null,
        Action<string>? deleteDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureRoot);
        CaptureRoot = Path.GetFullPath(captureRoot);
        _processProbe = processProbe ?? new SystemCaptureOwnerProcessProbe();
        _deleteDirectory = deleteDirectory ?? (path => Directory.Delete(path, recursive: true));
    }

    public string CaptureRoot { get; }

    public async Task<CaptureCleanupWatcherOutcome> RunAsync(
        string sessionDirectory,
        CaptureSessionProof expectedProof,
        TimeSpan maximumWait,
        TimeSpan pollingInterval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedProof);
        if (maximumWait < TimeSpan.Zero || pollingInterval < TimeSpan.Zero ||
            !TryValidateSessionPath(sessionDirectory, expectedProof.SessionId, out string? fullSessionPath))
        {
            return CaptureCleanupWatcherOutcome.InvalidProof;
        }

        CaptureOwnerManifest manifest;
        try
        {
            manifest = await CaptureSessionBootstrap.ReadOwnerManifestAsync(fullSessionPath!, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return CaptureCleanupWatcherOutcome.InvalidProof;
        }

        if (manifest.Proof != expectedProof)
        {
            return CaptureCleanupWatcherOutcome.InvalidProof;
        }

        if (!ActiveSessions.TryAdd(fullSessionPath!, 0))
        {
            return CaptureCleanupWatcherOutcome.AlreadyRunning;
        }

        try
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (true)
            {
                if (!Directory.Exists(fullSessionPath))
                {
                    return CaptureCleanupWatcherOutcome.Cleaned;
                }

                CaptureOwnerStatus status;
                try
                {
                    status = _processProbe.Observe(expectedProof.Owner);
                }
                catch
                {
                    return CaptureCleanupWatcherOutcome.OwnerUncertain;
                }

                if (status == CaptureOwnerStatus.Uncertain)
                {
                    return CaptureCleanupWatcherOutcome.OwnerUncertain;
                }

                if (status is CaptureOwnerStatus.Dead or CaptureOwnerStatus.ReusedOrDifferentOwner)
                {
                    try
                    {
                        _deleteDirectory(fullSessionPath!);
                        return Directory.Exists(fullSessionPath)
                            ? CaptureCleanupWatcherOutcome.Failed
                            : CaptureCleanupWatcherOutcome.Cleaned;
                    }
                    catch
                    {
                        return CaptureCleanupWatcherOutcome.Failed;
                    }
                }

                if (elapsed.Elapsed >= maximumWait)
                {
                    return CaptureCleanupWatcherOutcome.OwnerStillAlive;
                }

                TimeSpan remaining = maximumWait - elapsed.Elapsed;
                TimeSpan delay = pollingInterval <= remaining ? pollingInterval : remaining;
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return CaptureCleanupWatcherOutcome.OwnerStillAlive;
                    }
                }
            }
        }
        finally
        {
            ActiveSessions.TryRemove(fullSessionPath!, out _);
        }
    }

    private bool TryValidateSessionPath(string sessionDirectory, Guid sessionId, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(sessionDirectory) || sessionId == Guid.Empty)
        {
            return false;
        }

        string candidate = Path.GetFullPath(sessionDirectory);
        string rootPrefix = CaptureRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(candidate), CaptureRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(candidate), sessionId.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private sealed class SystemCaptureOwnerProcessProbe : ICaptureOwnerProcessProbe
    {
        public CaptureOwnerStatus Observe(CaptureOwnerIdentity owner)
        {
            try
            {
                using Process process = Process.GetProcessById(owner.ProcessId);
                long start = process.StartTime.ToUniversalTime().Ticks;
                string currentSid = OperatingSystem.IsWindows()
                    ? WindowsIdentity.GetCurrent().User?.Value ?? string.Empty
                    : owner.UserSid;
                return start == owner.ProcessStartUtcTicks &&
                    string.Equals(currentSid, owner.UserSid, StringComparison.Ordinal)
                    ? CaptureOwnerStatus.ExactOwnerAlive
                    : CaptureOwnerStatus.ReusedOrDifferentOwner;
            }
            catch (ArgumentException)
            {
                return CaptureOwnerStatus.Dead;
            }
            catch (InvalidOperationException)
            {
                return CaptureOwnerStatus.Dead;
            }
            catch
            {
                return CaptureOwnerStatus.Uncertain;
            }
        }
    }
}
