using System.Text;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Tests.TestDoubles;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Tests.Integration;

[TestClass]
public sealed class AskLastTruncationJourneyTests
{
    private static readonly CaptureSessionId Session = new(Guid.NewGuid(), "nonce");

    [TestMethod]
    public async Task LocalOnlyAiOnlyAndBothCompletenessStatesRemainIndependent()
    {
        AskAssistanceOutcome localOnly = await Execute(LocalCaptureCompleteness.LocalHeadTail, 8192);
        AskAssistanceOutcome aiOnly = await Execute(LocalCaptureCompleteness.Complete, RequiredBudget() + 40);
        AskAssistanceOutcome both = await Execute(LocalCaptureCompleteness.LocalHeadTail, RequiredBudget() + 40);

        Assert.AreEqual(LocalCaptureCompleteness.LocalHeadTail, localOnly.Request!.LocalCompleteness);
        Assert.AreEqual(AiInputCompleteness.Complete, localOnly.Request.AiCompleteness);
        Assert.AreEqual(LocalCaptureCompleteness.Complete, aiOnly.Request!.LocalCompleteness);
        Assert.AreEqual(AiInputCompleteness.AiHeadTail, aiOnly.Request.AiCompleteness);
        Assert.AreEqual(LocalCaptureCompleteness.LocalHeadTail, both.Request!.LocalCompleteness);
        Assert.AreEqual(AiInputCompleteness.AiHeadTail, both.Request.AiCompleteness);
    }

    [TestMethod]
    public async Task QuestionAndRequiredMetadataOverflowFailsClosedWithoutDroppingQuestion()
    {
        RecordingProvider provider = new();
        PreviousCommandSnapshot snapshot = Snapshot(LocalCaptureCompleteness.Complete);
        AssistanceRequest basis = AssistanceRequest.CreateQuestionWithPreviousCommand(
            new string('问', 200), ResponseLanguagePolicy.Select(new string('问', 200)),
            snapshot.CommandText, snapshot.Output, new TerminationFacts(1, false));
        int required = AssistanceRequestVariableInputSerializer.GetRequiredBytes(basis);
        AskLastAssistanceCoordinator coordinator = new(
            () => PreviousCommandResult.Success(snapshot), provider,
            new ApprovedAssistanceRequestAuthorizer(), new AiInputBudgetPolicy(required - 1));

        AskAssistanceOutcome outcome = await coordinator.ExecuteAsync(new string('问', 200), CancellationToken.None);

        Assert.AreEqual(AssistanceFailureKind.RequiredContextTooLarge, outcome.Failure);
        Assert.AreEqual(0, provider.Requests.Count);
    }

    private static async Task<AskAssistanceOutcome> Execute(LocalCaptureCompleteness local, int budget)
    {
        RecordingProvider provider = new();
        AskLastAssistanceCoordinator coordinator = new(
            () => PreviousCommandResult.Success(Snapshot(local)),
            provider,
            new ApprovedAssistanceRequestAuthorizer(),
            new AiInputBudgetPolicy(budget));
        return await coordinator.ExecuteAsync("为什么失败？", CancellationToken.None);
    }

    private static int RequiredBudget()
    {
        PreviousCommandSnapshot snapshot = Snapshot(LocalCaptureCompleteness.Complete);
        AssistanceRequest request = AssistanceRequest.CreateQuestionWithPreviousCommand(
            "为什么失败？", ResponseLanguagePolicy.Select("为什么失败？"), snapshot.CommandText,
            snapshot.Output, new TerminationFacts(1, false));
        return AssistanceRequestVariableInputSerializer.GetRequiredBytes(request);
    }

    private static PreviousCommandSnapshot Snapshot(LocalCaptureCompleteness local)
    {
        string output = string.Join('\n', Enumerable.Range(0, 200).Select(index => $"line {index:D3} failed"));
        return new(Session, 1, "dotnet build", output, 1, false, local, Encoding.UTF8.GetByteCount(output));
    }

    private sealed class RecordingProvider : IAssistanceProvider
    {
        public List<AuthorizedAssistanceRequest> Requests { get; } = [];
        public Task<AssistanceResult> CompleteAsync(AuthorizedAssistanceRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(AssistanceResult.CreateAnswer("answer"));
        }
    }
}
