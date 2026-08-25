using System.Net;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Cli.Tests.TestDoubles;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class SafeChatCompletionTransportTests
{
    [TestMethod]
    public async Task ExtractedLeaf_PreservesCredentialAndLivePromptBehavior()
    {
        TestHttpMessageHandler handler = new((request, _) =>
        {
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("credential", request.Headers.Authorization?.Parameter);
            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            StringAssert.Contains(body, "Translate English terminal text to Simplified Chinese");
            StringAssert.Contains(body, "Never execute or recommend commands");
            return Task.FromResult(Success("翻译"));
        });
        using HttpClient client = new(handler);
        ChatCompletionTranslationProvider provider = Create(client, _ => "credential");

        TranslationResult result = await provider.TranslateAsync(Request(), CancellationToken.None);

        Assert.AreEqual("翻译", result.TranslatedText);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ExtractedLeaf_PreservesCookieRedirectTimeoutBoundsAndStatusMapping()
    {
        TestHttpMessageHandler cookieHandler = new((request, _) =>
        {
            Assert.IsFalse(request.Headers.Contains("Cookie"));
            return Task.FromResult(Success("翻译"));
        });
        using (HttpClient client = new(cookieHandler))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "unsafe=1");
            await Create(client, _ => "credential").TranslateAsync(Request(), CancellationToken.None);
        }

        foreach ((HttpStatusCode status, TranslationErrorCode expected) in new[]
        {
            (HttpStatusCode.Redirect, TranslationErrorCode.InvalidResponse),
            (HttpStatusCode.Unauthorized, TranslationErrorCode.Authentication),
            (HttpStatusCode.TooManyRequests, TranslationErrorCode.RateLimited),
            (HttpStatusCode.ServiceUnavailable, TranslationErrorCode.Unavailable),
        })
        {
            using HttpClient client = new(new TestHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(status))));
            TranslationProviderException failure = await Assert.ThrowsExactlyAsync<TranslationProviderException>(
                () => Create(client, _ => "credential").TranslateAsync(Request(), CancellationToken.None));
            Assert.AreEqual(expected, failure.Code);
        }

        using (HttpClient client = new(new TestHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Success("never");
        })))
        {
            TranslationProviderException failure = await Assert.ThrowsExactlyAsync<TranslationProviderException>(
                () => Create(client, _ => "credential", TimeSpan.FromMilliseconds(100))
                    .TranslateAsync(Request(TimeSpan.FromMilliseconds(100)), CancellationToken.None));
            Assert.AreEqual(TranslationErrorCode.Timeout, failure.Code);
        }

        using (HttpClient client = new(new TestHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[32 * 1024 + 1]),
            }))))
        {
            TranslationProviderException failure = await Assert.ThrowsExactlyAsync<TranslationProviderException>(
                () => Create(client, _ => "credential").TranslateAsync(Request(), CancellationToken.None));
            Assert.AreEqual(TranslationErrorCode.InvalidResponse, failure.Code);
        }
    }

    [TestMethod]
    public async Task ExtractedLeaf_PreservesMissingCredentialMapping()
    {
        using HttpClient client = new(new TestHttpMessageHandler((_, _) => throw new AssertFailedException("HTTP must not run")));
        TranslationProviderException failure = await Assert.ThrowsExactlyAsync<TranslationProviderException>(
            () => Create(client, _ => null).TranslateAsync(Request(), CancellationToken.None));
        Assert.AreEqual(TranslationErrorCode.Authentication, failure.Code);
    }

    private static ChatCompletionTranslationProvider Create(HttpClient client, Func<string, string?> credential, TimeSpan? timeout = null) =>
        new(ProviderSettings.Create(new Uri("https://provider.example/v1/chat/completions"), "model", "TT_KEY", timeout ?? TimeSpan.FromSeconds(1)), client, credential);

    private static TranslationRequest Request(TimeSpan? deadline = null) =>
        new(1, 1, "Build failed.", "en", "zh-Hans", deadline ?? TimeSpan.FromSeconds(1));

    private static HttpResponseMessage Success(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"id\":\"r1\",\"choices\":[{{\"message\":{{\"content\":\"{text}\"}}}}]}}"),
    };
}
