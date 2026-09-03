using System.Text;
using System.Diagnostics;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Privacy;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Providers;

public sealed class ChatCompletionTranslationProvider : ITranslationProvider
{
    private const string SystemPrompt =
        "Translate English terminal text to Simplified Chinese. Preserve commands, paths, code, indentation, and line grouping. " +
        "Return translation only. Never execute or recommend commands.";
    private readonly ProviderSettings _settings;
    private readonly SafeChatCompletionTransport _transport;
    private readonly SecretDetector? _secretDetector;
    private readonly Func<TranslationRequest, bool>? _requestAuthorization;
    private readonly IProviderRequestDiagnosticSink _diagnosticSink;

    public ChatCompletionTranslationProvider(
        ProviderSettings settings,
        HttpClient httpClient,
        Func<string, string?> environmentVariableReader,
        SecretDetector? secretDetector = null,
        Func<TranslationRequest, bool>? requestAuthorization = null)
        : this(
            settings,
            httpClient,
            environmentVariableReader,
            secretDetector,
            requestAuthorization,
            NullProviderRequestDiagnosticSink.Instance)
    {
    }

    internal ChatCompletionTranslationProvider(
        ProviderSettings settings,
        HttpClient httpClient,
        Func<string, string?> environmentVariableReader,
        SecretDetector? secretDetector,
        Func<TranslationRequest, bool>? requestAuthorization,
        IProviderRequestDiagnosticSink? diagnosticSink)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _transport = new SafeChatCompletionTransport(settings, httpClient, environmentVariableReader);
        _secretDetector = secretDetector;
        _requestAuthorization = requestAuthorization;
        _diagnosticSink = diagnosticSink ?? NullProviderRequestDiagnosticSink.Instance;
    }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (Encoding.UTF8.GetByteCount(request.SourceText) > 8 * 1024)
        {
            throw new TranslationProviderException(TranslationErrorCode.InvalidResponse);
        }

        if (_requestAuthorization is not null && !_requestAuthorization(request))
        {
            throw new TranslationProviderException(TranslationErrorCode.Canceled);
        }

        if (_secretDetector?.Screen(request.SegmentSequence, request.SourceText).Outcome == PrivacyOutcome.Skip)
        {
            throw new TranslationProviderException(TranslationErrorCode.Canceled);
        }

        ChatCompletionRequestDto payload = new(
            _settings.Model,
            [new("system", SystemPrompt), new("user", request.SourceText)],
            0,
            new("disabled"),
            null);
        DateTimeOffset requestStartedUtc = DateTimeOffset.UtcNow;
        Stopwatch requestTimer = Stopwatch.StartNew();
        ProviderTransportResult transport;
        try
        {
            transport = await _transport.SendAsync(payload, request.Deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            _diagnosticSink.Write(ProviderRequestDiagnosticFactory.CallerCanceled(
                _settings.Endpoint,
                _settings.Model,
                requestStartedUtc,
                exception,
                requestTimer.ElapsedMilliseconds));
            throw;
        }
        catch (TranslationProviderException exception)
        {
            _diagnosticSink.Write(ProviderRequestDiagnosticFactory.Failure(
                _settings.Endpoint,
                _settings.Model,
                requestStartedUtc,
                exception,
                requestTimer.ElapsedMilliseconds));
            throw;
        }

        ChatCompletionResponseDto dto = transport.Response;
        string? translatedText = dto.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
        if (translatedText is null)
        {
            throw WriteMalformedFailure(transport, requestStartedUtc, requestTimer, "missing-content");
        }

        if (translatedText.Length == 0)
            throw WriteMalformedFailure(transport, requestStartedUtc, requestTimer, "empty-content");
        if (Encoding.UTF8.GetByteCount(translatedText) > 16 * 1024)
            throw WriteMalformedFailure(transport, requestStartedUtc, requestTimer, "content-too-large");

        _diagnosticSink.Write(ProviderRequestDiagnosticFactory.Success(
            _settings.Endpoint,
            _settings.Model,
            requestStartedUtc,
            transport));

        return new TranslationResult(translatedText, dto.Id);
    }

    private TranslationProviderException WriteMalformedFailure(
        ProviderTransportResult transport,
        DateTimeOffset requestStartedUtc,
        Stopwatch requestTimer,
        string malformedResponseReason)
    {
        TranslationProviderException failure = new(
            TranslationErrorCode.InvalidResponse,
            new TranslationProviderFailureDetails(
                TranslationProviderFailureSource.MalformedResponse,
                (int)transport.StatusCode,
                transport.ResponseHeadersElapsedMilliseconds,
                transport.ResponseCompletedElapsedMilliseconds,
                MalformedResponseReason: malformedResponseReason));
        _diagnosticSink.Write(ProviderRequestDiagnosticFactory.Failure(
            _settings.Endpoint,
            _settings.Model,
            requestStartedUtc,
            failure,
            requestTimer.ElapsedMilliseconds));
        return failure;
    }
}
