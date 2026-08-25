using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Core.Assistance;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class LastCommandContractTests
{
    [TestMethod]
    public void Root_RegistersOnlyCanonicalLastGrammar()
    {
        var root = CommandFactory.CreateRootCommand();

        Assert.IsNotNull(root.Subcommands.SingleOrDefault(command => command.Name == "last"));
        Assert.IsFalse(root.Subcommands.Any(command => command.Name is "translate-last" or "last-output"));
        Assert.IsTrue(root.Parse(["last"]).Errors.Count == 0);
        Assert.IsTrue(root.Parse(["last", "unexpected"]).Errors.Count > 0);
        Assert.IsTrue(root.Parse(["--last"]).Errors.Count > 0);
    }

    [TestMethod]
    public async Task Last_ComposesThroughControlledAssistanceSeamAndRendersInline()
    {
        using StringWriter output = new();
        int calls = 0;
        var command = LastCommand.Create(_ =>
        {
            calls++;
            AssistanceRequest request = AssistanceRequest.CreateLastTranslation(
                "build", "Build failed.", new TerminationFacts(1, false));
            return Task.FromResult(new LastAssistanceOutcome(
                AssistanceFailureKind.None,
                request,
                Result: new AssistanceResult("翻译", "建议")));
        }, output);

        int exitCode = await command.Parse([]).InvokeAsync();

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, calls);
        StringAssert.Contains(output.ToString(), "[翻译]");
        StringAssert.Contains(output.ToString(), "[建议]");
    }
}
