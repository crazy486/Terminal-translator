using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class AssistanceSecretBlockingTests
{
    private const string Secret = "sk-AAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task Ask_SecretQuestion_StopsBeforeProvider()
    {
        RecordingAssistanceProvider provider = new();
        AskAssistanceOutcome outcome = await new AskAssistanceCoordinator(
            provider, new ApprovedAssistanceRequestAuthorizer()).ExecuteAsync(
                $"why {Secret}?", CancellationToken.None);

        Assert.AreEqual(AssistanceFailureKind.SuspectedSecret, outcome.Failure);
        Assert.AreEqual(0, provider.Requests.Count);
    }

    [TestMethod]
    public async Task Last_SecretInCommandOutputAndSelectedHeadTail_StopsBeforeProvider()
    {
        foreach (PreviousCommandSnapshot snapshot in new[]
        {
            Snapshot($"echo {Secret}", "Build failed safely"),
            Snapshot("dotnet build", $"Build failed: {Secret}"),
            Snapshot("dotnet build", Secret + "\n" + new string('m', 600) + "\nsafe tail"),
            Snapshot("dotnet build", "safe head\n" + new string('m', 600) + "\n" + Secret),
        })
        {
            RecordingAssistanceProvider provider = new();
            AssistanceRequest raw = AssistanceRequest.CreateLastTranslation(
                snapshot.CommandText, snapshot.Output, new TerminationFacts(snapshot.ExitCode, snapshot.WasInterrupted));
            int budget = AssistanceRequestVariableInputSerializer.GetRequiredBytes(raw) + 100;
            LastAssistanceOutcome outcome = await new LastAssistanceCoordinator(
                () => PreviousCommandResult.Success(snapshot), provider, new ApprovedAssistanceRequestAuthorizer(),
                new AiInputBudgetPolicy(budget), isEligible: _ => true).ExecuteAsync(CancellationToken.None);

            Assert.AreEqual(AssistanceFailureKind.SuspectedSecret, outcome.Failure);
            Assert.AreEqual(0, provider.Requests.Count);
        }
    }

    [TestMethod]
    public async Task AskLast_SecretInQuestionCommandOrOutput_StopsBeforeProvider()
    {
        (string Question, PreviousCommandSnapshot Snapshot)[] cases =
        [
            ($"why {Secret}?", Snapshot("dotnet build", "Build failed")),
            ("why?", Snapshot($"echo {Secret}", "Build failed")),
            ("why?", Snapshot("dotnet build", $"Build failed {Secret}")),
        ];
        foreach ((string question, PreviousCommandSnapshot snapshot) in cases)
        {
            RecordingAssistanceProvider provider = new();
            AskAssistanceOutcome outcome = await new AskLastAssistanceCoordinator(
                () => PreviousCommandResult.Success(snapshot), provider, new ApprovedAssistanceRequestAuthorizer())
                .ExecuteAsync(question, CancellationToken.None);
            Assert.AreEqual(AssistanceFailureKind.SuspectedSecret, outcome.Failure);
            Assert.AreEqual(0, provider.Requests.Count);
        }
    }

    private static PreviousCommandSnapshot Snapshot(string command, string output) => new(
        new CaptureSessionId(Guid.NewGuid(), "nonce"), 1, command, output, 1, false,
        LocalCaptureCompleteness.Complete, System.Text.Encoding.UTF8.GetByteCount(output));
}
