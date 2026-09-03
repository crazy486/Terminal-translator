using System.Net;
using System.Text.Json;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class OnDemandProviderContractTests
{
    [TestMethod]
    public async Task StructuredProductionRequestProfile_SerializesNonThinkingJsonOutputContract()
    {
        List<string> bodies = [];
        using HttpClient client = new(new TestHttpMessageHandler(async (request, _) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return bodies.Count == 1
                ? Success("translated", "recommendation")
                : SuccessAnswer("answer");
        }));
        ChatCompletionAssistanceProvider provider = Create(client);

        await provider.CompleteAsync(
            Authorize(AssistanceRequest.CreateLastTranslation(
                "winget install Contoso.Tools",
                "The package installation completed successfully.",
                new TerminationFacts(0, false))),
            CancellationToken.None);
        await provider.CompleteAsync(
            Authorize(AssistanceRequest.CreateQuestionOnly(
                "What happened?",
                ResponseLanguagePolicy.Select("What happened?"))),
            CancellationToken.None);
        await provider.CompleteAsync(
            Authorize(AssistanceRequest.CreateQuestionWithPreviousCommand(
                "What happened?",
                ResponseLanguagePolicy.Select("What happened?"),
                "winget install Contoso.Tools",
                "The package installation failed.",
                new TerminationFacts(1, false))),
            CancellationToken.None);

        Assert.HasCount(3, bodies);
        foreach (string body in bodies)
        {
            using JsonDocument serialized = JsonDocument.Parse(body);
            JsonElement root = serialized.RootElement;
            Assert.AreEqual("model", root.GetProperty("model").GetString());
            Assert.AreEqual(0d, root.GetProperty("temperature").GetDouble());
            JsonElement.ArrayEnumerator messages = root.GetProperty("messages").EnumerateArray();
            Assert.AreEqual(2, messages.Count());
            Assert.AreEqual("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.AreEqual("user", root.GetProperty("messages")[1].GetProperty("role").GetString());
            Assert.AreEqual("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
            Assert.AreEqual("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
            foreach (string omitted in new[]
            {
                "reasoning_effort", "top_p", "max_tokens", "stream", "stop",
                "presence_penalty", "frequency_penalty", "tools", "tool_choice", "n", "seed", "logprobs",
            })
            {
                Assert.IsFalse(root.TryGetProperty(omitted, out _), $"{omitted} must remain omitted in the production profile.");
            }
            Assert.AreEqual(5, root.EnumerateObject().Count());
        }
    }

    [TestMethod]
    public async Task QuestionOnly_IsStatelessQuestionScopedBoundedAndSuggestedCommandsRemainInert()
    {
        List<string> sent = [];
        using HttpClient client = new(new TestHttpMessageHandler(async (request, _) =>
        {
            sent.Add(await request.Content!.ReadAsStringAsync());
            return SuccessAnswer("运行 git status 只是一段建议文本。");
        }));
        ChatCompletionAssistanceProvider provider = Create(client);

        AssistanceResult first = await provider.CompleteAsync(
            Authorize(AssistanceRequest.CreateQuestionOnly(
                "What is detached HEAD?",
                ResponseLanguagePolicy.Select("What is detached HEAD?"))), CancellationToken.None);
        AssistanceResult second = await provider.CompleteAsync(
            Authorize(AssistanceRequest.CreateQuestionOnly(
                "Please answer in English: how do I leave it?",
                ResponseLanguagePolicy.Select("Please answer in English: how do I leave it?"))), CancellationToken.None);

        Assert.AreEqual(2, sent.Count);
        StringAssert.Contains(sent[0], "What is detached HEAD?");
        StringAssert.Contains(sent[0], "Simplified Chinese");
        Assert.IsFalse(sent[0].Contains("Previous command", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(sent[1].Contains("What is detached HEAD?", StringComparison.Ordinal));
        Assert.IsFalse(sent[0].Contains("conversation", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("运行 git status 只是一段建议文本。", first.Answer);
        Assert.AreEqual(string.Empty, first.Translation);
        Assert.IsFalse(typeof(AssistanceResult).GetMethods().Any(method =>
            method.Name.Contains("Execute", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("StartProcess", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual("English", ResponseLanguagePolicy.Select("Please answer in English: how do I leave it?").Language);
        Assert.IsFalse(string.IsNullOrWhiteSpace(second.Answer));
    }

    [TestMethod]
    public async Task ContextualQuestion_UsesQuestionLanguageAndPreviousCommandShape()
    {
        string? sent = null;
        using HttpClient client = new(new TestHttpMessageHandler(async (request, _) =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return SuccessAnswer("The build failed because ERR42 was reported.");
        }));
        AssistanceRequest request = AssistanceRequest.CreateQuestionWithPreviousCommand(
            "Please answer in English: what caused this?",
            ResponseLanguagePolicy.Select("Please answer in English: what caused this?"),
            "dotnet build",
            "stderr ERR42",
            new TerminationFacts(1, false));

        AssistanceResult result = await Create(client).CompleteAsync(
            Authorize(request), CancellationToken.None);

        StringAssert.Contains(sent!, "Please answer in English");
        StringAssert.Contains(sent!, "Response language instruction");
        StringAssert.Contains(sent!, "dotnet build");
        StringAssert.Contains(sent!, "stderr ERR42");
        StringAssert.Contains(sent!, "using only the supplied reliable previous-command context");
        Assert.AreEqual("The build failed because ERR42 was reported.", result.Answer);
    }

    [TestMethod]
    public async Task QuestionOnly_NormalizesMalformedAndOversizedAnswers()
    {
        using HttpClient malformedClient = new(new TestHttpMessageHandler((_, _) => Task.FromResult(
            Success("not-an-answer-shape", "still-not-an-answer"))));
        AssistanceRequest request = AssistanceRequest.CreateQuestionOnly(
            "why?", ResponseLanguagePolicy.Select("why?"));

        TranslationProviderException malformed = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            Create(malformedClient).CompleteAsync(
                Authorize(request), CancellationToken.None));

        using HttpClient oversizedClient = new(new TestHttpMessageHandler((_, _) => Task.FromResult(
            SuccessAnswer(new string('a', (16 * 1024) + 1)))));
        TranslationProviderException oversized = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            Create(oversizedClient).CompleteAsync(
                Authorize(request), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.InvalidResponse, malformed.Code);
        Assert.AreEqual(TranslationErrorCode.InvalidResponse, oversized.Code);
    }
    [TestMethod]
    public async Task LastTranslation_UsesCommandOnlyAsContextAndCarriesOrderedOutputAndTerminationFacts()
    {
        string? sent = null;
        TestHttpMessageHandler handler = new(async (request, _) =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return Success("已保留 C:\\src\\app.cs、--no-restore、ERR42 和 包.mod。", "运行 dotnet build --no-restore 重试。");
        });
        using HttpClient client = new(handler);
        ChatCompletionAssistanceProvider provider = Create(client);
        AssistanceRequest request = AssistanceRequest.CreateLastTranslation(
            "dotnet build --no-restore",
            "first stdout\nthen stderr ERR42 at C:\\src\\app.cs 包.mod",
            new TerminationFacts(1, true),
            LocalCaptureCompleteness.Complete,
            61,
            AiInputCompleteness.Complete);

        AssistanceResult result = await provider.CompleteAsync(
            Authorize(request), CancellationToken.None);

        Assert.IsNotNull(sent);
        StringAssert.Contains(sent, "Do not translate the command text");
        StringAssert.Contains(sent, "dotnet build --no-restore");
        StringAssert.Contains(sent, "first stdout\\nthen stderr ERR42");
        StringAssert.Contains(sent, "interrupted=true");
        StringAssert.Contains(sent, "Simplified Chinese");
        StringAssert.Contains(sent, "preserve existing Chinese");
        StringAssert.Contains(sent, "paths, URLs, flags/options, error codes, identifiers");
        Assert.AreEqual("运行 dotnet build --no-restore 重试。", result.Recommendation);
    }

    [TestMethod]
    public async Task Adapter_RejectsInvalidAuthorizationAndCapacityBeforeHttp()
    {
        using HttpClient client = new(new TestHttpMessageHandler((_, _) => throw new AssertFailedException("HTTP must not run")));
        ChatCompletionAssistanceProvider provider = Create(client);
        AssistanceRequest normal = AssistanceRequest.CreateLastTranslation("echo", "Failed", new TerminationFacts(1, false));

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            provider.CompleteAsync(null!, CancellationToken.None));

        AssistanceRequest oversized = AssistanceRequest.CreateLastTranslation("echo", new string('a', 8193), new TerminationFacts(1, false));
        TranslationProviderException capacity = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            provider.CompleteAsync(Authorize(oversized), CancellationToken.None));
        Assert.AreEqual(TranslationErrorCode.InvalidResponse, capacity.Code);
    }

    [TestMethod]
    public async Task LastTranslationContract_ExplicitlyPreservesChineseAndTechnicalTokensWithoutTranslatingCommand()
    {
        string? body = null;
        using HttpClient client = new(new TestHttpMessageHandler(async (request, _) =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Success("已有中文；错误在 C:\\src\\a.cs，代码 ERR42。", "检查 package.module 配置。");
        }));
        AssistanceRequest request = AssistanceRequest.CreateLastTranslation(
            "Remove-Item C:\\src\\a.cs",
            "已有中文; Error ERR42 in C:\\src\\a.cs package.module --force",
            new TerminationFacts(1, false));

        AssistanceResult result = await Create(client).CompleteAsync(
            Authorize(request), CancellationToken.None);

        StringAssert.Contains(body!, "Do not translate the command text");
        StringAssert.Contains(body!, "preserve existing Chinese");
        StringAssert.Contains(result.Translation, "C:\\src\\a.cs");
        StringAssert.Contains(result.Translation, "ERR42");
    }

    private static ChatCompletionAssistanceProvider Create(HttpClient client) => new(
        ProviderSettings.Create(new Uri("https://provider.example/chat/completions"), "model", "TT_KEY", TimeSpan.FromSeconds(1)),
        client,
        _ => "credential");

    private static AuthorizedAssistanceRequest Authorize(AssistanceRequest request)
    {
        string scope = AssistanceConsentScopes.For(request.Kind);
        MatchingConsentAssertion consent = MatchingConsentAssertion.TryCreate("TEST-GRANT", "TEST-GRANT", scope)!;
        return new AssistancePrivacyGate(new TerminalTranslator.Core.Privacy.SecretDetector())
            .Authorize(request, consent)!;
    }

    private static HttpResponseMessage Success(string translation, string recommendation)
    {
        string content = JsonSerializer.Serialize(new { translation, recommendation });
        string response = JsonSerializer.Serialize(new
        {
            id = "assist-1",
            choices = new[] { new { message = new { content } } },
        });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
    }

    private static HttpResponseMessage SuccessAnswer(string answer)
    {
        string content = JsonSerializer.Serialize(new { answer });
        string response = JsonSerializer.Serialize(new
        {
            id = "assist-answer-1",
            choices = new[] { new { message = new { content } } },
        });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
    }
}
