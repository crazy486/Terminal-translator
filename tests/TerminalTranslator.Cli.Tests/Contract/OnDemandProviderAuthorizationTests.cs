using System.Net;
using System.Text.Json;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Privacy;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class OnDemandProviderAuthorizationTests
{
    [TestMethod]
    public async Task MissingAuthorization_CannotSerializeResolveCredentialOrInvokeHttp()
    {
        int serializationCalls = 0;
        int credentialCalls = 0;
        int httpCalls = 0;
        using HttpClient client = new(new TestHttpMessageHandler((_, _) =>
        {
            httpCalls++;
            throw new AssertFailedException("HTTP must not be called.");
        }));
        ChatCompletionAssistanceProvider provider = new(Settings(), client, _ =>
        {
            credentialCalls++;
            return "credential";
        }, request =>
        {
            serializationCalls++;
            return AssistanceRequestVariableInputSerializer.Serialize(request);
        });

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => provider.CompleteAsync(null!, CancellationToken.None));

        Assert.AreEqual(0, serializationCalls);
        Assert.AreEqual(0, credentialCalls);
        Assert.AreEqual(0, httpCalls);
        Assert.IsFalse(typeof(AuthorizedAssistanceRequest).GetConstructors().Any(),
            "Production callers must not be able to directly construct an authorization assertion.");
    }

    [TestMethod]
    public void MissingConsentOrPrivacyPass_CannotProduceAdapterInput()
    {
        AssistanceRequest request = AssistanceRequest.CreateQuestionOnly("safe question", ResponseLanguagePolicy.Select("safe question"));
        AssistancePrivacyGate gate = new(new SecretDetector());

        Assert.IsNull(gate.Authorize(request, null));
        Assert.IsNull(gate.Authorize(request,
            MatchingConsentAssertion.TryCreate("expected", "missing", AssistanceConsentScopes.QuestionOnly)));

        AssistanceRequest secret = AssistanceRequest.CreateQuestionOnly(
            "token=synthetic-secret-value", ResponseLanguagePolicy.Select("token=synthetic-secret-value"));
        MatchingConsentAssertion consent = MatchingConsentAssertion.TryCreate(
            "grant", "grant", AssistanceConsentScopes.QuestionOnly)!;
        Assert.IsNull(gate.Authorize(secret, consent));
    }

    [TestMethod]
    public async Task FullyAuthorizedRequest_ResolvesCredentialAndSerializesOnlyAfterAssertionValidation()
    {
        int serializationCalls = 0;
        int credentialCalls = 0;
        int httpCalls = 0;
        using HttpClient client = new(new TestHttpMessageHandler((_, _) =>
        {
            httpCalls++;
            string content = JsonSerializer.Serialize(new { answer = "safe answer" });
            string body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }));
        ChatCompletionAssistanceProvider provider = new(Settings(), client, _ =>
        {
            credentialCalls++;
            return "credential";
        }, request =>
        {
            serializationCalls++;
            return AssistanceRequestVariableInputSerializer.Serialize(request);
        });
        AssistanceRequest request = AssistanceRequest.CreateQuestionOnly("safe question", ResponseLanguagePolicy.Select("safe question"));
        MatchingConsentAssertion consent = MatchingConsentAssertion.TryCreate("grant", "grant", AssistanceConsentScopes.QuestionOnly)!;
        AuthorizedAssistanceRequest authorized = new AssistancePrivacyGate(new SecretDetector()).Authorize(request, consent)!;

        AssistanceResult result = await provider.CompleteAsync(authorized, CancellationToken.None);

        Assert.AreEqual("safe answer", result.Answer);
        Assert.AreEqual(1, serializationCalls);
        Assert.AreEqual(1, credentialCalls);
        Assert.AreEqual(1, httpCalls);
    }

    private static ProviderSettings Settings() => ProviderSettings.Create(
        new Uri("https://provider.example/v1/chat"), "model", "TT_AUTH_TEST_KEY", TimeSpan.FromSeconds(1));
}
