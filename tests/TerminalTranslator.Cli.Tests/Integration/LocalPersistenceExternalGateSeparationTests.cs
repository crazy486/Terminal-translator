using System.Text;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Privacy;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class LocalPersistenceExternalGateSeparationTests
{
    [TestMethod]
    public async Task SensitiveTerminalContent_IsRetainedRawLocally_ButBlockedFromEveryExternalScope()
    {
        const string secret = "token=synthetic-secret-value";
        string directory = Path.Combine(Path.GetTempPath(), $"tt-local-external-{Guid.NewGuid():N}");
        try
        {
            RetainedCaptureStore store = new(directory);
            RetainedGeneration generation = await store.PublishAsync(
                [new RetainedRecordWrite("record-1", 1, Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("metadata"))]);
            string retained = Encoding.UTF8.GetString(await store.ReadContentAsync(generation.Records.Single()));
            Assert.AreEqual(secret, retained, "Local capture must not gain pre-persistence secret filtering.");

            AssistanceRequest[] requests =
            [
                AssistanceRequest.CreateQuestionOnly(secret, ResponseLanguagePolicy.Select(secret)),
                AssistanceRequest.CreateLastTranslation(secret, "Build failed", new TerminationFacts(1, false)),
                AssistanceRequest.CreateQuestionWithPreviousCommand("why?", ResponseLanguagePolicy.Select("why?"), "dotnet build", secret, new TerminationFacts(1, false)),
            ];
            AssistancePrivacyGate gate = new(new SecretDetector());
            foreach (AssistanceRequest request in requests)
            {
                string scope = AssistanceConsentScopes.For(request.Kind);
                MatchingConsentAssertion consent = MatchingConsentAssertion.TryCreate("grant", "grant", scope)!;
                Assert.IsNull(gate.Authorize(request, consent));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
