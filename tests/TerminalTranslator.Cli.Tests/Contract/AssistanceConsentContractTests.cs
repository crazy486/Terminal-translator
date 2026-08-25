using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Privacy;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class AssistanceConsentContractTests
{
    [TestMethod]
    public async Task QuestionOnly_DisclosesExactScopeWithoutContent_AndPersistsReuse()
    {
        await InTemporaryStore(async store =>
        {
            string secretQuestion = "Why? password=synthetic-secret-value";
            StringWriter firstOutput = new();
            AssistanceConsentPrompt first = Prompt(store, new StringReader("yes\n"), firstOutput);
            AssistanceRequest request = AssistanceRequest.CreateQuestionOnly(secretQuestion, ResponseLanguagePolicy.Select(secretQuestion));

            AssistanceAuthorizationDecision blocked = await first.AuthorizeAsync(request, CancellationToken.None);
            Assert.AreEqual(AssistanceAuthorizationFailure.SuspectedSecret, blocked.Failure);
            StringAssert.Contains(firstOutput.ToString(), "explicit one-time question");
            StringAssert.Contains(firstOutput.ToString(), "No terminal Capture");
            Assert.IsFalse(firstOutput.ToString().Contains(secretQuestion, StringComparison.Ordinal));

            StringWriter reuseOutput = new();
            AssistanceConsentPrompt reuse = Prompt(store, new StringReader(string.Empty), reuseOutput);
            AssistanceRequest safe = AssistanceRequest.CreateQuestionOnly("Why did the build fail?", ResponseLanguagePolicy.Select("Why did the build fail?"));
            AssistanceAuthorizationDecision authorized = await reuse.AuthorizeAsync(safe, CancellationToken.None);
            Assert.AreEqual(AssistanceAuthorizationFailure.None, authorized.Failure);
            Assert.AreEqual(string.Empty, reuseOutput.ToString(), "An unchanged matching grant must be reused without prompting.");
        });
    }

    [TestMethod]
    public async Task ContextualScope_IsDistinctExplicitAndCapturePermissionIsNotProviderConsent()
    {
        await InTemporaryStore(async store =>
        {
            AssistanceRequest question = AssistanceRequest.CreateQuestionOnly("safe question", ResponseLanguagePolicy.Select("safe question"));
            await Prompt(store, new StringReader("yes\n"), new StringWriter()).AuthorizeAsync(question, CancellationToken.None);

            StringWriter contextualOutput = new();
            AssistanceRequest contextual = AssistanceRequest.CreateQuestionWithPreviousCommand(
                "why?", ResponseLanguagePolicy.Select("why?"), "dotnet build", "Build failed", new TerminationFacts(1, false));
            AssistanceAuthorizationDecision declined = await Prompt(
                store, new StringReader("no\n"), contextualOutput).AuthorizeAsync(contextual, CancellationToken.None);

            Assert.AreEqual(AssistanceAuthorizationFailure.ConsentMissingOrDeclined, declined.Failure);
            string disclosure = contextualOutput.ToString();
            StringAssert.Contains(disclosure, "strict previous command text");
            StringAssert.Contains(disclosure, "selected captured output");
            StringAssert.Contains(disclosure, "exit/interruption metadata");
            StringAssert.Contains(disclosure, "not complete terminal history");
            StringAssert.Contains(disclosure, "Local Capture permission is separate");
            Assert.IsFalse(disclosure.Contains("dotnet build", StringComparison.Ordinal));
            Assert.IsFalse(disclosure.Contains("Build failed", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public async Task ChangedTrustConfiguration_RequiresConsentAgain()
    {
        await InTemporaryStore(async store =>
        {
            AssistanceRequest request = AssistanceRequest.CreateQuestionOnly("safe question", ResponseLanguagePolicy.Select("safe question"));
            await Prompt(store, new StringReader("yes\n"), new StringWriter()).AuthorizeAsync(request, CancellationToken.None);
            StringWriter changedOutput = new();
            AssistanceConsentPrompt changed = Prompt(store, new StringReader("no\n"), changedOutput,
                ProviderSettings.Create(new Uri("https://other.example/v1/chat"), "model", "TT_KEY", TimeSpan.FromSeconds(1)));

            AssistanceAuthorizationDecision result = await changed.AuthorizeAsync(request, CancellationToken.None);
            Assert.AreEqual(AssistanceAuthorizationFailure.ConsentMissingOrDeclined, result.Failure);
            StringAssert.Contains(changedOutput.ToString(), "other.example");
        });
    }

    private static AssistanceConsentPrompt Prompt(
        ProviderConsentGrantStore store, TextReader input, TextWriter output, ProviderSettings? settings = null) =>
        new(settings ?? ProviderSettings.Create(new Uri("https://provider.example/v1/chat"), "model", "TT_KEY", TimeSpan.FromSeconds(1)),
            store, new AssistancePrivacyGate(new SecretDetector()), input, output);

    private static async Task InTemporaryStore(Func<ProviderConsentGrantStore, Task> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tt-consent-contract-{Guid.NewGuid():N}");
        try { await test(new ProviderConsentGrantStore(directory)); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
