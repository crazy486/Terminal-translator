using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Configuration;

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

    [TestMethod]
    public async Task Initialize_WhenDirectOwnerValidationFails_ReturnsContentFreeDiagnosticCategory()
    {
        using TemporaryDirectory temporary = new();
        StringWriter output = new();
        StringWriter error = new();
        CaptureMaintenanceBridge bridge = new(
            new CapturePreferenceStore(temporary.Path),
            Path.Combine(temporary.Path, "capture"),
            output,
            error);

        int exitCode = await bridge.HandleAsync(
            [
                "initialize",
                "owner-pid=1",
                "owner-start=1",
                "owner-sid=S-1-5-21-1",
                "version=2.0",
            ],
            CancellationToken.None);

        Assert.AreEqual(5, exitCode);
        Assert.AreEqual("unavailable=ownervalidation", output.ToString().Trim());
        Assert.AreEqual(string.Empty, error.ToString());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-bridge-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
