using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Tests.Fixtures;

public enum StubTranslationScenario
{
    Success,
    Timeout,
    Unauthorized,
    Forbidden,
    RateLimited,
    RequestTimeout,
    ServerError,
    MalformedResponse,
    EmptyResponse,
    OversizedResponse,
    Redirect,
}

/// <summary>
/// Deterministic loopback-only chat-completion server for validation. Request content exists only
/// in a handler-local buffer while its shape is checked and is never exposed, logged, or persisted.
/// </summary>
public sealed class StubTranslationServer : IAsyncDisposable
{
    private const int MaximumRequestBytes = 64 * 1024;
    private const int OversizedProviderResponseBytes = (32 * 1024) + 1;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly TaskCompletionSource _timeoutRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _acceptLoop;
    private int _requestCount;

    private StubTranslationServer(StubTranslationScenario scenario)
    {
        Scenario = scenario;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Endpoint = new Uri($"http://127.0.0.1:{port}/chat/completions");
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    public StubTranslationScenario Scenario { get; }

    public Uri Endpoint { get; }

    public int RequestCount => Volatile.Read(ref _requestCount);

    public static StubTranslationServer Start(
        StubTranslationScenario scenario = StubTranslationScenario.Success) => new(scenario);

    public void ReleaseTimeout() => _timeoutRelease.TrySetResult();

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _connections.Add(HandleConnectionAsync(client, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using NetworkStream stream = client.GetStream();
                byte[] requestBody = await ReadRequestBodyAsync(stream, cancellationToken).ConfigureAwait(false);
                ValidateChatCompletionShape(requestBody);
                Interlocked.Increment(ref _requestCount);

                if (Scenario == StubTranslationScenario.Timeout)
                {
                    await _timeoutRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                await WriteScenarioResponseAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or SocketException or OperationCanceledException or JsonException or InvalidDataException)
            {
                // The client may cancel or close while exercising a failure mode. The stub retains
                // no request content and shutdown remains deterministic.
            }
        }
    }

    private async Task WriteScenarioResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        switch (Scenario)
        {
            case StubTranslationScenario.Success:
            case StubTranslationScenario.Timeout:
                await WriteResponseAsync(
                    stream,
                    HttpStatusCode.OK,
                    "{\"id\":\"local-stub-1\",\"choices\":[{\"message\":{\"content\":\"本地确定性翻译。\"}}]}",
                    cancellationToken).ConfigureAwait(false);
                break;
            case StubTranslationScenario.Unauthorized:
                await WriteResponseAsync(stream, HttpStatusCode.Unauthorized, "", cancellationToken).ConfigureAwait(false);
                break;
            case StubTranslationScenario.Forbidden:
                await WriteResponseAsync(stream, HttpStatusCode.Forbidden, "", cancellationToken).ConfigureAwait(false);
                break;
            case StubTranslationScenario.RateLimited:
                await WriteResponseAsync(stream, HttpStatusCode.TooManyRequests, "", cancellationToken).ConfigureAwait(false);
                break;
            case StubTranslationScenario.RequestTimeout:
                await WriteResponseAsync(stream, HttpStatusCode.RequestTimeout, "", cancellationToken).ConfigureAwait(false);
                break;
            case StubTranslationScenario.ServerError:
                await WriteResponseAsync(stream, HttpStatusCode.ServiceUnavailable, "", cancellationToken).ConfigureAwait(false);
                break;
            case StubTranslationScenario.MalformedResponse:
                await WriteResponseAsync(stream, HttpStatusCode.OK, "{not-json", cancellationToken).ConfigureAwait(false);
                break;
            case StubTranslationScenario.EmptyResponse:
                await WriteResponseAsync(stream, HttpStatusCode.OK, "", cancellationToken).ConfigureAwait(false);
                break;
            case StubTranslationScenario.OversizedResponse:
                await WriteResponseAsync(
                    stream,
                    HttpStatusCode.OK,
                    new string('x', OversizedProviderResponseBytes),
                    cancellationToken).ConfigureAwait(false);
                break;
            case StubTranslationScenario.Redirect:
                await WriteResponseAsync(
                    stream,
                    HttpStatusCode.Redirect,
                    "",
                    cancellationToken,
                    "Location: https://redirect.invalid/chat/completions\r\n").ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unsupported stub scenario: {Scenario}.");
        }
    }

    private static async Task<byte[]> ReadRequestBodyAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        List<byte> header = new(1024);
        byte[] oneByte = new byte[1];
        int matched = 0;
        byte[] terminator = "\r\n\r\n"u8.ToArray();
        while (matched < terminator.Length)
        {
            int read = await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("Request headers ended unexpectedly.");
            }

            byte value = oneByte[0];
            header.Add(value);
            if (header.Count > 16 * 1024)
            {
                throw new InvalidDataException("Request headers exceeded the stub limit.");
            }

            matched = value == terminator[matched]
                ? matched + 1
                : value == terminator[0] ? 1 : 0;
        }

        string headerText = Encoding.ASCII.GetString([.. header]);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].StartsWith("POST /chat/completions HTTP/1.1", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stub accepts only chat-completion POST requests.");
        }

        int contentLength = 0;
        foreach (string line in lines.Skip(1))
        {
            int separator = line.IndexOf(':');
            if (separator > 0 &&
                line[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(line[(separator + 1)..].Trim(), CultureInfo.InvariantCulture);
            }
        }

        if (contentLength <= 0 || contentLength > MaximumRequestBytes)
        {
            throw new InvalidDataException("Request body length is outside the stub limit.");
        }

        byte[] body = new byte[contentLength];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return body;
    }

    private static void ValidateChatCompletionShape(byte[] requestBody)
    {
        using JsonDocument document = JsonDocument.Parse(requestBody);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("model", out JsonElement model) ||
            string.IsNullOrWhiteSpace(model.GetString()) ||
            !root.TryGetProperty("messages", out JsonElement messages) ||
            messages.ValueKind != JsonValueKind.Array ||
            messages.GetArrayLength() < 2)
        {
            throw new InvalidDataException("Request is not a chat-completion payload.");
        }
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        HttpStatusCode statusCode,
        string body,
        CancellationToken cancellationToken,
        string extraHeaders = "")
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string reason = statusCode switch
        {
            HttpStatusCode.OK => "OK",
            HttpStatusCode.Redirect => "Found",
            HttpStatusCode.Unauthorized => "Unauthorized",
            HttpStatusCode.Forbidden => "Forbidden",
            HttpStatusCode.RequestTimeout => "Request Timeout",
            HttpStatusCode.TooManyRequests => "Too Many Requests",
            HttpStatusCode.ServiceUnavailable => "Service Unavailable",
            _ => "Stub Response",
        };
        byte[] headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)statusCode} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            extraHeaders +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _timeoutRelease.TrySetCanceled(_shutdown.Token);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
            await Task.WhenAll(_connections.ToArray()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }
}

[TestClass]
[DoNotParallelize]
public sealed class StubTranslationServerTests
{
    [TestMethod]
    public async Task Success_ProvidesDeterministicChatCompletionResponse()
    {
        await using StubTranslationServer server = StubTranslationServer.Start();
        using HttpClient client = CreateClient();
        ChatCompletionTranslationProvider provider = CreateProvider(server, client, TimeSpan.FromSeconds(2));

        TranslationResult result = await provider.TranslateAsync(CreateRequest(), CancellationToken.None);

        Assert.AreEqual("本地确定性翻译。", result.TranslatedText);
        Assert.AreEqual("local-stub-1", result.ProviderRequestId);
        Assert.AreEqual(1, server.RequestCount);
    }

    [TestMethod]
    [DataRow(StubTranslationScenario.Unauthorized, TranslationErrorCode.Authentication)]
    [DataRow(StubTranslationScenario.Forbidden, TranslationErrorCode.Authentication)]
    [DataRow(StubTranslationScenario.RateLimited, TranslationErrorCode.RateLimited)]
    [DataRow(StubTranslationScenario.RequestTimeout, TranslationErrorCode.Unavailable)]
    [DataRow(StubTranslationScenario.ServerError, TranslationErrorCode.Unavailable)]
    [DataRow(StubTranslationScenario.MalformedResponse, TranslationErrorCode.InvalidResponse)]
    [DataRow(StubTranslationScenario.EmptyResponse, TranslationErrorCode.InvalidResponse)]
    [DataRow(StubTranslationScenario.OversizedResponse, TranslationErrorCode.InvalidResponse)]
    [DataRow(StubTranslationScenario.Redirect, TranslationErrorCode.InvalidResponse)]
    public async Task ConfiguredFailure_MapsWithoutRetry(
        StubTranslationScenario scenario,
        TranslationErrorCode expected)
    {
        await using StubTranslationServer server = StubTranslationServer.Start(scenario);
        using HttpClient client = CreateClient();
        ChatCompletionTranslationProvider provider = CreateProvider(server, client, TimeSpan.FromSeconds(2));

        TranslationProviderException exception =
            await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
                provider.TranslateAsync(CreateRequest(), CancellationToken.None));

        Assert.AreEqual(expected, exception.Code);
        Assert.AreEqual(1, server.RequestCount);
    }

    [TestMethod]
    public async Task Timeout_IsLocallyControlledAndMapsWithoutRetry()
    {
        await using StubTranslationServer server = StubTranslationServer.Start(StubTranslationScenario.Timeout);
        using HttpClient client = CreateClient();
        ChatCompletionTranslationProvider provider = CreateProvider(server, client, TimeSpan.FromMilliseconds(100));

        TranslationProviderException exception =
            await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
                provider.TranslateAsync(CreateRequest(TimeSpan.FromMilliseconds(100)), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Timeout, exception.Code);
        Assert.AreEqual(1, server.RequestCount);
    }

    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    });

    private static ChatCompletionTranslationProvider CreateProvider(
        StubTranslationServer server,
        HttpClient client,
        TimeSpan timeout)
    {
        ProviderSettings settings = ProviderSettings.Create(
            server.Endpoint,
            "local-stub-model",
            "TT_LOCAL_STUB_KEY",
            timeout,
            allowLoopbackHttp: true);
        return new ChatCompletionTranslationProvider(settings, client, _ => "local-test-credential");
    }

    private static TranslationRequest CreateRequest(TimeSpan? deadline = null) => new(
        1,
        1,
        "The local deterministic provider is ready.",
        "en",
        "zh-Hans",
        deadline ?? TimeSpan.FromSeconds(2));
}
