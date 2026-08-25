using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Core.Assistance;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class AskCommandContractTests
{
    [TestMethod]
    public async Task Ask_PreservesQuotedMultiTokenAndUnicodeQuestions()
    {
        List<string> questions = [];
        var command = AskCommand.Create(
            (question, _) =>
            {
                questions.Add(question);
                return Task.FromResult(AskAssistanceOutcome.Success(
                    AssistanceRequest.CreateQuestionOnly(question, ResponseLanguagePolicy.Select(question)),
                    AssistanceResult.CreateAnswer("answer")));
            },
            output: new StringWriter());

        Assert.AreEqual(0, await command.Parse(["why does this fail"]).InvokeAsync());
        Assert.AreEqual(0, await command.Parse(["为什么", "git status", "失败？"]).InvokeAsync());

        CollectionAssert.AreEqual(
            new[] { "why does this fail", "为什么 git status 失败？" },
            questions);
    }

    [TestMethod]
    public void Ask_RequiresQuestionAndReservesLastAsSubcommand()
    {
        var command = AskCommand.Create();
        var root = CommandFactory.CreateRootCommand();

        Assert.IsGreaterThan(0, command.Parse([]).Errors.Count);
        Assert.IsGreaterThan(0, command.Parse([""]).Errors.Count);
        Assert.IsGreaterThan(0, command.Parse(["last"]).Errors.Count);
        Assert.AreEqual(0, command.Parse(["last", "what happened?"]).Errors.Count);
        Assert.AreEqual(0, command.Parse(["last", "刚才", "发生了什么？"]).Errors.Count);
        Assert.IsNotNull(root.Subcommands.SingleOrDefault(candidate => candidate.Name == "ask"));
        Assert.AreEqual(0, root.Parse(["ask", "why?"]).Errors.Count);
        Assert.AreEqual(0, root.Parse(["ask", "last", "why?"]).Errors.Count);
    }
}
