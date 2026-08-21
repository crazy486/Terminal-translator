using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
[DoNotParallelize]
public sealed class CliContractTests
{
    [TestMethod]
    public async Task Configure_StoresAndDisclosesOnlyNonSensitivePreferences()
    {
        using TemporaryDirectory temporary = new();
        ProviderSettingsStore store = new(temporary.Path);
        using StringWriter output = new();
        using StringWriter error = new();
        const string secret = "must-never-appear-in-settings-or-output";
        string? previous = Environment.GetEnvironmentVariable("TT_US3_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("TT_US3_SECRET", secret);
            int exitCode = await ConfigureCommand.Create(store, output, error).Parse(
            [
                "--endpoint", "https://provider.example/chat/completions",
                "--model", "model-a",
                "--api-key-env", "TT_US3_SECRET",
                "--timeout-ms", "900",
            ]).InvokeAsync();

            Assert.AreEqual(0, exitCode, error.ToString());
            string settingsJson = await File.ReadAllTextAsync(store.SettingsPath);
            StringAssert.Contains(output.ToString(), "provider.example");
            StringAssert.Contains(output.ToString(), "TT_US3_SECRET");
            Assert.IsFalse(output.ToString().Contains(secret, StringComparison.Ordinal));
            Assert.IsFalse(settingsJson.Contains(secret, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TT_US3_SECRET", previous);
        }
    }

    [TestMethod]
    public async Task On_DeclineDisclosesScopeAndSendsNoControlRequest()
    {
        using TemporaryDirectory temporary = new();
        ProviderSettingsStore store = new(temporary.Path);
        await store.SaveAsync(ProviderSettings.Create(
            new Uri("https://provider.example/chat/completions"),
            "model-a",
            "TT_US3_KEY",
            TimeSpan.FromSeconds(1)));
        string? oldSession = Environment.GetEnvironmentVariable("TT_SESSION_ID");
        string? oldNonce = Environment.GetEnvironmentVariable("TT_SESSION_NONCE");
        string? oldKey = Environment.GetEnvironmentVariable("TT_US3_KEY");
        bool sent = false;
        try
        {
            Environment.SetEnvironmentVariable("TT_SESSION_ID", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("TT_SESSION_NONCE", new string('A', 64));
            Environment.SetEnvironmentVariable("TT_US3_KEY", "secret");
            using StringWriter output = new();
            int exitCode = await OnCommand.Create(
                store,
                new StringReader("n\n"),
                output,
                new StringWriter(),
                (_, _) => new RejectIfCalledSender(() => sent = true)).Parse([]).InvokeAsync();

            Assert.AreEqual(0, exitCode);
            Assert.IsFalse(sent);
            StringAssert.Contains(output.ToString(), "External destination: provider.example");
            StringAssert.Contains(output.ToString(), "eligible, non-sensitive English segments");
            StringAssert.Contains(output.ToString(), "not persisted");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TT_SESSION_ID", oldSession);
            Environment.SetEnvironmentVariable("TT_SESSION_NONCE", oldNonce);
            Environment.SetEnvironmentVariable("TT_US3_KEY", oldKey);
        }
    }

    [TestMethod]
    public async Task Off_IsIdempotentAndStatusIsContentFree()
    {
        string? oldSession = Environment.GetEnvironmentVariable("TT_SESSION_ID");
        string? oldNonce = Environment.GetEnvironmentVariable("TT_SESSION_NONCE");
        FakeControlClient client = new();
        try
        {
            Environment.SetEnvironmentVariable("TT_SESSION_ID", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("TT_SESSION_NONCE", new string('B', 64));
            using StringWriter offOutput = new();
            Assert.AreEqual(0, await OffCommand.Create(
                offOutput,
                new StringWriter(),
                (_, _) => client).Parse([]).InvokeAsync());
            Assert.AreEqual(0, await OffCommand.Create(
                offOutput,
                new StringWriter(),
                (_, _) => client).Parse([]).InvokeAsync());
            Assert.AreEqual(2, client.DisableCalls);
            StringAssert.Contains(offOutput.ToString(), "Translation disabled.");

            using StringWriter statusOutput = new();
            Assert.AreEqual(0, await StatusCommand.Create(
                statusOutput,
                new StringWriter(),
                (_, _) => client).Parse([]).InvokeAsync());
            StringAssert.Contains(statusOutput.ToString(), "session: active");
            StringAssert.Contains(statusOutput.ToString(), "translation: disabled");
            StringAssert.Contains(statusOutput.ToString(), "provider: provider.example, model-a");
            Assert.IsFalse(statusOutput.ToString().Contains("terminal source", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(statusOutput.ToString().Contains("secret", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TT_SESSION_ID", oldSession);
            Environment.SetEnvironmentVariable("TT_SESSION_NONCE", oldNonce);
        }
    }

    [TestMethod]
    public async Task OffAndStatus_OutsideSessionReturnExitCodeFive()
    {
        string? oldSession = Environment.GetEnvironmentVariable("TT_SESSION_ID");
        string? oldNonce = Environment.GetEnvironmentVariable("TT_SESSION_NONCE");
        try
        {
            Environment.SetEnvironmentVariable("TT_SESSION_ID", null);
            Environment.SetEnvironmentVariable("TT_SESSION_NONCE", null);
            Assert.AreEqual(5, await OffCommand.Create().Parse([]).InvokeAsync());
            Assert.AreEqual(5, await StatusCommand.Create().Parse([]).InvokeAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TT_SESSION_ID", oldSession);
            Environment.SetEnvironmentVariable("TT_SESSION_NONCE", oldNonce);
        }
    }

    [TestMethod]
    public async Task PublicCommands_RejectUnexpectedArgumentsWithUsageExitCode()
    {
        Assert.AreEqual(2, await CommandFactory.InvokeAsync(["off", "unexpected"]));
        Assert.AreEqual(2, await CommandFactory.InvokeAsync(["status", "--unknown"]));
    }

    private sealed class RejectIfCalledSender(Action called) : IEnableRequestSender
    {
        public Task<bool> SendAsync(ControlRequestMessage request, CancellationToken cancellationToken)
        {
            called();
            throw new AssertFailedException("Control request must not be sent after declined consent.");
        }
    }

    private sealed class FakeControlClient : IControlPipeClient
    {
        public int DisableCalls { get; private set; }

        public Task<ControlResultMessage> EnableAsync(
            string providerFingerprint,
            bool consent,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ControlResultMessage(
                "control-result", SessionProtocol.Version, "enable", true, "enabled", 1));

        public Task<ControlResultMessage> DisableAsync(CancellationToken cancellationToken)
        {
            DisableCalls++;
            return Task.FromResult(new ControlResultMessage(
                "control-result", SessionProtocol.Version, "disable", true, "disabled", 2));
        }

        public Task<StatusResultMessage> StatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StatusResultMessage(
                "status-result", SessionProtocol.Version, "disabled", "provider.example", "model-a", 0, 0, 3, 0));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-cli-contract-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
