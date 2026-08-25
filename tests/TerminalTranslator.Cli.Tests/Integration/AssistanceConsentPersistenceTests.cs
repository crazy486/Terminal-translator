using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Privacy;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class AssistanceConsentPersistenceTests
{
    [TestMethod]
    public async Task ExactTrustBoundaryReusesGrant_AndEveryMaterialChangeReprompts()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tt-reconsent-{Guid.NewGuid():N}");
        try
        {
            ProviderConsentGrantStore store = new(directory);
            ProviderSettings baseline = Settings("https://provider.example/v1/chat", "model-a", "TT_KEY", 1);
            AssistanceRequest question = AssistanceRequest.CreateQuestionOnly("safe question", ResponseLanguagePolicy.Select("safe question"));
            await Authorize(store, baseline, question, "yes\n");

            Assert.AreEqual(string.Empty, (await Authorize(store,
                Settings("https://provider.example/v1/chat", "model-a", "TT_KEY", 9), question, string.Empty)).Output,
                "Timeout and local rendering preferences do not affect the trust fingerprint.");

            (ProviderSettings Settings, AssistanceRequest Request, string Policy)[] changed =
            [
                (baseline with { Adapter = "other-adapter" }, question, ProviderConsentContext.CurrentPolicyVersion),
                (Settings("https://other.example/v1/chat", "model-a", "TT_KEY", 1), question, ProviderConsentContext.CurrentPolicyVersion),
                (Settings("https://provider.example/v1/chat", "model-b", "TT_KEY", 1), question, ProviderConsentContext.CurrentPolicyVersion),
                (Settings("https://provider.example/v1/chat", "model-a", "OTHER_KEY", 1), question, ProviderConsentContext.CurrentPolicyVersion),
                (baseline, AssistanceRequest.CreateLastTranslation("dotnet build", "Build failed", new TerminationFacts(1, false)), ProviderConsentContext.CurrentPolicyVersion),
                (baseline, question, "policy-v2"),
            ];

            foreach ((ProviderSettings settings, AssistanceRequest request, string policy) in changed)
            {
                (AssistanceAuthorizationDecision Decision, string Output) result =
                    await Authorize(store, settings, request, "no\n", policy);
                Assert.AreEqual(AssistanceAuthorizationFailure.ConsentMissingOrDeclined, result.Decision.Failure);
                StringAssert.Contains(result.Output, "Allow this external transmission scope");
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(AssistanceAuthorizationDecision Decision, string Output)> Authorize(
        ProviderConsentGrantStore store, ProviderSettings settings, AssistanceRequest request, string answer,
        string policy = ProviderConsentContext.CurrentPolicyVersion)
    {
        StringWriter output = new();
        AssistanceConsentPrompt prompt = new(settings, store, new AssistancePrivacyGate(new SecretDetector()),
            new StringReader(answer), output, policy);
        return (await prompt.AuthorizeAsync(request, CancellationToken.None), output.ToString());
    }

    private static ProviderSettings Settings(string endpoint, string model, string key, int timeout) =>
        ProviderSettings.Create(new Uri(endpoint), model, key, TimeSpan.FromSeconds(timeout));
}
