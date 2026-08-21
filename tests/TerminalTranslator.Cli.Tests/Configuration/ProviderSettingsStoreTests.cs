using System.CommandLine;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Configuration;

namespace TerminalTranslator.Cli.Tests.Configuration;

[TestClass]
public sealed class ProviderSettingsStoreTests
{
    [TestMethod]
    public void DefaultDirectory_UsesWindowsLocalApplicationData()
    {
        ProviderSettingsStore store = new();
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerminalTranslator");

        Assert.IsTrue(Path.IsPathFullyQualified(store.SettingsDirectory));
        Assert.AreEqual(expected, store.SettingsDirectory);
        Assert.AreEqual(Path.Combine(expected, "provider-settings.json"), store.SettingsPath);
    }

    [TestMethod]
    public async Task SaveAsync_CreatesMissingDirectoryBeforeAtomicallyReplacingSettings()
    {
        using TemporaryDirectory temporary = new();
        string settingsDirectory = Path.Combine(temporary.Path, "missing", "nested");
        ProviderSettingsStore store = new(settingsDirectory);
        ProviderSettings settings = CreateSettings();

        await store.SaveAsync(settings);

        Assert.IsTrue(Directory.Exists(settingsDirectory));
        Assert.IsTrue(File.Exists(store.SettingsPath));
        Assert.AreEqual(settings, await store.LoadAsync());
        Assert.AreEqual(0, Directory.GetFiles(settingsDirectory, "*.tmp").Length);
    }

    [TestMethod]
    public async Task ConfigureCommand_WhenSettingsCannotBeWritten_ReportsSafeIoDetails()
    {
        using TemporaryDirectory temporary = new();
        string blockedDirectory = Path.Combine(temporary.Path, "not-a-directory");
        await File.WriteAllTextAsync(blockedDirectory, "blocker");
        ProviderSettingsStore store = new(blockedDirectory);
        using StringWriter output = new();
        using StringWriter error = new();
        Command command = ConfigureCommand.Create(store, output, error);

        int exitCode = await command.Parse(
            [
                "--endpoint", "https://api.deepseek.com/chat/completions",
                "--model", "deepseek-v4-flash",
                "--api-key-env", "DEEPSEEK_API_KEY",
            ]).InvokeAsync();

        Assert.AreEqual(7, exitCode);
        StringAssert.Contains(error.ToString(), "Unable to write provider settings");
        StringAssert.Contains(error.ToString(), blockedDirectory);
        Assert.IsFalse(error.ToString().Contains("DEEPSEEK_API_KEY", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ConfigureCommand_WithoutTimeout_UsesReliableDefault()
    {
        using TemporaryDirectory temporary = new();
        ProviderSettingsStore store = new(temporary.Path);
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await ConfigureCommand.Create(store, output, error).Parse(
            [
                "--endpoint", "https://api.deepseek.com/chat/completions",
                "--model", "deepseek-v4-flash",
                "--api-key-env", "DEEPSEEK_API_KEY",
            ]).InvokeAsync();

        ProviderSettings settings = (await store.LoadAsync())!;
        Assert.AreEqual(0, exitCode, error.ToString());
        Assert.AreEqual(ProviderSettings.DefaultRequestTimeout, settings.RequestTimeout);
        Assert.IsTrue(settings.RequestTimeoutIsDefault);
    }

    [TestMethod]
    public async Task LoadAsync_MigratesLegacyDefaultTimeout()
    {
        using TemporaryDirectory temporary = new();
        ProviderSettingsStore store = new(temporary.Path);
        const string legacySettings =
            "{\"adapter\":\"chat-completion-http\",\"endpoint\":\"https://api.deepseek.com/chat/completions\",\"model\":\"deepseek-v4-flash\",\"apiKeyEnvironmentVariable\":\"DEEPSEEK_API_KEY\",\"requestTimeout\":\"00:00:01.5000000\",\"sourceLanguage\":\"en\",\"targetLanguage\":\"zh-Hans\"}";
        await File.WriteAllTextAsync(store.SettingsPath, legacySettings);

        ProviderSettings settings = (await store.LoadAsync())!;

        Assert.AreEqual(ProviderSettings.DefaultRequestTimeout, settings.RequestTimeout);
        Assert.IsTrue(settings.RequestTimeoutIsDefault);
    }

    [TestMethod]
    public async Task SaveAsync_UsesExplicitAllowListAndNeverPersistsCredentialValue()
    {
        using TemporaryDirectory temporary = new();
        ProviderSettingsStore store = new(temporary.Path);
        const string credential = "credential-value-must-remain-outside-json";
        string? previous = Environment.GetEnvironmentVariable("TT_SETTINGS_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("TT_SETTINGS_SECRET", credential);
            await store.SaveAsync(ProviderSettings.Create(
                new Uri("https://provider.example/chat/completions"),
                "model-a",
                "TT_SETTINGS_SECRET",
                TimeSpan.FromSeconds(1)));

            string json = await File.ReadAllTextAsync(store.SettingsPath);
            Assert.IsFalse(json.Contains(credential, StringComparison.Ordinal));
            Assert.IsFalse(json.Contains("fingerprint", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(json, "TT_SETTINGS_SECRET");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TT_SETTINGS_SECRET", previous);
        }
    }

    private static ProviderSettings CreateSettings() => ProviderSettings.Create(
        new Uri("https://api.deepseek.com/chat/completions"),
        "deepseek-v4-flash",
        "DEEPSEEK_API_KEY",
        TimeSpan.FromMilliseconds(1500));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
