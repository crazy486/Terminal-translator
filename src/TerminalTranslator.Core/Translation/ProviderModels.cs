namespace TerminalTranslator.Core.Translation;

public sealed record TranslationRequest(
    long SessionGeneration,
    ulong SegmentSequence,
    string SourceText,
    string SourceLanguage,
    string TargetLanguage,
    TimeSpan Deadline);

public sealed record TranslationResult(string TranslatedText, string? ProviderRequestId = null);

public enum TranslationErrorCode
{
    Canceled,
    Timeout,
    Authentication,
    RateLimited,
    Unavailable,
    InvalidResponse,
}

public enum TranslationProviderFailureSource
{
    Unspecified,
    Authorization,
    Authentication,
    RateLimit,
    Network,
    HttpStatus,
    Timeout,
    MalformedResponse,
}

public sealed record TranslationProviderFailureDetails(
    TranslationProviderFailureSource Source,
    int? HttpStatusCode = null,
    long? ResponseHeadersElapsedMilliseconds = null,
    long? ResponseCompletedElapsedMilliseconds = null,
    string? ExceptionType = null,
    string? CancellationReason = null,
    string? TimeoutSource = null,
    string? MalformedResponseReason = null,
    bool? ReasoningContentPresent = null);

public sealed class TranslationProviderException : Exception
{
    public TranslationProviderException(TranslationErrorCode code)
        : this(code, null, null)
    {
    }

    public TranslationProviderException(TranslationErrorCode code, Exception innerException)
        : this(code, null, innerException)
    {
    }

    public TranslationProviderException(
        TranslationErrorCode code,
        TranslationProviderFailureDetails? details,
        Exception? innerException = null)
        : base($"Translation provider failed: {code}.", innerException)
    {
        Code = code;
        Details = details;
    }

    public TranslationErrorCode Code { get; }

    public TranslationProviderFailureDetails? Details { get; }
}
