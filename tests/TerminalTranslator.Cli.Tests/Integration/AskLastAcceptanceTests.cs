using System.Text;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class AskLastAcceptanceTests
{
    [TestMethod]
    public async Task SharedStrictRetrievalPreventsSelfPollutionAndRendersSuggestedCommandsOnly()
    {
        CaptureSessionId session = new(Guid.NewGuid(), "nonce");
        IReadOnlyList<CapturedCommand> records =
        [
            Command(session, 1, "git status", "HEAD detached at abc", false),
            Command(session, 2, "tt ask last explain", "old answer", true),
            Command(session, 3, "tt last", "old translation", true),
        ];
        PreviousCommandRetrievalState state = new(
            CapturePreference.Enabled, CaptureHealthState.Healthy, session, records, true);
        RecordingAssistanceProvider provider = new()
        {
            Result = AssistanceResult.CreateAnswer("原因如下。建议仅显示：git switch main"),
        };
        AskLastAssistanceCoordinator coordinator = new(
            () => PreviousCommandRetriever.Retrieve(state),
            provider,
            new ApprovedAssistanceRequestAuthorizer());
        int paneStarts = 0;
        int commandExecutions = 0;
        using StringWriter output = new();

        int first = await AskCommand.Create(executeLast: coordinator.ExecuteAsync, output: output)
            .Parse(["last", "刚才发生了什么？"]).InvokeAsync();
        int second = await AskCommand.Create(executeLast: coordinator.ExecuteAsync, output: output)
            .Parse(["last", "Please answer in English: explain again"]).InvokeAsync();

        Assert.AreEqual(0, first);
        Assert.AreEqual(0, second);
        Assert.AreEqual(2, provider.Requests.Count);
        Assert.IsTrue(provider.Requests.All(request => request.Request.CommandText == "git status"));
        Assert.IsTrue(provider.Requests.All(request => request.Request.SelectedOutput == "HEAD detached at abc"));
        Assert.AreEqual("Simplified Chinese", provider.Requests[0].Request.ResponseLanguage);
        Assert.AreEqual("English", provider.Requests[1].Request.ResponseLanguage);
        Assert.IsFalse(AssistanceRequestVariableInputSerializer.Serialize(provider.Requests[1].Request)
            .Contains(provider.Requests[0].Request.Question, StringComparison.Ordinal));
        StringAssert.Contains(output.ToString(), "git switch main");
        Assert.AreEqual(0, paneStarts);
        Assert.AreEqual(0, commandExecutions);
    }

    private static CapturedCommand Command(
        CaptureSessionId session,
        long sequence,
        string command,
        string output,
        bool contextual)
    {
        CommandBoundary boundary = new(session, sequence, command, true, 1, false);
        return new(session, sequence, command, output, boundary, LocalCaptureCompleteness.Complete,
            Encoding.UTF8.GetByteCount(output), contextual);
    }
}
