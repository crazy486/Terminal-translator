using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Cli.Tests.Configuration;

[TestClass]
public sealed class CapturePreferenceStoreTests
{
    [TestMethod]
    public async Task MissingPreference_DefaultsDisabled()
    {
        using TemporaryDirectory temporary = new();
        CapturePreferenceStore store = new(temporary.Path);
        Assert.AreEqual(CapturePreference.Disabled, await store.LoadAsync());
    }

    [TestMethod]
    public async Task SaveAsync_AtomicallyPersistsOnlyPreference()
    {
        using TemporaryDirectory temporary = new();
        CapturePreferenceStore store = new(temporary.Path);
        await store.SaveAsync(CapturePreference.Enabled);

        Assert.AreEqual(CapturePreference.Enabled, await store.LoadAsync());
        Assert.AreEqual(0, Directory.GetFiles(temporary.Path, "*.tmp").Length);
        string json = await File.ReadAllTextAsync(store.PreferencePath);
        StringAssert.Contains(json, "enabled");
        Assert.IsFalse(json.Contains("command", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("output", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task InvalidPreference_FailsClosed()
    {
        using TemporaryDirectory temporary = new();
        CapturePreferenceStore store = new(temporary.Path);
        await File.WriteAllTextAsync(store.PreferencePath, "{\"state\":\"unknown\"}");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-capture-pref-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
