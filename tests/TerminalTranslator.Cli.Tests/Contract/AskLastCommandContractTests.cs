using TerminalTranslator.Cli.Commands;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class AskLastCommandContractTests
{
    [TestMethod]
    public async Task AskLast_IsTheOnlyContextualGrammarAndPreservesQuestion()
    {
        string? observed = null;
        var command = AskCommand.Create(
            executeLast: (question, _) =>
            {
                observed = question;
                return Task.FromResult(TerminalTranslator.Core.Assistance.AskAssistanceOutcome.NoContext(
                    TerminalTranslator.Core.Assistance.AssistanceFailureKind.NoPreviousCommand));
            },
            output: new StringWriter());

        Assert.AreEqual(0, await command.Parse(["last", "解释", "detached HEAD 为什么出现？"]).InvokeAsync());
        Assert.AreEqual("解释 detached HEAD 为什么出现？", observed);
        Assert.IsGreaterThan(0, command.Parse(["last"]).Errors.Count);
        Assert.IsGreaterThan(0, command.Parse(["--last", "why"]).Errors.Count);
    }
}
