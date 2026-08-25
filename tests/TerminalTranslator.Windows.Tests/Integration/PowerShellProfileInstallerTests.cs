using System.Text;
using TerminalTranslator.Windows.PowerShell;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class PowerShellProfileInstallerTests
{
    [TestMethod]
    public async Task InstallAndRemove_AreIdempotentAndPreserveUnrelatedContent()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        const string original = "# user header\r\nfunction prompt { 'CUSTOM> ' }\r\n# user tail\r\n";
        await File.WriteAllTextAsync(profile, original, new UTF8Encoding(false));
        PowerShellProfileInstaller installer = new();

        await installer.InstallAsync(profile, "C:\\LocalAppData\\TerminalTranslator\\PowerShell\\loader.ps1");
        string once = await File.ReadAllTextAsync(profile);
        await installer.InstallAsync(profile, "C:\\LocalAppData\\TerminalTranslator\\PowerShell\\loader.ps1");
        string twice = await File.ReadAllTextAsync(profile);

        Assert.AreEqual(once, twice);
        StringAssert.Contains(once, original);
        Assert.AreEqual(1, Count(once, PowerShellProfileInstaller.BlockBegin));

        await installer.RemoveAsync(profile);
        Assert.AreEqual(original, await File.ReadAllTextAsync(profile));
        await installer.RemoveAsync(profile);
        Assert.AreEqual(original, await File.ReadAllTextAsync(profile));
        Assert.AreEqual(0, Directory.GetFiles(temporary.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task Install_WhenTransactionFails_LeavesOriginalAndCleansTemporary()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        await File.WriteAllTextAsync(profile, "Write-Output 'user'\r\n");
        PowerShellProfileInstaller installer = new(phase =>
        {
            if (phase == ProfileEditPhase.BeforeCommit)
            {
                throw new IOException("injected");
            }
        });

        await Assert.ThrowsExactlyAsync<IOException>(() => installer.InstallAsync(profile, "C:\\loader.ps1"));
        Assert.AreEqual("Write-Output 'user'\r\n", await File.ReadAllTextAsync(profile));
        Assert.AreEqual(0, Directory.GetFiles(temporary.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task Install_WhenProfileChangesConcurrently_RefusesReplacement()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        await File.WriteAllTextAsync(profile, "original\r\n");
        PowerShellProfileInstaller installer = new(phase =>
        {
            if (phase == ProfileEditPhase.BeforeCommit)
            {
                File.AppendAllText(profile, "concurrent\r\n");
            }
        });

        await Assert.ThrowsExactlyAsync<IOException>(() => installer.InstallAsync(profile, "C:\\loader.ps1"));
        Assert.AreEqual("original\r\nconcurrent\r\n", await File.ReadAllTextAsync(profile));
    }

    private static int Count(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-profile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
