using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Providers;

public sealed class ChatCompletionTranslationProvider(
    ProviderSettings settings,
    HttpClient httpClient,
    Func<string, string?> environmentVariableReader) : ITranslationProvider
{
    private const int MaximumResponseBytes = 32 * 1024;
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

        string? credential = environmentVariableReader(settings.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new TranslationProviderException(TranslationErrorCode.Authentication);
        }

        ChatCompletionRequestDto payload = new(
            settings.Model,
            [new("system", SystemPrompt), new("user", request.SourceText)],
            0);
        string json = JsonSerializer.Serialize(payload, ProviderJsonContext.Default.ChatCompletionRequestDto);
        using HttpRequestMessage message = new(HttpMethod.Post, settings.Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);

        TimeSpan timeout = request.Deadline > TimeSpan.Zero && request.Deadline < settings.RequestTimeout
            ? request.Deadline
            : settings.RequestTimeout;
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationProviderException(TranslationErrorCode.Timeout);
        }
        catch (HttpRequestException exception)
        {
            throw new TranslationProviderException(TranslationErrorCode.Unavailable, exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new TranslationProviderException(MapStatus(response.StatusCode));
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(timeoutSource.Token).ConfigureAwait(false);
            if (bytes.Length > MaximumResponseBytes)
            {
                throw new TranslationProviderException(TranslationErrorCode.InvalidResponse);
            }

            ChatCompletionResponseDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize(bytes, ProviderJsonContext.Default.ChatCompletionResponseDto);
            }
            catch (JsonException exception)
            {
                throw new TranslationProviderException(TranslationErrorCode.InvalidResponse, exception);
            }

            string? translatedText = dto?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
            if (string.IsNullOrEmpty(translatedText) || Encoding.UTF8.GetByteCount(translatedText) > 16 * 1024)
            {
                throw new TranslationProviderException(TranslationErrorCode.InvalidResponse);
            }

            return new TranslationResult(translatedText, dto?.Id);
        }
    }

    private static TranslationErrorCode MapStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => TranslationErrorCode.Authentication,
        HttpStatusCode.TooManyRequests => TranslationErrorCode.RateLimited,
        HttpStatusCode.RequestTimeout => TranslationErrorCode.Unavailable,
        >= HttpStatusCode.InternalServerError => TranslationErrorCode.Unavailable,
        _ => TranslationErrorCode.InvalidResponse,
    };
}
