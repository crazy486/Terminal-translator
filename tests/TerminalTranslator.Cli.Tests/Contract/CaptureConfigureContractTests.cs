using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class CaptureConfigureContractTests
{
    [TestMethod]
    [DataRow("enabled", CapturePreference.Enabled)]
    [DataRow("disabled", CapturePreference.Disabled)]
    public async Task CaptureOnly_IsIndependentAndDisclosesPurpose(string value, CapturePreference expected)
    {
        using TemporaryDirectory temporary = new();
        CapturePreference? observed = null;
        using StringWriter output = new();
        using StringWriter error = new();

        int result = await ConfigureCommand.Create(
            new ProviderSettingsStore(temporary.Path), output, error,
            new CapturePreferenceStore(temporary.Path),
            (preference, _) => { observed = preference; return Task.CompletedTask; })
            .Parse(["--capture", value]).InvokeAsync();

        Assert.AreEqual(0, result, error.ToString());
        Assert.AreEqual(expected, observed);
        StringAssert.Contains(output.ToString(), "local command output");
        StringAssert.Contains(output.ToString(), "current PowerShell session");
        Assert.IsFalse(File.Exists(Path.Combine(temporary.Path, "provider-settings.json")));
    }

    [TestMethod]
    public async Task MixedCaptureAndProviderMutation_IsRejectedWithoutEitherWrite()
    {
        using TemporaryDirectory temporary = new();
        int captureCalls = 0;
        using StringWriter error = new();
        int result = await ConfigureCommand.Create(
            new ProviderSettingsStore(temporary.Path), new StringWriter(), error,
            new CapturePreferenceStore(temporary.Path),
            (_, _) => { captureCalls++; return Task.CompletedTask; })
            .Parse(["--capture", "enabled", "--endpoint", "https://provider.example", "--model", "m", "--api-key-env", "KEY"])
            .InvokeAsync();

        Assert.AreEqual(4, result);
        Assert.AreEqual(0, captureCalls);
        Assert.IsFalse(File.Exists(Path.Combine(temporary.Path, "provider-settings.json")));
        StringAssert.Contains(error.ToString(), "cannot be combined");
    }

    [TestMethod]
    public async Task ProviderOnly_RemainsAllOrNone()
    {
        using TemporaryDirectory temporary = new();
        ProviderSettingsStore store = new(temporary.Path);
        using StringWriter error = new();
        var command = ConfigureCommand.Create(store, new StringWriter(), error,
            new CapturePreferenceStore(temporary.Path), (_, _) => throw new AssertFailedException("capture must not run"));

        Assert.AreEqual(4, await command.Parse(["--endpoint", "https://provider.example"]).InvokeAsync());
        Assert.IsFalse(File.Exists(store.SettingsPath));

        Assert.AreEqual(0, await command.Parse([
            "--endpoint", "https://provider.example", "--model", "model", "--api-key-env", "KEY"])
            .InvokeAsync());
        Assert.IsNotNull(await store.LoadAsync());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-configure-capture-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
