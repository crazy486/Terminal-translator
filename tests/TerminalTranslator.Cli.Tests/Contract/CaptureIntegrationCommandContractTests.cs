using TerminalTranslator.Cli.Commands;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class CaptureIntegrationCommandContractTests
{
    [TestMethod]
    public async Task HiddenBridge_IsInternalAndForwardsMaintenanceArguments()
    {
        IReadOnlyList<string>? observed = null;
        System.CommandLine.Command command = CaptureIntegrationCommand.Create((arguments, _) =>
        {
            observed = arguments;
            return Task.FromResult(0);
        });

        Assert.IsTrue(command.Hidden);
        Assert.AreEqual(0, await command.Parse(["boundary", "sequence=7"]).InvokeAsync());
        CollectionAssert.AreEqual(new[] { "boundary", "sequence=7" }, observed!.ToArray());
    }
}
