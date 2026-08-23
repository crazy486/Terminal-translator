namespace TerminalTranslator.Core.Translation;

public enum TranslationRuntimeStage
{
    VtUnitEmitted,
    OwnershipAcceptedProgramOutput,
    OwnershipRejectedArtifact,
    SemanticBoundary,
    OutputBlockOpened,
    OutputBlockAppended,
    OutputBlockClosed,
    ClassifierInvoked,
    ClassifierRejected,
    PrivacyInvoked,
    PrivacyRejected,
    SegmentCreated,
    QueueOffer,
    QueueEnqueued,
    QueueRejected,
    QueueExpired,
    QueueDequeued,
    ProviderStarted,
    ProviderCompleted,
    ProviderFailed,
    CancelRequested,
    CancelObserved,
    TranslationItemCreated,
}

public enum TranslationCancellationReason
{
    None,
    ProviderDeadline,
    GenerationChange,
    TranslationDisabled,
    Shutdown,
    Caller,
    QueueExpired,
    StaleGeneration,
    Unknown,
}

public sealed record TranslationRuntimeEvent(
    TranslationRuntimeStage Stage,
    TimeSpan Timestamp,
    ulong Sequence = 0,
    long Generation = 0,
    TimeSpan? CreatedAt = null,
    TimeSpan? EnqueuedAt = null,
    TimeSpan? DequeuedAt = null,
    TimeSpan? ProviderStartedAt = null,
    TimeSpan? ProviderCompletedAt = null,
    TimeSpan? CancelRequestedAt = null,
    TimeSpan? CancelObservedAt = null,
    TimeSpan? QueueAge = null,
    TimeSpan? ProviderElapsed = null,
    TimeSpan? TotalAge = null,
    TranslationCancellationReason CancelReason = TranslationCancellationReason.None,
    TranslationErrorCode? NormalizedProviderError = null);

public interface ITranslationRuntimeObserver
{
    void Record(TranslationRuntimeEvent runtimeEvent);
}
