using System.Text;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Windows.Capture;

public enum CaptureBoundaryStatus
{
    ReadyForNextInterval,
    Disabled,
    Unavailable,
    InvalidAssociation,
}

public sealed record CaptureBoundaryRequest(
    Guid SessionId,
    string Nonce,
    CaptureOwnerIdentity DirectOwner,
    string IntegrationVersion,
    string StagingPath,
    long HistoryId,
    string? CommandText,
    bool Succeeded,
    int? NativeExitCode);

public sealed record CaptureBoundaryProcessingResult(
    CaptureBoundaryStatus Status,
    string? NextStagingPath,
    CaptureFailureReason FailureReason,
    CaptureHealthNotification Notification);

public sealed class CaptureBoundaryProcessor
{
    public CaptureBoundaryProcessor(string captureRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureRoot);
        CaptureRoot = Path.GetFullPath(captureRoot);
    }

    public string CaptureRoot { get; }

    public async Task<CaptureBoundaryProcessingResult> ProcessAsync(
        CaptureBoundaryRequest request,
        CapturePreference preference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string sessionDirectory = Path.Combine(CaptureRoot, request.SessionId.ToString("N"));
        CaptureOwnerManifest manifest;
        try
        {
            manifest = await CaptureSessionBootstrap.ReadOwnerManifestAsync(sessionDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return Unavailable(CaptureBoundaryStatus.InvalidAssociation, CaptureFailureReason.Storage);
        }

        if (!CaptureSessionIdentity.Validate(
                manifest.Proof,
                request.SessionId,
                request.Nonce,
                request.DirectOwner,
                request.IntegrationVersion))
        {
            return Unavailable(CaptureBoundaryStatus.InvalidAssociation, CaptureFailureReason.Boundary);
        }

        if (preference != CapturePreference.Enabled)
        {
            TryDeleteAuthorizedSession(sessionDirectory);
            return new CaptureBoundaryProcessingResult(
                CaptureBoundaryStatus.Disabled,
                null,
                CaptureFailureReason.None,
                CaptureHealthNotification.None);
        }

        FinalizationResidueManager residue = new(sessionDirectory);
        if (!await residue.TryRecoverAsync(cancellationToken).ConfigureAwait(false))
        {
            await MarkHealthAsync(sessionDirectory, manifest, CaptureHealthState.Unavailable, cancellationToken).ConfigureAwait(false);
            return Unavailable(CaptureBoundaryStatus.Unavailable, CaptureFailureReason.Cleanup);
        }

        long sequence = manifest.NextSequence;
        string expectedStagingPath = CaptureSessionBootstrap.GetStagingPath(sessionDirectory, sequence);
        if (!string.Equals(Path.GetFullPath(request.StagingPath), Path.GetFullPath(expectedStagingPath), StringComparison.OrdinalIgnoreCase))
        {
            await MarkHealthAsync(sessionDirectory, manifest, CaptureHealthState.Unavailable, cancellationToken).ConfigureAwait(false);
            return Unavailable(CaptureBoundaryStatus.InvalidAssociation, CaptureFailureReason.Boundary);
        }

        if (string.IsNullOrWhiteSpace(request.CommandText) || request.HistoryId <= 0)
        {
            TryDeleteFile(request.StagingPath);
            await PrepareNextIntervalAsync(sessionDirectory, manifest, sequence, cancellationToken).ConfigureAwait(false);
            return Ready(sessionDirectory, sequence);
        }

        StableTranscriptSnapshotResult snapshot = await StableTranscriptSnapshot.AcquireAsync(
            request.StagingPath,
            TimeSpan.FromMilliseconds(500),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!snapshot.IsStable || !await snapshot.MatchesFileAsync(request.StagingPath, cancellationToken).ConfigureAwait(false))
        {
            await MarkHealthAsync(sessionDirectory, manifest, CaptureHealthState.Unavailable, cancellationToken).ConfigureAwait(false);
            return Unavailable(CaptureBoundaryStatus.Unavailable, CaptureFailureReason.Snapshot);
        }

        string transcript;
        try
        {
            transcript = snapshot.Content is not null
                ? DecodeTranscript(snapshot.Content)
                : await File.ReadAllTextAsync(request.StagingPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await MarkHealthAsync(sessionDirectory, manifest, CaptureHealthState.Unavailable, cancellationToken).ConfigureAwait(false);
            return Unavailable(CaptureBoundaryStatus.Unavailable, CaptureFailureReason.Snapshot);
        }

        if (!TryExtractOrderedOutput(transcript, request.CommandText, out string output))
        {
            await MarkHealthAsync(sessionDirectory, manifest, CaptureHealthState.Unavailable, cancellationToken).ConfigureAwait(false);
            return Unavailable(CaptureBoundaryStatus.Unavailable, CaptureFailureReason.Boundary);
        }

        CaptureSessionId session = new(request.SessionId, request.Nonce);
        CommandBoundaryEvidence evidence = new(
            CommandBoundaryValidator.CurrentFormatVersion,
            session,
            session,
            sequence,
            sequence,
            true,
            true,
            request.HistoryId,
            request.CommandText,
            request.CommandText,
            output,
            request.NativeExitCode ?? (request.Succeeded ? 0 : 1),
            WasInterrupted: false,
            TerminationBoundaryReliable: true);
        BoundaryValidationResult validation = CommandBoundaryValidator.Validate(evidence);
        if (!validation.IsReliable)
        {
            await MarkHealthAsync(sessionDirectory, manifest, CaptureHealthState.Unavailable, cancellationToken).ConfigureAwait(false);
            return Unavailable(CaptureBoundaryStatus.Unavailable, CaptureFailureReason.Boundary);
        }

        CapturedCommand command = new(
            session,
            sequence,
            request.CommandText,
            validation.OrderedOutput ?? string.Empty,
            validation.Boundary!,
            LocalCaptureCompleteness.Complete,
            Encoding.UTF8.GetByteCount(validation.OrderedOutput ?? string.Empty),
            ContextualCommandSelection.IsContextual(request.CommandText),
            request.HistoryId);
        byte[] metadata = RetainedCommandRecordCodec.SerializeMetadata(command);
        CapturedCommandCandidate candidate = new(
            Guid.NewGuid().ToString("N"),
            sequence,
            Encoding.UTF8.GetBytes(command.Output),
            metadata,
            true,
            command);
        CapturedCommandFinalizer finalizer = new(new RetainedCaptureStore(sessionDirectory), residue);
        FinalizationResult finalized = await finalizer.FinalizeAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (finalized.Outcome != FinalizationOutcome.Published)
        {
            await MarkHealthAsync(sessionDirectory, manifest, CaptureHealthState.Unavailable, cancellationToken).ConfigureAwait(false);
            return Unavailable(CaptureBoundaryStatus.Unavailable, CaptureFailureReason.Retention);
        }

        try
        {
            File.Delete(request.StagingPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = await residue.TryRegisterAsync([request.StagingPath], cancellationToken).ConfigureAwait(false);
            await MarkHealthAsync(sessionDirectory, manifest, CaptureHealthState.Unavailable, cancellationToken).ConfigureAwait(false);
            return Unavailable(CaptureBoundaryStatus.Unavailable, CaptureFailureReason.Cleanup);
        }

        long nextSequence = checked(sequence + 1);
        await PrepareNextIntervalAsync(sessionDirectory, manifest, nextSequence, cancellationToken).ConfigureAwait(false);
        return Ready(sessionDirectory, nextSequence);
    }

    public async Task<CaptureBoundaryProcessingResult> RecoverAsync(
        Guid sessionId,
        string nonce,
        CaptureOwnerIdentity directOwner,
        string integrationVersion,
        CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(CaptureRoot, sessionId.ToString("N"));
        try
        {
            CaptureOwnerManifest manifest = await CaptureSessionBootstrap.ReadOwnerManifestAsync(directory, cancellationToken).ConfigureAwait(false);
            if (!CaptureSessionIdentity.Validate(manifest.Proof, sessionId, nonce, directOwner, integrationVersion))
            {
                return Unavailable(CaptureBoundaryStatus.InvalidAssociation, CaptureFailureReason.Boundary);
            }

            FinalizationResidueManager residue = new(directory);
            if (!await residue.TryRecoverAsync(cancellationToken).ConfigureAwait(false))
            {
                return Unavailable(CaptureBoundaryStatus.Unavailable, CaptureFailureReason.Cleanup);
            }

            await PrepareNextIntervalAsync(directory, manifest, manifest.NextSequence, cancellationToken).ConfigureAwait(false);
            return Ready(directory, manifest.NextSequence, CaptureHealthNotification.CaptureRestored);
        }
        catch
        {
            return Unavailable(CaptureBoundaryStatus.Unavailable, CaptureFailureReason.Storage);
        }
    }

    public async Task<bool> CleanupAuthorizedAsync(
        Guid sessionId,
        string nonce,
        CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(CaptureRoot, sessionId.ToString("N"));
        try
        {
            CaptureOwnerManifest manifest = await CaptureSessionBootstrap.ReadOwnerManifestAsync(directory, cancellationToken).ConfigureAwait(false);
            if (manifest.Proof.SessionId != sessionId ||
                !string.Equals(manifest.Proof.Nonce, nonce, StringComparison.Ordinal))
            {
                return false;
            }

            TryDeleteAuthorizedSession(directory);
            return !Directory.Exists(directory);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractOrderedOutput(string transcript, string commandText, out string output)
    {
        output = string.Empty;
        int commandStart = transcript.IndexOf(commandText, StringComparison.Ordinal);
        if (commandStart < 0 || transcript.IndexOf(commandText, commandStart + commandText.Length, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        int outputStart = commandStart + commandText.Length;
        string tail = transcript[outputStart..].TrimStart('\r', '\n');
        int footer = tail.IndexOf("Windows PowerShell transcript end", StringComparison.OrdinalIgnoreCase);
        if (footer >= 0)
        {
            int stars = tail.LastIndexOf("**********************", footer, StringComparison.Ordinal);
            tail = tail[..(stars >= 0 ? stars : footer)];
        }

        output = tail.TrimEnd('\r', '\n');
        return true;
    }

    private static string DecodeTranscript(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static async Task PrepareNextIntervalAsync(
        string sessionDirectory,
        CaptureOwnerManifest current,
        long nextSequence,
        CancellationToken cancellationToken)
    {
        CaptureOwnerManifest next = current with { NextSequence = nextSequence, HealthState = CaptureHealthState.Healthy };
        await CaptureSessionBootstrap.WriteOwnerManifestAsync(sessionDirectory, next, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkHealthAsync(
        string sessionDirectory,
        CaptureOwnerManifest current,
        CaptureHealthState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await CaptureSessionBootstrap.WriteOwnerManifestAsync(
                sessionDirectory,
                current with { HealthState = state },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static CaptureBoundaryProcessingResult Ready(
        string sessionDirectory,
        long sequence,
        CaptureHealthNotification notification = CaptureHealthNotification.None) => new(
        CaptureBoundaryStatus.ReadyForNextInterval,
        CaptureSessionBootstrap.GetStagingPath(sessionDirectory, sequence),
        CaptureFailureReason.None,
        notification);

    private static CaptureBoundaryProcessingResult Unavailable(CaptureBoundaryStatus status, CaptureFailureReason reason) => new(
        status,
        null,
        reason,
        CaptureHealthNotification.CaptureUnavailable);

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteAuthorizedSession(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
