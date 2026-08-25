using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Providers;

internal sealed class SafeChatCompletionTransport
{
    internal const int MaximumResponseBytes = 32 * 1024;
    private readonly ProviderSettings _settings;
    private readonly HttpClient _client;
    private readonly Func<string, string?> _environmentVariableReader;

    public SafeChatCompletionTransport(
        ProviderSettings settings,
        HttpClient client,
        Func<string, string?> environmentVariableReader)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _environmentVariableReader = environmentVariableReader ?? throw new ArgumentNullException(nameof(environmentVariableReader));
        _client.DefaultRequestHeaders.Remove("Cookie");
        _client.DefaultRequestHeaders.Remove("Cookie2");
    }

    public async Task<ChatCompletionResponseDto> SendAsync(
        ChatCompletionRequestDto payload,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? credential = _environmentVariableReader(_settings.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new TranslationProviderException(TranslationErrorCode.Authentication);
        }

        string json = JsonSerializer.Serialize(payload, ProviderJsonContext.Default.ChatCompletionRequestDto);
        using HttpRequestMessage message = new(HttpMethod.Post, _settings.Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);

        TimeSpan timeout = deadline > TimeSpan.Zero && deadline < _settings.RequestTimeout
            ? deadline
            : _settings.RequestTimeout;
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
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

            byte[] bytes;
            try
            {
                bytes = await ReadBoundedResponseAsync(response.Content, timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TranslationProviderException(TranslationErrorCode.Timeout);
            }
            catch (HttpRequestException exception)
            {
                throw new TranslationProviderException(TranslationErrorCode.Unavailable, exception);
            }
            catch (IOException exception)
            {
                throw new TranslationProviderException(TranslationErrorCode.Unavailable, exception);
            }

            try
            {
                return JsonSerializer.Deserialize(bytes, ProviderJsonContext.Default.ChatCompletionResponseDto)
                    ?? throw new TranslationProviderException(TranslationErrorCode.InvalidResponse);
            }
            catch (JsonException exception)
            {
                throw new TranslationProviderException(TranslationErrorCode.InvalidResponse, exception);
            }
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

    private static async Task<byte[]> ReadBoundedResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new TranslationProviderException(TranslationErrorCode.InvalidResponse);
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new(content.Headers.ContentLength is long length ? checked((int)length) : 4096);
        byte[] chunk = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > MaximumResponseBytes)
                throw new TranslationProviderException(TranslationErrorCode.InvalidResponse);
            buffer.Write(chunk, 0, read);
        }
    }
}
