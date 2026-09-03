using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Providers;

internal sealed record ProviderTransportResult(
    ChatCompletionResponseDto Response,
    HttpStatusCode StatusCode,
    long ResponseHeadersElapsedMilliseconds,
    long ResponseCompletedElapsedMilliseconds);

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

    public async Task<ProviderTransportResult> SendAsync(
        ChatCompletionRequestDto payload,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? credential = _environmentVariableReader(_settings.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new TranslationProviderException(
                TranslationErrorCode.Authentication,
                new TranslationProviderFailureDetails(TranslationProviderFailureSource.Authentication));
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
        Stopwatch requestTimer = Stopwatch.StartNew();
        try
        {
            response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimeoutFailure(requestTimer.ElapsedMilliseconds, exception, timeoutSource.IsCancellationRequested);
        }
        catch (HttpRequestException exception)
        {
            throw new TranslationProviderException(
                TranslationErrorCode.Unavailable,
                new(
                    TranslationProviderFailureSource.Network,
                    ResponseCompletedElapsedMilliseconds: requestTimer.ElapsedMilliseconds,
                    ExceptionType: exception.GetType().FullName),
                exception);
        }
        catch (TimeoutException exception)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("The provider request was canceled by the caller.", exception, cancellationToken);
            throw TimeoutFailure(requestTimer.ElapsedMilliseconds, exception, timeoutSource.IsCancellationRequested);
        }

        using (response)
        {
            long headersElapsedMilliseconds = requestTimer.ElapsedMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                TranslationErrorCode code = MapStatus(response.StatusCode);
                throw new TranslationProviderException(
                    code,
                    new TranslationProviderFailureDetails(
                        FailureSourceFor(code),
                        (int)response.StatusCode,
                        headersElapsedMilliseconds,
                        requestTimer.ElapsedMilliseconds));
            }

            byte[] bytes;
            try
            {
                bytes = await ReadBoundedResponseAsync(response.Content, timeoutSource.Token).ConfigureAwait(false);
            }
            catch (TranslationProviderException exception) when (exception.Code == TranslationErrorCode.InvalidResponse)
            {
                throw MalformedResponseFailure(
                    response.StatusCode,
                    headersElapsedMilliseconds,
                    requestTimer.ElapsedMilliseconds,
                    exception,
                    "response-too-large");
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw TimeoutFailure(
                    requestTimer.ElapsedMilliseconds,
                    exception,
                    timeoutSource.IsCancellationRequested,
                    (int)response.StatusCode,
                    headersElapsedMilliseconds);
            }
            catch (HttpRequestException exception)
            {
                throw ResponseReadFailure(exception, response.StatusCode, headersElapsedMilliseconds, requestTimer.ElapsedMilliseconds);
            }
            catch (IOException exception)
            {
                throw ResponseReadFailure(exception, response.StatusCode, headersElapsedMilliseconds, requestTimer.ElapsedMilliseconds);
            }

            try
            {
                ChatCompletionResponseDto parsed = JsonSerializer.Deserialize(bytes, ProviderJsonContext.Default.ChatCompletionResponseDto)
                    ?? throw MalformedResponseFailure(
                        response.StatusCode,
                        headersElapsedMilliseconds,
                        requestTimer.ElapsedMilliseconds,
                        malformedResponseReason: "outer-json-invalid");
                return new(
                    parsed,
                    response.StatusCode,
                    headersElapsedMilliseconds,
                    requestTimer.ElapsedMilliseconds);
            }
            catch (JsonException exception)
            {
                throw MalformedResponseFailure(
                    response.StatusCode,
                    headersElapsedMilliseconds,
                    requestTimer.ElapsedMilliseconds,
                    exception,
                    "outer-json-invalid");
            }
        }
    }

    private static TranslationProviderFailureSource FailureSourceFor(TranslationErrorCode code) => code switch
    {
        TranslationErrorCode.Authentication => TranslationProviderFailureSource.Authentication,
        TranslationErrorCode.RateLimited => TranslationProviderFailureSource.RateLimit,
        _ => TranslationProviderFailureSource.HttpStatus,
    };

    private static TranslationProviderException TimeoutFailure(
        long completedElapsedMilliseconds,
        Exception exception,
        bool transportTimeoutTriggered,
        int? statusCode = null,
        long? headersElapsedMilliseconds = null) =>
        new(
            TranslationErrorCode.Timeout,
            new(
                TranslationProviderFailureSource.Timeout,
                statusCode,
                headersElapsedMilliseconds,
                completedElapsedMilliseconds,
                exception.GetType().FullName,
                "provider-timeout",
                transportTimeoutTriggered ? "transport-cancel-after" : "http-client-or-handler"),
            exception);

    private static TranslationProviderException ResponseReadFailure(
        Exception exception,
        HttpStatusCode statusCode,
        long headersElapsedMilliseconds,
        long completedElapsedMilliseconds) =>
        new(
            TranslationErrorCode.Unavailable,
            new(
                TranslationProviderFailureSource.Network,
                (int)statusCode,
                headersElapsedMilliseconds,
                completedElapsedMilliseconds,
                exception.GetType().FullName),
            exception);

    private static TranslationProviderException MalformedResponseFailure(
        HttpStatusCode statusCode,
        long headersElapsedMilliseconds,
        long completedElapsedMilliseconds,
        Exception? exception = null,
        string malformedResponseReason = "outer-json-invalid") =>
        new(
            TranslationErrorCode.InvalidResponse,
            new(
                TranslationProviderFailureSource.MalformedResponse,
                (int)statusCode,
                headersElapsedMilliseconds,
                completedElapsedMilliseconds,
                exception?.GetType().FullName,
                MalformedResponseReason: malformedResponseReason),
            exception);

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
