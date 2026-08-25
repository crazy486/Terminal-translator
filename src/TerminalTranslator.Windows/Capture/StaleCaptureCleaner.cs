namespace TerminalTranslator.Windows.Capture;

public sealed record StaleCaptureCleanupResult(int Deleted, int Preserved, int Failed);

public sealed class StaleCaptureCleaner
{
    private readonly ICaptureOwnerProcessProbe _processProbe;
    private readonly Action<string> _deleteDirectory;
    private readonly Action<string>? _ownerManifestRead;

    public StaleCaptureCleaner(
        string captureRoot,
        ICaptureOwnerProcessProbe? processProbe = null,
        Action<string>? deleteDirectory = null,
        Action<string>? ownerManifestRead = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureRoot);
        CaptureRoot = Path.GetFullPath(captureRoot);
        _processProbe = processProbe ?? new SystemProcessProbeAdapter();
        _deleteDirectory = deleteDirectory ?? (path => Directory.Delete(path, recursive: true));
        _ownerManifestRead = ownerManifestRead;
    }

    public string CaptureRoot { get; }

    public Task<StaleCaptureCleanupResult> CleanupForQuestionOnlyInitializationAsync(
        string currentUserSid,
        CancellationToken cancellationToken = default) => CleanupAsync(currentUserSid, cancellationToken);

    public async Task<StaleCaptureCleanupResult> CleanupAsync(
        string currentUserSid,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentUserSid);
        if (!Directory.Exists(CaptureRoot))
        {
            return new StaleCaptureCleanupResult(0, 0, 0);
        }

        int deleted = 0;
        int preserved = 0;
        int failed = 0;
        foreach (string sessionDirectory in Directory.EnumerateDirectories(CaptureRoot, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string manifestPath = Path.Combine(sessionDirectory, CaptureSessionBootstrap.OwnerManifestFileName);
            CaptureOwnerManifest manifest;
            try
            {
                _ownerManifestRead?.Invoke(manifestPath);
                manifest = await CaptureSessionBootstrap.ReadOwnerManifestAsync(sessionDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                preserved++;
                continue;
            }

            if (!string.Equals(manifest.Proof.Owner.UserSid, currentUserSid, StringComparison.Ordinal))
            {
                preserved++;
                continue;
            }

            CaptureOwnerStatus status;
            try
            {
                status = _processProbe.Observe(manifest.Proof.Owner);
            }
            catch
            {
                status = CaptureOwnerStatus.Uncertain;
            }

            if (status is CaptureOwnerStatus.ExactOwnerAlive or CaptureOwnerStatus.Uncertain)
            {
                preserved++;
                continue;
            }

            try
            {
                _deleteDirectory(sessionDirectory);
                if (Directory.Exists(sessionDirectory))
                {
                    failed++;
                }
                else
                {
                    deleted++;
                }
            }
            catch
            {
                failed++;
            }
        }

        return new StaleCaptureCleanupResult(deleted, preserved, failed);
    }

    private sealed class SystemProcessProbeAdapter : ICaptureOwnerProcessProbe
    {
        private readonly CaptureCleanupWatcherProbe _inner = new();
        public CaptureOwnerStatus Observe(CaptureOwnerIdentity owner) => _inner.Observe(owner);
    }

    // Kept local so stale cleanup exposes no watcher or content dependency.
    private sealed class CaptureCleanupWatcherProbe : ICaptureOwnerProcessProbe
    {
        public CaptureOwnerStatus Observe(CaptureOwnerIdentity owner)
        {
            try
            {
                using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(owner.ProcessId);
                return process.StartTime.ToUniversalTime().Ticks == owner.ProcessStartUtcTicks
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
