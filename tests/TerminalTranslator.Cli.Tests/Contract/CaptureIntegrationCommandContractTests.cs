using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.Capture;

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

    [TestMethod]
    public async Task BoundaryHistoryMismatch_ReturnsFixedContentFreeSubreason()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The production bridge owner proof is Windows-only.");
            return;
        }
        Assert.IsTrue(CaptureSessionIdentity.TryGetDirectParentOwner(out CaptureOwnerIdentity? owner));
        using TemporaryDirectory temporary = new();
        string settings = Path.Combine(temporary.Path, "settings");
        string captureRoot = Path.Combine(temporary.Path, "capture");
        CapturePreferenceStore preference = new(settings);
        await preference.SaveAsync(CapturePreference.Enabled);
        CaptureBootstrapResult bootstrap = await new CaptureSessionBootstrap(captureRoot).InitializeAsync(
            CapturePreference.Enabled,
            owner!,
            CaptureSessionBootstrap.CurrentIntegrationVersion,
            isHostedFeature001Session: false);
        await File.WriteAllTextAsync(bootstrap.StagingPath!, string.Empty);
        Dictionary<string, string> environment = new()
        {
            [CaptureSessionIdentity.SessionEnvironmentVariable] = bootstrap.Proof!.SessionId.ToString("N"),
            [CaptureSessionIdentity.NonceEnvironmentVariable] = bootstrap.Proof.Nonce,
        };
        StringWriter output = new();
        CaptureMaintenanceBridge bridge = new(
            preference,
            captureRoot,
            output,
            new StringWriter(),
            name => environment.GetValueOrDefault(name));

        int exitCode = await bridge.HandleAsync(
            [
                "boundary",
                $"version={CaptureSessionBootstrap.CurrentIntegrationVersion}",
                $"staging={bootstrap.StagingPath}",
                "opening-history-id=4",
                "history-id=6",
                $"command-base64={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Write-Output ambiguous"))}",
                "succeeded=True",
            ],
            CancellationToken.None);

        Assert.AreEqual(6, exitCode);
        CollectionAssert.AreEqual(
            new[] { "unavailable=boundary", "boundary-detail=historyidentitymismatch" },
            output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
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
