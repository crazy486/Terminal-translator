using System.Text;
using TerminalTranslator.Windows.PowerShell;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class PowerShellIntegrationLifecycleTests
{
    [TestMethod]
    public async Task EnableUpdateDisable_IsIdempotentReversibleAndPreservesCustomProfile()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        const string user = "function prompt { 'CUSTOM> ' }\r\n# user-tail\r\n";
        await File.WriteAllTextAsync(profile, user);
        PowerShellIntegrationInstaller installer = new(Path.Combine(temporary.Path, "integration"));

        await installer.InstallAsync(profile);
        string installed = await File.ReadAllTextAsync(profile);
        await installer.InstallAsync(profile);
        Assert.AreEqual(installed, await File.ReadAllTextAsync(profile));
        StringAssert.Contains(installed, user);
        Assert.AreEqual(1, installed.Split(PowerShellProfileInstaller.BlockBegin).Length - 1);

        await installer.RemoveAsync(profile);
        Assert.AreEqual(user, await File.ReadAllTextAsync(profile));
        Assert.IsFalse(File.Exists(installer.LoaderPath));
        Assert.AreEqual(0, Directory.GetFiles(temporary.Path, "*.tmp", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task FailedProfileUpdate_RestoresPriorManagedLoaderAndLeavesUserProfileFirst()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        await File.WriteAllTextAsync(profile, "user\r\n");
        string integration = Path.Combine(temporary.Path, "integration");
        PowerShellIntegrationInstaller initial = new(integration);
        await initial.InstallAsync(profile);
        byte[] installedLoader = await File.ReadAllBytesAsync(initial.LoaderPath);
        byte[] priorLoader = Encoding.UTF8.GetBytes(
            "# prior valid TT-managed loader\r\n" + Encoding.UTF8.GetString(installedLoader));
        await File.WriteAllBytesAsync(initial.LoaderPath, priorLoader);

        // Keep the prior loader valid while making the owned block require a real update. The
        // non-canonical path resolves to the same loader, but differs from the installer's
        // canonical candidate so the pre-commit synchronization point is guaranteed to run.
        string priorLoaderReference = Path.Combine(
            integration,
            ".",
            Path.GetFileName(initial.LoaderPath));
        await new PowerShellProfileInstaller().InstallAsync(profile, priorLoaderReference);
        string profileA = await File.ReadAllTextAsync(profile);
        List<ProfileEditPhase> phases = [];

        PowerShellProfileInstaller failingProfile = new(phase =>
        {
            phases.Add(phase);
            if (phase == ProfileEditPhase.BeforeCommit)
            {
                File.AppendAllText(profile, "external\r\n");
            }
        });
        PowerShellIntegrationInstaller update = new(integration, failingProfile);
        await Assert.ThrowsExactlyAsync<IOException>(() => update.InstallAsync(profile));

        CollectionAssert.AreEqual(
            new[] { ProfileEditPhase.TemporaryWritten, ProfileEditPhase.BeforeCommit },
            phases);
        CollectionAssert.AreEqual(priorLoader, await File.ReadAllBytesAsync(initial.LoaderPath));
        Assert.AreEqual(profileA + "external\r\n", await File.ReadAllTextAsync(profile));
        Assert.AreEqual(0, Directory.GetFiles(temporary.Path, "*.tmp", SearchOption.AllDirectories).Length);

        await initial.InstallAsync(profile);
        string retried = await File.ReadAllTextAsync(profile);
        StringAssert.Contains(retried, "external\r\n");
        StringAssert.Contains(retried, initial.LoaderPath);
        Assert.AreEqual(1, retried.Split(PowerShellProfileInstaller.BlockBegin).Length - 1);
        await initial.InstallAsync(profile);
        Assert.AreEqual(retried, await File.ReadAllTextAsync(profile));
    }

    [TestMethod]
    public async Task MalformedPriorOwnedBlock_FailsWithoutRewritingProfile()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        string malformed = "user\r\n" + PowerShellProfileInstaller.BlockBegin + "\r\n. 'broken.ps1'\r\n";
        await File.WriteAllTextAsync(profile, malformed);
        PowerShellIntegrationInstaller installer = new(Path.Combine(temporary.Path, "integration"));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => installer.InstallAsync(profile));
        Assert.AreEqual(malformed, await File.ReadAllTextAsync(profile));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-integration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
