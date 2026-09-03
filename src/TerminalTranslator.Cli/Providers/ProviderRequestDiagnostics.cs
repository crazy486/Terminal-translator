using System.Text.Json;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Providers;

internal sealed record ProviderRequestDiagnostic(
    Uri Endpoint,
    string Model,
    DateTimeOffset RequestStartedUtc,
    string Outcome,
    int? HttpStatusCode,
    long? ResponseHeadersElapsedMilliseconds,
    long ResponseCompletedElapsedMilliseconds,
    string? ExceptionType,
    string? CancellationReason,
    string? TimeoutSource,
    string? MalformedResponseReason,
    bool? ReasoningContentPresent);

internal interface IProviderRequestDiagnosticSink
{
    void Write(ProviderRequestDiagnostic diagnostic);
}

internal sealed class NullProviderRequestDiagnosticSink : IProviderRequestDiagnosticSink
{
    public static NullProviderRequestDiagnosticSink Instance { get; } = new();

    public void Write(ProviderRequestDiagnostic diagnostic)
    {
    }
}

internal sealed class JsonProviderRequestDiagnosticSink(TextWriter writer) : IProviderRequestDiagnosticSink
{
    internal const string EnableEnvironmentVariable = "TT_PROVIDER_DIAGNOSTICS";

    public void Write(ProviderRequestDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                endpoint = SanitizeEndpoint(diagnostic.Endpoint),
                model = diagnostic.Model,
                requestStartedUtc = diagnostic.RequestStartedUtc,
                outcome = diagnostic.Outcome,
                httpStatusCode = diagnostic.HttpStatusCode,
                responseHeadersElapsedMilliseconds = diagnostic.ResponseHeadersElapsedMilliseconds,
                responseCompletedElapsedMilliseconds = diagnostic.ResponseCompletedElapsedMilliseconds,
                exceptionType = diagnostic.ExceptionType,
                cancellationReason = diagnostic.CancellationReason,
                timeoutSource = diagnostic.TimeoutSource,
                malformedResponseReason = diagnostic.MalformedResponseReason,
                reasoningContentPresent = diagnostic.ReasoningContentPresent,
            });
            writer.WriteLine($"[tt:provider] {json}");
        }
        catch (Exception)
        {
            // Diagnostics are optional and must never replace the provider result.
        }
    }

    internal static IProviderRequestDiagnosticSink Create(
        TextWriter writer,
        Func<string, string?> environmentVariableReader) =>
        string.Equals(
            environmentVariableReader(EnableEnvironmentVariable),
            "1",
            StringComparison.Ordinal)
            ? new JsonProviderRequestDiagnosticSink(writer)
            : NullProviderRequestDiagnosticSink.Instance;

    private static string SanitizeEndpoint(Uri endpoint)
    {
        UriBuilder sanitized = new(endpoint)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return sanitized.Uri.AbsoluteUri;
    }
}

internal sealed class JsonAssistancePipelineDiagnosticSink(TextWriter writer) : IAssistancePipelineDiagnosticSink
{
    public void Write(AssistancePipelineDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                stage = diagnostic.Stage.ToString(),
                retrievalKind = diagnostic.RetrievalKind.ToString(),
                retrievedOutputUtf8Bytes = diagnostic.RetrievedOutputUtf8Bytes,
                asciiLetterCount = diagnostic.AsciiLetterCount,
                controlCharacterCount = diagnostic.ControlCharacterCount,
                englishEligible = diagnostic.EnglishEligible,
                selectionSupported = diagnostic.SelectionSupported,
                selectedOutputUtf8Bytes = diagnostic.SelectedOutputUtf8Bytes,
            });
            writer.WriteLine($"[tt:pipeline] {json}");
        }
        catch (Exception)
        {
            // Diagnostics are optional and must never replace the assistance outcome.
        }
    }

    internal static IAssistancePipelineDiagnosticSink Create(
        TextWriter writer,
        Func<string, string?> environmentVariableReader) =>
        string.Equals(
            environmentVariableReader(JsonProviderRequestDiagnosticSink.EnableEnvironmentVariable),
            "1",
            StringComparison.Ordinal)
            ? new JsonAssistancePipelineDiagnosticSink(writer)
            : NullAssistancePipelineDiagnosticSink.Instance;
}

internal static class ProviderRequestDiagnosticFactory
{
    internal static ProviderRequestDiagnostic Success(
        Uri endpoint,
        string model,
        DateTimeOffset startedUtc,
        ProviderTransportResult transport) =>
        new(
            endpoint,
            model,
            startedUtc,
            "success",
            (int)transport.StatusCode,
            transport.ResponseHeadersElapsedMilliseconds,
            transport.ResponseCompletedElapsedMilliseconds,
            null,
            null,
            null,
            null,
            null);

    internal static ProviderRequestDiagnostic Failure(
        Uri endpoint,
        string model,
        DateTimeOffset startedUtc,
        TranslationProviderException exception,
        long completedElapsedMilliseconds)
    {
        TranslationProviderFailureDetails? details = exception.Details;
        return new(
            endpoint,
            model,
            startedUtc,
            details?.Source.ToString() ?? exception.Code.ToString(),
            details?.HttpStatusCode,
            details?.ResponseHeadersElapsedMilliseconds,
            details?.ResponseCompletedElapsedMilliseconds ?? completedElapsedMilliseconds,
            details?.ExceptionType ?? exception.InnerException?.GetType().FullName ?? exception.GetType().FullName,
            details?.CancellationReason,
            details?.TimeoutSource,
            details?.MalformedResponseReason,
            details?.ReasoningContentPresent);
    }

    internal static ProviderRequestDiagnostic CallerCanceled(
        Uri endpoint,
        string model,
        DateTimeOffset startedUtc,
        OperationCanceledException exception,
        long completedElapsedMilliseconds) =>
        new(
            endpoint,
            model,
            startedUtc,
            "canceled",
            null,
            null,
            completedElapsedMilliseconds,
            exception.GetType().FullName,
            "caller-cancellation",
            null,
            null,
            null);
}
