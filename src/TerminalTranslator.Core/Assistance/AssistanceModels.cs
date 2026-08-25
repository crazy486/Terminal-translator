using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Assistance;

public enum AssistanceRequestKind
{
    LastTranslation,
    QuestionOnly,
    QuestionWithPreviousCommand,
}

public sealed record ResponseLanguageSelection
{
    public ResponseLanguageSelection(string language, string instruction)
    {
        if (string.IsNullOrWhiteSpace(language)) throw new ArgumentException("Language is required.", nameof(language));
        if (string.IsNullOrWhiteSpace(instruction)) throw new ArgumentException("Instruction is required.", nameof(instruction));
        Language = language;
        Instruction = instruction;
    }

    public string Language { get; }
    public string Instruction { get; }
}

public sealed record TerminationFacts(int? ExitCode, bool WasInterrupted)
{
    public string ToProviderText() =>
        $"exitCode={(ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")}; interrupted={WasInterrupted.ToString().ToLowerInvariant()}";
}

public sealed record AssistanceRequest
{
    private AssistanceRequest(
        AssistanceRequestKind kind,
        string question,
        ResponseLanguageSelection responseLanguage,
        string commandText,
        string selectedOutput,
        TerminationFacts termination,
        LocalCaptureCompleteness localCompleteness,
        long originalOutputBytes,
        AiInputCompleteness aiCompleteness)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(responseLanguage);
        ArgumentNullException.ThrowIfNull(commandText);
        ArgumentNullException.ThrowIfNull(selectedOutput);
        ArgumentNullException.ThrowIfNull(termination);
        if (kind == AssistanceRequestKind.LastTranslation && string.IsNullOrWhiteSpace(commandText))
            throw new ArgumentException("Command text is required.", nameof(commandText));
        if (kind is AssistanceRequestKind.QuestionOnly or AssistanceRequestKind.QuestionWithPreviousCommand &&
            string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question is required.", nameof(question));
        if (kind == AssistanceRequestKind.QuestionWithPreviousCommand && string.IsNullOrWhiteSpace(commandText))
            throw new ArgumentException("Command text is required.", nameof(commandText));
        if (originalOutputBytes < 0) throw new ArgumentOutOfRangeException(nameof(originalOutputBytes));
        Kind = kind;
        Question = question;
        ResponseLanguage = responseLanguage.Language;
        ResponseLanguageInstruction = responseLanguage.Instruction;
        CommandText = commandText;
        SelectedOutput = selectedOutput;
        Termination = termination;
        LocalCompleteness = localCompleteness;
        OriginalOutputBytes = originalOutputBytes;
        AiCompleteness = aiCompleteness;
    }

    public AssistanceRequestKind Kind { get; }
    public string Question { get; }
    public string ResponseLanguage { get; }
    public string ResponseLanguageInstruction { get; }
    public string CommandText { get; }
    public string SelectedOutput { get; }
    public TerminationFacts Termination { get; }
    public LocalCaptureCompleteness LocalCompleteness { get; }
    public long OriginalOutputBytes { get; }
    public AiInputCompleteness AiCompleteness { get; }

    public static AssistanceRequest CreateLastTranslation(
        string commandText,
        string selectedOutput,
        TerminationFacts termination,
        LocalCaptureCompleteness localCompleteness = LocalCaptureCompleteness.Complete,
        long originalOutputBytes = 0,
        AiInputCompleteness aiCompleteness = AiInputCompleteness.Complete) =>
        new(AssistanceRequestKind.LastTranslation, string.Empty,
            new("Simplified Chinese", "Translate the selected terminal output to Simplified Chinese."),
            commandText, selectedOutput, termination,
            localCompleteness, originalOutputBytes, aiCompleteness);

    public static AssistanceRequest CreateQuestionOnly(
        string question,
        ResponseLanguageSelection responseLanguage) =>
        new(AssistanceRequestKind.QuestionOnly, question, responseLanguage,
            string.Empty, string.Empty, new TerminationFacts(null, false),
            LocalCaptureCompleteness.Complete, 0, AiInputCompleteness.Complete);

    public static AssistanceRequest CreateQuestionWithPreviousCommand(
        string question,
        ResponseLanguageSelection responseLanguage,
        string commandText,
        string selectedOutput,
        TerminationFacts termination,
        LocalCaptureCompleteness localCompleteness = LocalCaptureCompleteness.Complete,
        long originalOutputBytes = 0,
        AiInputCompleteness aiCompleteness = AiInputCompleteness.Complete) =>
        new(AssistanceRequestKind.QuestionWithPreviousCommand, question, responseLanguage,
            commandText, selectedOutput, termination, localCompleteness, originalOutputBytes, aiCompleteness);

    public AssistanceRequest WithSelectedOutput(string output, AiInputCompleteness completeness) =>
        new(Kind, Question, new(ResponseLanguage, ResponseLanguageInstruction), CommandText, output,
            Termination, LocalCompleteness, OriginalOutputBytes, completeness);
}

public sealed record AssistanceResult
{
    public AssistanceResult(string translation, string recommendation, string? providerRequestId = null)
    {
        if (string.IsNullOrWhiteSpace(translation)) throw new ArgumentException("Translation is required.", nameof(translation));
        if (string.IsNullOrWhiteSpace(recommendation)) throw new ArgumentException("Recommendation is required.", nameof(recommendation));
        Translation = translation;
        Recommendation = recommendation;
        Answer = string.Empty;
        ProviderRequestId = providerRequestId;
    }

    private AssistanceResult(string answer, string? providerRequestId)
    {
        if (string.IsNullOrWhiteSpace(answer)) throw new ArgumentException("Answer is required.", nameof(answer));
        Translation = string.Empty;
        Recommendation = string.Empty;
        Answer = answer;
        ProviderRequestId = providerRequestId;
    }

    public string Translation { get; }
    public string Recommendation { get; }
    public string Answer { get; }
    public string? ProviderRequestId { get; }

    public static AssistanceResult CreateAnswer(string answer, string? providerRequestId = null) =>
        new(answer, providerRequestId);
}

public static class AssistanceConsentScopes
{
    public const string QuestionOnly = "question-only";
    public const string PreviousCommandContext = "previous-command-context";

    public static string For(AssistanceRequestKind kind) => kind switch
    {
        AssistanceRequestKind.QuestionOnly => QuestionOnly,
        AssistanceRequestKind.LastTranslation or AssistanceRequestKind.QuestionWithPreviousCommand => PreviousCommandContext,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed class MatchingConsentAssertion
{
    private MatchingConsentAssertion(string fingerprint, string scope)
    {
        Fingerprint = fingerprint;
        Scope = scope;
    }

    internal string Fingerprint { get; }
    internal string Scope { get; }

    public static MatchingConsentAssertion? TryCreate(
        string expectedFingerprint,
        string grantedFingerprint,
        string scope)
    {
        if (string.IsNullOrWhiteSpace(expectedFingerprint) ||
            string.IsNullOrWhiteSpace(grantedFingerprint) ||
            string.IsNullOrWhiteSpace(scope))
            return null;

        return string.Equals(expectedFingerprint, grantedFingerprint, StringComparison.Ordinal)
            ? new MatchingConsentAssertion(expectedFingerprint, scope)
            : null;
    }
}

public sealed class AuthorizedAssistanceRequest
{
    private readonly string _payloadFingerprint;

    internal AuthorizedAssistanceRequest(
        AssistanceRequest request,
        MatchingConsentAssertion consent,
        string payloadFingerprint)
    {
        Request = request;
        ConsentFingerprint = consent.Fingerprint;
        Scope = consent.Scope;
        _payloadFingerprint = payloadFingerprint;
    }

    public AssistanceRequest Request { get; }
    public string ConsentFingerprint { get; }
    public string Scope { get; }

    public void AssertValid()
    {
        string requiredScope = AssistanceConsentScopes.For(Request.Kind);
        string currentFingerprint = AssistancePrivacyGate.ComputePayloadFingerprint(Request);
        if (!string.Equals(Scope, requiredScope, StringComparison.Ordinal) ||
            !string.Equals(_payloadFingerprint, currentFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The assistance authorization assertion is invalid.");
    }
}

public enum AssistanceAuthorizationFailure
{
    None,
    ConsentMissingOrDeclined,
    SuspectedSecret,
}

public sealed record AssistanceAuthorizationDecision(
    AssistanceAuthorizationFailure Failure,
    AuthorizedAssistanceRequest? Request)
{
    public static AssistanceAuthorizationDecision Authorized(AuthorizedAssistanceRequest request) =>
        new(AssistanceAuthorizationFailure.None, request);

    public static AssistanceAuthorizationDecision Denied() =>
        new(AssistanceAuthorizationFailure.ConsentMissingOrDeclined, null);

    public static AssistanceAuthorizationDecision PrivacyBlocked() =>
        new(AssistanceAuthorizationFailure.SuspectedSecret, null);
}

public interface IAssistanceRequestAuthorizer
{
    Task<AssistanceAuthorizationDecision> AuthorizeAsync(
        AssistanceRequest request,
        CancellationToken cancellationToken);
}

public interface IAssistanceProvider
{
    Task<AssistanceResult> CompleteAsync(AuthorizedAssistanceRequest request, CancellationToken cancellationToken);
}

public enum AssistanceFailureKind
{
    None,
    NoPreviousCommand,
    NoOutput,
    CaptureDisabled,
    CaptureUnavailable,
    UnreliableOrCorrupt,
    NoTranslatableEnglish,
    RequiredContextTooLarge,
    ConsentMissingOrDeclined,
    SuspectedSecret,
    ProviderTimeout,
    ProviderError,
}

public sealed record LastAssistanceOutcome(
    AssistanceFailureKind Failure,
    AssistanceRequest? Request = null,
    AssistanceResult? Result = null);

public sealed record AskAssistanceOutcome(
    AssistanceFailureKind Failure,
    AssistanceRequest? Request = null,
    AssistanceResult? Result = null)
{
    public static AskAssistanceOutcome Success(AssistanceRequest request, AssistanceResult result) =>
        new(AssistanceFailureKind.None, request, result);

    public static AskAssistanceOutcome NoContext(AssistanceFailureKind failure) => new(failure);
}
