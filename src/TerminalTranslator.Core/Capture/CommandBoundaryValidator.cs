namespace TerminalTranslator.Core.Capture;

public sealed record CommandBoundaryEvidence(
    int FormatVersion,
    CaptureSessionId OpeningSession,
    CaptureSessionId ClosingSession,
    long OpeningSequence,
    long ClosingSequence,
    bool HasOpeningBoundary,
    bool HasClosingBoundary,
    long HistoryId,
    string CommandText,
    string TranscriptCommandEcho,
    string OrderedOutput,
    int? ExitCode,
    bool WasInterrupted,
    bool TerminationBoundaryReliable)
{
    public int CommandEchoMatchCount { get; init; } = 1;
    public bool HasUnattributedContentBeforeEcho { get; init; }
    public string? PostCompletionOutput { get; init; }
    public bool? PowerShellSucceeded { get; init; }
}

public sealed record BoundaryValidationResult(
    bool IsReliable,
    CommandBoundary? Boundary,
    string? OrderedOutput,
    string? FailureReason)
{
    public static BoundaryValidationResult Invalid(string reason) => new(false, null, null, reason);
}

public static class CommandBoundaryValidator
{
    public const int CurrentFormatVersion = 1;

    public static BoundaryValidationResult Validate(CommandBoundaryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.FormatVersion != CurrentFormatVersion)
        {
            return BoundaryValidationResult.Invalid("unknown-version");
        }

        if (!evidence.HasOpeningBoundary || !evidence.HasClosingBoundary)
        {
            return BoundaryValidationResult.Invalid("missing-boundary");
        }

        if (evidence.OpeningSession != evidence.ClosingSession ||
            evidence.OpeningSequence <= 0 ||
            evidence.OpeningSequence != evidence.ClosingSequence)
        {
            return BoundaryValidationResult.Invalid("session-or-sequence-mismatch");
        }

        if (evidence.HistoryId <= 0 || string.IsNullOrWhiteSpace(evidence.CommandText) ||
            !string.Equals(evidence.CommandText, evidence.TranscriptCommandEcho, StringComparison.Ordinal) ||
            evidence.CommandEchoMatchCount != 1 || evidence.HasUnattributedContentBeforeEcho)
        {
            return BoundaryValidationResult.Invalid("command-identity-mismatch");
        }

        if (evidence.WasInterrupted && !evidence.TerminationBoundaryReliable)
        {
            return BoundaryValidationResult.Invalid("unreliable-interruption");
        }

        if (evidence.OrderedOutput is null)
        {
            return BoundaryValidationResult.Invalid("missing-output-span");
        }

        CommandBoundary boundary = new(
            evidence.OpeningSession,
            evidence.OpeningSequence,
            evidence.CommandText,
            true,
            evidence.ExitCode,
            evidence.WasInterrupted,
            evidence.PowerShellSucceeded);
        return new BoundaryValidationResult(true, boundary, evidence.OrderedOutput, null);
    }
}
