namespace TerminalTranslator.Windows.Capture;

public enum TranscriptStagingState
{
    Absent,
    Recording,
    Stopping,
    Candidate,
    Abandoned,
}

public interface ITranscriptLifecycleAdapter
{
    Task StartAsync(string path, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class TranscriptStagingController(ITranscriptLifecycleAdapter adapter)
{
    public TranscriptStagingState State { get; private set; } = TranscriptStagingState.Absent;

    public string? StagingPath { get; private set; }

    public async Task<bool> TryStartAsync(string protectedStagingPath, CancellationToken cancellationToken)
    {
        if (State is TranscriptStagingState.Recording or TranscriptStagingState.Stopping)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(protectedStagingPath))
        {
            State = TranscriptStagingState.Abandoned;
            return false;
        }

        try
        {
            await adapter.StartAsync(protectedStagingPath, cancellationToken).ConfigureAwait(false);
            StagingPath = protectedStagingPath;
            State = TranscriptStagingState.Recording;
            return true;
        }
        catch
        {
            StagingPath = null;
            State = TranscriptStagingState.Abandoned;
            return false;
        }
    }

    public async Task<bool> TryStopAsync(CancellationToken cancellationToken)
    {
        if (State != TranscriptStagingState.Recording)
        {
            return false;
        }

        State = TranscriptStagingState.Stopping;
        try
        {
            await adapter.StopAsync(cancellationToken).ConfigureAwait(false);
            State = TranscriptStagingState.Candidate;
            return true;
        }
        catch
        {
            State = TranscriptStagingState.Abandoned;
            return false;
        }
    }

    public void ResetAfterFinalization()
    {
        if (State is TranscriptStagingState.Candidate or TranscriptStagingState.Abandoned)
        {
            StagingPath = null;
            State = TranscriptStagingState.Absent;
        }
    }
}
