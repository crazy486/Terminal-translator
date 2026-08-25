using TerminalTranslator.Windows.PowerShell;

namespace TerminalTranslator.Windows.Tests.Unit;

[TestClass]
public sealed class PowerShellLoaderContractTests
{
    [TestMethod]
    public async Task Install_WritesVersionedLocalLoaderAndMarkedProfileBlockIdempotently()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        await File.WriteAllTextAsync(profile, "function prompt { 'CUSTOM> ' }\r\n");
        PowerShellIntegrationInstaller installer = new(Path.Combine(temporary.Path, "local"));

        await installer.InstallAsync(profile);
        string once = await File.ReadAllTextAsync(profile);
        await installer.InstallAsync(profile);

        Assert.AreEqual(once, await File.ReadAllTextAsync(profile));
        Assert.IsTrue(File.Exists(installer.LoaderPath));
        string loader = await File.ReadAllTextAsync(installer.LoaderPath);
        StringAssert.Contains(loader, "TT_HOSTED_SESSION_ID");
        StringAssert.Contains(loader, "TtOriginalPrompt");
        StringAssert.Contains(loader, "TtCaptureIntegrationVersion");
        Assert.IsFalse(loader.Contains("^PS", StringComparison.Ordinal));
        StringAssert.Contains(once, installer.LoaderPath.Replace("'", "''", StringComparison.Ordinal));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-loader-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
