namespace TerminalTranslator.Core.Models;

public enum SourceBoundary { Line, Block, IdlePrompt }

public enum TranslationPriority { High, Normal }

public enum SegmentState
{
    Capturing,
    Ready,
    SkippedSensitive,
    SkippedLowValue,
    DroppedOverload,
    Queued,
    Translating,
    Translated,
    Failed,
    Canceled,
    Expired,
}

public sealed record LayoutHints(int LineCount, IReadOnlyList<int> Indent);

public sealed record OutputSegment(
    Guid SessionId,
    long Generation,
    ulong Sequence,
    string NormalizedText,
    LayoutHints Layout,
    SourceBoundary SourceBoundary,
    TranslationPriority Priority,
    string? RedrawKey,
    TimeSpan CapturedAt,
    SegmentState State = SegmentState.Ready);

public enum PrivacyOutcome { Allow, Skip }

public enum PrivacyReasonCode
{
    None,
    CredentialAssignment,
    AuthorizationValue,
    PrivateKey,
    TokenShape,
    CredentialUri,
    MalformedSensitiveBlock,
    DetectorFailure,
}

public sealed record PrivacyDecision(ulong SegmentSequence, PrivacyOutcome Outcome, PrivacyReasonCode ReasonCode);

public sealed record TranslationItem(
    Guid SessionId,
    long Generation,
    ulong SegmentSequence,
    string SourceText,
    string TranslatedText,
    LayoutHints Layout,
    string? ProviderRequestId,
    TimeSpan CompletedAt);

public enum StatusKind { State, PrivacySkip, Degraded, ProviderError, SessionEnded }

public sealed record StatusEvent(Guid SessionId, StatusKind Kind, string Code, int? Count, DateTimeOffset OccurredAt);
