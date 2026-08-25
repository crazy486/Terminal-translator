using System.Text;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class LongAndMixedOutputAcceptanceTests
{
    private static readonly CaptureSessionId Session = new(Guid.Parse("13131313-1313-1313-1313-131313131313"), "nonce");

    [TestMethod]
    public async Task ChineseOnlyMakesZeroCallsAndMixedUsesWholeContext()
    {
        RecordingProvider provider = new();
        LastAssistanceOutcome chinese = await Coordinator("构建已完成，没有错误。", provider).ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(AssistanceFailureKind.NoTranslatableEnglish, chinese.Failure);
        Assert.AreEqual(0, provider.Requests.Count);

        const string mixed = "已有中文。The build failed because configuration is missing. C:\\src\\app.cs";
        LastAssistanceOutcome result = await Coordinator(mixed, provider).ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(AssistanceFailureKind.None, result.Failure);
        Assert.AreEqual(mixed, provider.Requests.Single().Request.SelectedOutput);
        Assert.AreEqual(AiInputCompleteness.Complete, provider.Requests.Single().Request.AiCompleteness);
    }

    [TestMethod]
    public async Task UnderBudgetAiHeadTailLocalHeadTailAndBothRemainDistinct()
    {
        const string longEnglish = "The build failed because configuration is missing.\n";
        string output = string.Concat(Enumerable.Repeat(longEnglish, 40));
        RecordingProvider aiProvider = new();
        AssistanceRequest basis = AssistanceRequest.CreateLastTranslation("build", output, new TerminationFacts(1, false));
        int required = AssistanceRequestVariableInputSerializer.GetRequiredBytes(basis);
        LastAssistanceOutcome ai = await Coordinator(output, aiProvider,
            budget: new AiInputBudgetPolicy(required + 100)).ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(AiInputCompleteness.AiHeadTail, aiProvider.Requests.Single().Request.AiCompleteness);
        Assert.AreEqual(LocalCaptureCompleteness.Complete, aiProvider.Requests.Single().Request.LocalCompleteness);

        RecordingProvider localProvider = new();
        LastAssistanceOutcome local = await Coordinator("The retained head and tail show a failed build.", localProvider,
            local: LocalCaptureCompleteness.LocalHeadTail, originalBytes: 20_000_000).ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(LocalCaptureCompleteness.LocalHeadTail, localProvider.Requests.Single().Request.LocalCompleteness);
        Assert.AreEqual(AiInputCompleteness.Complete, localProvider.Requests.Single().Request.AiCompleteness);

        RecordingProvider bothProvider = new();
        LastAssistanceOutcome both = await Coordinator(output, bothProvider,
            local: LocalCaptureCompleteness.LocalHeadTail, originalBytes: 20_000_000,
            budget: new AiInputBudgetPolicy(required + 100)).ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(LocalCaptureCompleteness.LocalHeadTail, bothProvider.Requests.Single().Request.LocalCompleteness);
        Assert.AreEqual(AiInputCompleteness.AiHeadTail, bothProvider.Requests.Single().Request.AiCompleteness);
        Assert.AreEqual(AssistanceFailureKind.None, ai.Failure);
        Assert.AreEqual(AssistanceFailureKind.None, local.Failure);
        Assert.AreEqual(AssistanceFailureKind.None, both.Failure);
    }

    private static LastAssistanceCoordinator Coordinator(
        string output,
        RecordingProvider provider,
        LocalCaptureCompleteness local = LocalCaptureCompleteness.Complete,
        long? originalBytes = null,
        AiInputBudgetPolicy? budget = null) => new(
        () => PreviousCommandResult.Success(new PreviousCommandSnapshot(
            Session, 1, "build", output, 1, false, local,
            originalBytes ?? Encoding.UTF8.GetByteCount(output))),
        provider,
        new ApprovedAssistanceRequestAuthorizer(),
        budget);

    private sealed class RecordingProvider : IAssistanceProvider
    {
        public List<AuthorizedAssistanceRequest> Requests { get; } = [];
        public Task<AssistanceResult> CompleteAsync(AuthorizedAssistanceRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new AssistanceResult("摘要翻译", "建议"));
        }
    }
}
