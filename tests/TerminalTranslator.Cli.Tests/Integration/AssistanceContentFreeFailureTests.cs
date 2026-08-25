using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class AssistanceContentFreeFailureTests
{
    private const string Content = "question command output token=synthetic-secret-value";

    [TestMethod]
    public async Task SecretAndConsentFailures_RenderOnlySafeCategories()
    {
        AskAssistanceOutcome secret = await new AskAssistanceCoordinator(
            new RejectIfCalledAssistanceProvider(), new ApprovedAssistanceRequestAuthorizer())
            .ExecuteAsync(Content, CancellationToken.None);
        AssertContentFree(Render(secret));

        AskAssistanceOutcome declined = await new AskAssistanceCoordinator(
            new RejectIfCalledAssistanceProvider(), new DenyingAuthorizer())
            .ExecuteAsync(Content, CancellationToken.None);
        AssertContentFree(Render(declined));
    }

    [TestMethod]
    public async Task ProviderTimeoutHttpAndParseFailures_DoNotRenderRequestOrUnsafeProviderDetails()
    {
        foreach (TranslationErrorCode code in new[]
        {
            TranslationErrorCode.Timeout,
            TranslationErrorCode.Unavailable,
            TranslationErrorCode.InvalidResponse,
        })
        {
            AskAssistanceOutcome outcome = await new AskAssistanceCoordinator(
                new ThrowingProvider(code), new ApprovedAssistanceRequestAuthorizer())
                .ExecuteAsync($"safe question {Content.Replace("token=", "marker-")}", CancellationToken.None);
            AssertContentFree(Render(outcome));
        }
    }

    [TestMethod]
    public void CaptureUnavailableAndCorruptFailures_DoNotRenderCapturedFieldsOrFakeTranslation()
    {
        foreach (AssistanceFailureKind failure in new[]
        {
            AssistanceFailureKind.CaptureUnavailable,
            AssistanceFailureKind.UnreliableOrCorrupt,
        })
        {
            StringWriter output = new();
            new InlineAssistanceRenderer(output).Render(new LastAssistanceOutcome(failure));
            string rendered = output.ToString();
            AssertContentFree(rendered);
            Assert.IsFalse(rendered.Contains("[翻译]", StringComparison.Ordinal));
            Assert.IsFalse(rendered.Contains("[建议]", StringComparison.Ordinal));
        }
    }

    private static string Render(AskAssistanceOutcome outcome)
    {
        StringWriter output = new();
        new InlineAssistanceRenderer(output).RenderAnswer(outcome);
        return output.ToString();
    }

    private static void AssertContentFree(string rendered)
    {
        Assert.IsFalse(rendered.Contains(Content, StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("synthetic-secret-value", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("unsafe provider body", StringComparison.Ordinal));
    }

    private sealed class DenyingAuthorizer : IAssistanceRequestAuthorizer
    {
        public Task<AssistanceAuthorizationDecision> AuthorizeAsync(AssistanceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(AssistanceAuthorizationDecision.Denied());
    }

    private sealed class ThrowingProvider(TranslationErrorCode code) : IAssistanceProvider
    {
        public Task<AssistanceResult> CompleteAsync(AuthorizedAssistanceRequest request, CancellationToken cancellationToken) =>
            throw new TranslationProviderException(code, new InvalidDataException($"unsafe provider body {Content}"));
    }
}
