using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Privacy;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class TranslationProviderContractTests
{
    [TestMethod]
    public async Task TranslateAsync_MapsRequestAndResponseAndUsesCredentialHeader()
    {
        string? body = null;
        TestHttpMessageHandler handler = new(async (request, cancellationToken) =>
        {
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            Assert.AreEqual("test-secret", request.Headers.Authorization.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"req-1\",\"choices\":[{\"message\":{\"content\":\"\\u64cd\\u4f5c\\u5df2\\u5b8c\\u6210\\u3002\"}}]}", Encoding.UTF8, "application/json"),
            };
        });
        using HttpClient client = new(handler);
        ProviderSettings settings = ProviderSettings.Create(
            new Uri("https://provider.example/v1/chat/completions"), "test-model", "TT_TEST_KEY", TimeSpan.FromSeconds(2));
        ChatCompletionTranslationProvider provider = new(settings, client, name => name == "TT_TEST_KEY" ? "test-secret" : null);

        TranslationResult result = await provider.TranslateAsync(
            new TranslationRequest(1, 2, "Operation completed.", "en", "zh-Hans", TimeSpan.FromSeconds(2)), CancellationToken.None);

        Assert.AreEqual("\u64cd\u4f5c\u5df2\u5b8c\u6210\u3002", result.TranslatedText);
        Assert.AreEqual("req-1", result.ProviderRequestId);
        StringAssert.Contains(body!, "Operation completed.");
        StringAssert.Contains(body!, "test-model");
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task TranslateAsync_PropagatesCancellationWithoutSendingWhenAlreadyCanceled()
    {
        TestHttpMessageHandler handler = new((_, _) => throw new AssertFailedException("HTTP must not be called."));
        using HttpClient client = new(handler);
        ProviderSettings settings = ProviderSettings.Create(new Uri("https://provider.example/v1/chat/completions"), "model", "KEY", TimeSpan.FromSeconds(1));
        ChatCompletionTranslationProvider provider = new(settings, client, _ => "secret");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => provider.TranslateAsync(
            new TranslationRequest(1, 1, "Useful English output.", "en", "zh-Hans", TimeSpan.FromSeconds(1)), cancellation.Token));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task TranslateAsync_DecodesRawUtf8ChineseResponse()
    {
        byte[] responseBytes = Encoding.UTF8.GetBytes(
            "{\"id\":\"req-utf8\",\"choices\":[{\"message\":{\"content\":\"构建失败，因为缺少必需的配置文件。\"}}]}");
        TestHttpMessageHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(responseBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        }));
        using HttpClient client = new(handler);
        ProviderSettings settings = ProviderSettings.Create(
            new Uri("https://provider.example/v1/chat/completions"), "model", "KEY", TimeSpan.FromSeconds(2));
        ChatCompletionTranslationProvider provider = new(settings, client, _ => "secret");

        TranslationResult result = await provider.TranslateAsync(
            new TranslationRequest(1, 1, "The build failed.", "en", "zh-Hans", TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        Assert.AreEqual("构建失败，因为缺少必需的配置文件。", result.TranslatedText);
    }

    [TestMethod]
    public async Task TranslateAsync_MapsResponseBodyDeadlineToTimeout()
    {
        TestHttpMessageHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new DelayedContent(
                Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"迟到的响应\"}}]}"),
                TimeSpan.FromMilliseconds(300)),
        }));
        using HttpClient client = new(handler);
        ProviderSettings settings = ProviderSettings.Create(
            new Uri("https://provider.example/v1/chat/completions"), "model", "KEY", TimeSpan.FromMilliseconds(100));
        ChatCompletionTranslationProvider provider = new(settings, client, _ => "secret");

        TranslationProviderException exception = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(
                new TranslationRequest(1, 1, "The build failed.", "en", "zh-Hans", TimeSpan.FromMilliseconds(100)),
                CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Timeout, exception.Code);
    }

    [TestMethod]
    public async Task CoordinatorCanSwapAdaptersWithoutChangingCoreContract()
    {
        ITranslationProvider first = new StubProvider("?");
        ITranslationProvider second = new StubProvider("?");
        TranslationRequest request = new(1, 1, "One useful sentence.", "en", "zh-Hans", TimeSpan.FromSeconds(1));
        Assert.AreEqual("?", (await first.TranslateAsync(request, CancellationToken.None)).TranslatedText);
        Assert.AreEqual("?", (await second.TranslateAsync(request, CancellationToken.None)).TranslatedText);
    }

    [TestMethod]
    public async Task TranslateAsync_SecondSecretGatePreventsHttpSerialization()
    {
        TestHttpMessageHandler handler = new((_, _) =>
            throw new AssertFailedException("Secret-bearing text must not reach HTTP."));
        using HttpClient client = new(handler);
        ProviderSettings settings = ProviderSettings.Create(
            new Uri("https://provider.example/chat/completions"),
            "model",
            "KEY",
            TimeSpan.FromSeconds(1));
        ChatCompletionTranslationProvider provider = new(
            settings,
            client,
            _ => "credential",
            new SecretDetector(),
            _ => true);

        TranslationProviderException exception = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(
                new TranslationRequest(
                    1,
                    1,
                    "API_KEY=sk-test-1234567890abcdefghijklmnop",
                    "en",
                    "zh-Hans",
                    TimeSpan.FromSeconds(1)),
                CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Canceled, exception.Code);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task TranslateAsync_UnauthorizedGenerationPreventsHttpSerialization()
    {
        TestHttpMessageHandler handler = new((_, _) =>
            throw new AssertFailedException("Unauthorized generation must not reach HTTP."));
        using HttpClient client = new(handler);
        ProviderSettings settings = ProviderSettings.Create(
            new Uri("https://provider.example/chat/completions"),
            "model",
            "KEY",
            TimeSpan.FromSeconds(1));
        ChatCompletionTranslationProvider provider = new(
            settings,
            client,
            _ => "credential",
            new SecretDetector(),
            _ => false);

        TranslationProviderException exception = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(
                new TranslationRequest(
                    4,
                    1,
                    "The operation completed successfully.",
                    "en",
                    "zh-Hans",
                    TimeSpan.FromSeconds(1)),
                CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Canceled, exception.Code);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    private sealed class StubProvider(string result) : ITranslationProvider
    {
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new TranslationResult(result));
    }

    private sealed class DelayedContent(byte[] bytes, TimeSpan delay) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) => SerializeAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }

        private async Task SerializeAsync(Stream stream, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
        }
    }
}
