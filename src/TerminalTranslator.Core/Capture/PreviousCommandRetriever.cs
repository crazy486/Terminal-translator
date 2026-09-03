namespace TerminalTranslator.Core.Capture;

public sealed record PreviousCommandRetrievalState(
    CapturePreference Preference,
    CaptureHealthState Health,
    CaptureSessionId CurrentSession,
    IReadOnlyList<CapturedCommand> Records,
    bool IsStoreReliable)
{
    public int FormatVersion { get; init; } = 1;
    public bool CommittedGenerationReliable { get; init; } = true;
    public bool ReferencesComplete { get; init; } = true;
    public bool HashesMatch { get; init; } = true;
    public bool LatestFinalizationSucceeded { get; init; } = true;
}

public static class PreviousCommandRetriever
{
    public static PreviousCommandResult Retrieve(PreviousCommandRetrievalState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Preference == CapturePreference.Disabled)
        {
            return PreviousCommandResult.CaptureDisabled();
        }

        if (state.Health != CaptureHealthState.Healthy)
        {
            return PreviousCommandResult.CaptureUnavailable();
        }

        if (!state.IsStoreReliable || state.CurrentSession is null || state.Records is null ||
            state.FormatVersion != 1 || !state.CommittedGenerationReliable || !state.ReferencesComplete ||
            !state.HashesMatch || !state.LatestFinalizationSucceeded)
        {
            return PreviousCommandResult.UnreliableOrCorrupt();
        }

        long previousSequence = 0;
        foreach (CapturedCommand record in state.Records)
        {
            if (record.Session != state.CurrentSession || record.Sequence <= previousSequence ||
                !record.Boundary.IsReliable || record.Boundary.Sequence != record.Sequence)
            {
                return PreviousCommandResult.UnreliableOrCorrupt();
            }

            previousSequence = record.Sequence;
        }

        CapturedCommand? selected = ContextualCommandSelection.SelectStrictTarget(state.Records);
        if (selected is null)
        {
            return PreviousCommandResult.NoPreviousCommand();
        }

        if (selected.Output.Length == 0)
        {
            return PreviousCommandResult.NoOutput();
        }

        PreviousCommandSnapshot snapshot = new(
            selected.Session,
            selected.Sequence,
            selected.CommandText,
            selected.Output,
            selected.Boundary.NativeExitCode,
            selected.Boundary.WasInterrupted,
            selected.LocalCompleteness,
            selected.OriginalOutputBytes)
        {
            PowerShellSucceeded = selected.Boundary.PowerShellSucceeded,
        };
        return PreviousCommandResult.Success(snapshot);
    }
}
