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

public sealed class TranslationProviderException : Exception
{
    public TranslationProviderException(TranslationErrorCode code)
        : base($"Translation provider failed: {code}.") => Code = code;

    public TranslationProviderException(TranslationErrorCode code, Exception innerException)
        : base($"Translation provider failed: {code}.", innerException) => Code = code;

    public TranslationErrorCode Code { get; }
}
