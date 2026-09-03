using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Privacy;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class AssistancePrivacyGateTests
{
    private const string Secret = "sk-AAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public void ExactSelectedPayload_BlocksSecretInQuestionCommandAndOutput()
    {
        AssistanceRequest[] requests =
        [
            AssistanceRequest.CreateQuestionOnly($"why {Secret}?", ResponseLanguagePolicy.Select($"why {Secret}?")),
            AssistanceRequest.CreateLastTranslation($"echo {Secret}", "Build failed", new TerminationFacts(1, false)),
            AssistanceRequest.CreateLastTranslation("dotnet build", $"Build failed {Secret}", new TerminationFacts(1, false)),
            AssistanceRequest.CreateQuestionWithPreviousCommand(
                $"why {Secret}?", ResponseLanguagePolicy.Select($"why {Secret}?"), "dotnet build", "Build failed", new TerminationFacts(1, false)),
        ];

        foreach (AssistanceRequest request in requests)
            Assert.IsNull(Authorize(request), $"{request.Kind} must fail closed.");
    }

    [TestMethod]
    public void TextualTerminationMetadata_IsPartOfTheExactScreenedPayload()
    {
        SecretDetector detector = new(text => text.Contains("nativeExitCode=42", StringComparison.Ordinal)
            ? PrivacyReasonCode.CredentialAssignment
            : null);
        AssistanceRequest request = AssistanceRequest.CreateLastTranslation(
            "dotnet build", "Build failed", new TerminationFacts(42, false));

        Assert.IsNull(Authorize(request, detector));
    }

    [TestMethod]
    public void LocalHeadTailSelection_BlocksSecretsAtRetainedHeadAndTail()
    {
        foreach (string selected in new[] { $"{Secret}\nretained tail", $"retained head\n{Secret}" })
        {
            AssistanceRequest request = AssistanceRequest.CreateLastTranslation(
                "dotnet build", selected, new TerminationFacts(1, false),
                LocalCaptureCompleteness.LocalHeadTail, 10_180_100, AiInputCompleteness.Complete);
            Assert.IsNull(Authorize(request));
        }
    }

    [TestMethod]
    public void AiHeadTailSelection_IsBuiltBeforeGateAndBlocksSelectedHeadAndTail()
    {
        foreach (bool secretAtHead in new[] { true, false })
        {
            string output = secretAtHead
                ? Secret + "\n" + new string('m', 600) + "\nsafe-tail"
                : "safe-head\n" + new string('m', 600) + "\n" + Secret;
            AssistanceRequest raw = AssistanceRequest.CreateLastTranslation(
                "dotnet build", output, new TerminationFacts(1, false));
            int required = AssistanceRequestVariableInputSerializer.GetRequiredBytes(raw);
            AiSelectionResult selection = AiRequestSelector.Select(raw, new AiInputBudgetPolicy(required + 100));

            Assert.IsTrue(selection.Supported);
            Assert.AreEqual(AiInputCompleteness.AiHeadTail, selection.Request!.AiCompleteness);
            StringAssert.Contains(selection.Request.SelectedOutput, Secret);
            Assert.IsNull(Authorize(selection.Request));
        }
    }

    [TestMethod]
    public void MatchingConsentIsRequiredAndAuthorizationIsBoundToExactPayload()
    {
        AssistanceRequest request = AssistanceRequest.CreateQuestionOnly("safe question", ResponseLanguagePolicy.Select("safe question"));
        AssistancePrivacyGate gate = new(new SecretDetector());
        Assert.IsNull(gate.Authorize(request, null));
        Assert.IsNull(gate.Authorize(request, MatchingConsentAssertion.TryCreate("expected", "different", AssistanceConsentScopes.QuestionOnly)));

        AuthorizedAssistanceRequest authorized = Authorize(request)!;
        authorized.AssertValid();
        Assert.AreEqual(AssistanceConsentScopes.QuestionOnly, authorized.Scope);
    }

    private static AuthorizedAssistanceRequest? Authorize(AssistanceRequest request, SecretDetector? detector = null)
    {
        string scope = AssistanceConsentScopes.For(request.Kind);
        MatchingConsentAssertion consent = MatchingConsentAssertion.TryCreate("grant", "grant", scope)!;
        return new AssistancePrivacyGate(detector ?? new SecretDetector()).Authorize(request, consent);
    }
}
