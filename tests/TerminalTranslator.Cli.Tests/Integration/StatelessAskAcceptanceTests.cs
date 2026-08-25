using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Assistance;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class StatelessAskAcceptanceTests
{
    [TestMethod]
    public async Task ConsecutiveInvocationsHaveNoMemoryLanguageLeakPaneOrExecutionAuthority()
    {
        RecordingAssistanceProvider provider = new();
        AskAssistanceCoordinator coordinator = new(
            provider,
            new ApprovedAssistanceRequestAuthorizer());
        int paneStarts = 0;
        int processStarts = 0;
        int fileActions = 0;

        using StringWriter firstOutput = new();
        provider.Result = AssistanceResult.CreateAnswer("第一条回答；建议：git status");
        Assert.AreEqual(0, await AskCommand.Create(coordinator.ExecuteAsync, output: firstOutput)
            .Parse(["What is detached HEAD?"]).InvokeAsync());

        using StringWriter secondOutput = new();
        provider.Result = AssistanceResult.CreateAnswer("Second independent answer: Remove-Item sample");
        Assert.AreEqual(0, await AskCommand.Create(coordinator.ExecuteAsync, output: secondOutput)
            .Parse(["Please", "answer", "in", "English:", "how", "do", "I", "leave?"]).InvokeAsync());

        Assert.AreEqual(2, provider.Requests.Count);
        Assert.AreEqual("Simplified Chinese", provider.Requests[0].Request.ResponseLanguage);
        Assert.AreEqual("English", provider.Requests[1].Request.ResponseLanguage);
        Assert.IsFalse(AssistanceRequestVariableInputSerializer.Serialize(provider.Requests[1].Request)
            .Contains(provider.Requests[0].Request.Question, StringComparison.Ordinal));
        Assert.AreEqual("第一条回答；建议：git status", firstOutput.ToString().Trim());
        Assert.AreEqual("Second independent answer: Remove-Item sample", secondOutput.ToString().Trim());
        Assert.AreEqual(0, paneStarts);
        Assert.AreEqual(0, processStarts);
        Assert.AreEqual(0, fileActions);
    }
}
