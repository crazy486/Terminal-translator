using System.Net;
using System.Text;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Cli.Tests.TestDoubles;
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
    public async Task CoordinatorCanSwapAdaptersWithoutChangingCoreContract()
    {
        ITranslationProvider first = new StubProvider("?");
        ITranslationProvider second = new StubProvider("?");
        TranslationRequest request = new(1, 1, "One useful sentence.", "en", "zh-Hans", TimeSpan.FromSeconds(1));
        Assert.AreEqual("?", (await first.TranslateAsync(request, CancellationToken.None)).TranslatedText);
        Assert.AreEqual("?", (await second.TranslateAsync(request, CancellationToken.None)).TranslatedText);
    }

    private sealed class StubProvider(string result) : ITranslationProvider
    {
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new TranslationResult(result));
    }
}
