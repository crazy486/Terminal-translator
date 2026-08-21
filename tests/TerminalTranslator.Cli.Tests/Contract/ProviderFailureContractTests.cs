using System.Net;
using System.Text;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class ProviderFailureContractTests
{
    private const int MaximumResponseBytes = 32 * 1024;

    [TestMethod]
    public async Task Timeout_MapsToTimeout_WithoutRetry()
    {
        TestHttpMessageHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertFailedException("The canceled request must not complete normally.");
        });
        using HttpClient client = new(handler);
        ChatCompletionTranslationProvider provider = CreateProvider(client, TimeSpan.FromMilliseconds(100));

        TranslationProviderException exception = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(CreateRequest(TimeSpan.FromMilliseconds(100)), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Timeout, exception.Code);
        Assert.AreEqual(1, handler.Requests.Count, "Timed-out requests must not be retried.");
    }

    [TestMethod]
    public async Task CallerCancellation_StopsDelivery_WithoutRetry()
    {
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestHttpMessageHandler handler = new(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertFailedException("The canceled request must not complete normally.");
        });
        using HttpClient client = new(handler);
        ChatCompletionTranslationProvider provider = CreateProvider(client, TimeSpan.FromSeconds(5));
        using CancellationTokenSource cancellation = new();

        Task<TranslationResult> translation = provider.TranslateAsync(
            CreateRequest(TimeSpan.FromSeconds(5)), cancellation.Token);
        await requestStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => translation);
        Assert.AreEqual(1, handler.Requests.Count, "Canceled requests must not be retried.");
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task AuthenticationStatus_MapsToAuthentication(HttpStatusCode statusCode)
    {
        await AssertStatusMappingAsync(statusCode, TranslationErrorCode.Authentication);
    }

    [TestMethod]
    public async Task TooManyRequests_MapsToRateLimited()
    {
        await AssertStatusMappingAsync(HttpStatusCode.TooManyRequests, TranslationErrorCode.RateLimited);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.RequestTimeout)]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.BadGateway)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    public async Task UnavailableStatus_MapsToUnavailable(HttpStatusCode statusCode)
    {
        await AssertStatusMappingAsync(statusCode, TranslationErrorCode.Unavailable);
    }

    [TestMethod]
    public async Task ConnectionFailure_MapsToUnavailable_WithoutRetry()
    {
        TestHttpMessageHandler handler = new((_, _) =>
            throw new HttpRequestException("Synthetic local connection failure."));
        using HttpClient client = new(handler);
        ChatCompletionTranslationProvider provider = CreateProvider(client);

        TranslationProviderException exception = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(CreateRequest(), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Unavailable, exception.Code);
        Assert.AreEqual(1, handler.Requests.Count, "Connection failures must not be retried.");
    }

    [TestMethod]
    public async Task ResponseBodyConnectionFailure_MapsToUnavailable_WithoutRetry()
    {
        TestHttpMessageHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new FailingContent(new IOException("Synthetic response stream failure.")),
        }));
        using HttpClient client = new(handler);
        ChatCompletionTranslationProvider provider = CreateProvider(client);

        TranslationProviderException exception = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(CreateRequest(), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Unavailable, exception.Code);
        Assert.AreEqual(1, handler.Requests.Count, "Response stream failures must not be retried.");
    }

    [TestMethod]
    public async Task MalformedJson_MapsToInvalidResponse_WithoutRetry()
    {
        await AssertInvalidResponseAsync(new StringContent("{not-json", Encoding.UTF8, "application/json"));
    }

    [TestMethod]
    public async Task EmptyResponseBody_MapsToInvalidResponse_WithoutRetry()
    {
        await AssertInvalidResponseAsync(new ByteArrayContent([]));
    }

    [TestMethod]
    public async Task EmptyTranslation_MapsToInvalidResponse_WithoutRetry()
    {
        await AssertInvalidResponseAsync(JsonContent("{\"choices\":[{\"message\":{\"content\":\"   \"}}]}"));
    }

    [TestMethod]
    public async Task OversizedResponse_IsRejectedBeforeBodyIsBuffered_WithoutRetry()
    {
        GuardedOversizedContent content = new(MaximumResponseBytes + 1);
        TestHttpMessageHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        }));
        using HttpClient client = new(handler);
        ChatCompletionTranslationProvider provider = CreateProvider(client);

        TranslationProviderException exception = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(CreateRequest(), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.InvalidResponse, exception.Code);
        Assert.IsFalse(content.ReadAttempted, "A declared oversized response must be rejected before buffering its body.");
        Assert.AreEqual(1, handler.Requests.Count, "Oversized responses must not be retried.");
    }

    [TestMethod]
    public async Task CrossAuthorityRedirect_IsRejected_WithoutFollowingOrRetrying()
    {
        TestHttpMessageHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://other-provider.example/v1/chat/completions");
            return Task.FromResult(response);
        });
        using HttpClient client = new(handler);
        ChatCompletionTranslationProvider provider = CreateProvider(client);

        TranslationProviderException exception = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(CreateRequest(), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.InvalidResponse, exception.Code);
        Assert.AreEqual(1, handler.Requests.Count, "Redirect responses must not be followed or retried.");
        Assert.AreEqual("provider.example", handler.Requests[0].RequestUri!.Host);
    }

    [TestMethod]
    public async Task Provider_DoesNotSendOrReplayCookies()
    {
        bool cookieObserved = false;
        int requestCount = 0;
        TestHttpMessageHandler handler = new((request, _) =>
        {
            cookieObserved |= request.Headers.Contains("Cookie");
            HttpResponseMessage response = SuccessfulResponse();
            if (requestCount++ == 0)
            {
                response.Headers.TryAddWithoutValidation("Set-Cookie", "provider-session=synthetic; Secure; HttpOnly");
            }

            return Task.FromResult(response);
        });
        using HttpClient client = new(handler);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "caller-cookie=must-not-be-sent");
        ChatCompletionTranslationProvider provider = CreateProvider(client);

        await provider.TranslateAsync(CreateRequest(), CancellationToken.None);
        await provider.TranslateAsync(CreateRequest(sequence: 2), CancellationToken.None);

        Assert.IsFalse(cookieObserved, "Provider requests must not send caller cookies or replay response cookies.");
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ServerFailure_IsAttemptedExactlyOnce()
    {
        TestHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using HttpClient client = new(handler);
        ChatCompletionTranslationProvider provider = CreateProvider(client);

        TranslationProviderException exception = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(CreateRequest(), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Unavailable, exception.Code);
        Assert.AreEqual(1, handler.Requests.Count, "Phase one must not automatically retry provider failures.");
    }

    private static async Task AssertStatusMappingAsync(
        HttpStatusCode statusCode,
        TranslationErrorCode expectedCode)
    {
        TestHttpMessageHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)));
        using HttpClient client = new(handler);
        ChatCompletionTranslationProvider provider = CreateProvider(client);

        TranslationProviderException exception = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(CreateRequest(), CancellationToken.None));

        Assert.AreEqual(expectedCode, exception.Code);
        Assert.AreEqual(1, handler.Requests.Count, $"HTTP {(int)statusCode} must not be retried.");
    }

    private static async Task AssertInvalidResponseAsync(HttpContent content)
    {
        TestHttpMessageHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        }));
        using HttpClient client = new(handler);
        ChatCompletionTranslationProvider provider = CreateProvider(client);

        TranslationProviderException exception = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(CreateRequest(), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.InvalidResponse, exception.Code);
        Assert.AreEqual(1, handler.Requests.Count, "Invalid responses must not be retried.");
    }

    private static ChatCompletionTranslationProvider CreateProvider(
        HttpClient client,
        TimeSpan? timeout = null)
    {
        ProviderSettings settings = ProviderSettings.Create(
            new Uri("https://provider.example/v1/chat/completions"),
            "test-model",
            "TT_PROVIDER_FAILURE_TEST_KEY",
            timeout ?? TimeSpan.FromSeconds(2));
        return new ChatCompletionTranslationProvider(settings, client, _ => "synthetic-credential");
    }

    private static TranslationRequest CreateRequest(TimeSpan? deadline = null, ulong sequence = 1) =>
        new(1, sequence, "The build failed because configuration is missing.", "en", "zh-Hans", deadline ?? TimeSpan.FromSeconds(2));

    private static HttpContent JsonContent(string json) =>
        new StringContent(json, Encoding.UTF8, "application/json");

    private static HttpResponseMessage SuccessfulResponse() => new(HttpStatusCode.OK)
    {
        Content = JsonContent("{\"choices\":[{\"message\":{\"content\":\"构建失败。\"}}]}"),
    };

    private sealed class FailingContent(Exception exception) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(exception);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class GuardedOversizedContent(long declaredLength) : HttpContent
    {
        public bool ReadAttempted { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempted = true;
            return Task.FromException(new InvalidOperationException("Oversized content must not be read."));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }
    }
}
