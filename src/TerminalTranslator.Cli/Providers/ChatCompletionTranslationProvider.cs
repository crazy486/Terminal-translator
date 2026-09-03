using System.Text;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Privacy;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Providers;

public sealed class ChatCompletionTranslationProvider(
    ProviderSettings settings,
    HttpClient httpClient,
    Func<string, string?> environmentVariableReader,
    SecretDetector? secretDetector = null,
    Func<TranslationRequest, bool>? requestAuthorization = null) : ITranslationProvider
{
    private readonly SafeChatCompletionTransport _transport = new(settings, httpClient, environmentVariableReader);
    private const string SystemPrompt =
        "Translate English terminal text to Simplified Chinese. Preserve commands, paths, code, indentation, and line grouping. " +
        "Return translation only. Never execute or recommend commands.";

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (Encoding.UTF8.GetByteCount(request.SourceText) > 8 * 1024)
        {
            throw new TranslationProviderException(TranslationErrorCode.InvalidResponse);
        }

        if (requestAuthorization is not null && !requestAuthorization(request))
        {
            throw new TranslationProviderException(TranslationErrorCode.Canceled);
        }

        if (secretDetector?.Screen(request.SegmentSequence, request.SourceText).Outcome == PrivacyOutcome.Skip)
        {
            throw new TranslationProviderException(TranslationErrorCode.Canceled);
        }

        ChatCompletionRequestDto payload = new(
            settings.Model,
            [new("system", SystemPrompt), new("user", request.SourceText)],
            0,
            new("disabled"),
            null);
        ProviderTransportResult transport = await _transport.SendAsync(payload, request.Deadline, cancellationToken).ConfigureAwait(false);
        ChatCompletionResponseDto dto = transport.Response;
        string? translatedText = dto.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
        if (string.IsNullOrEmpty(translatedText) || Encoding.UTF8.GetByteCount(translatedText) > 16 * 1024)
        {
            throw new TranslationProviderException(TranslationErrorCode.InvalidResponse);
        }

        return new TranslationResult(translatedText, dto.Id);
    }
}
