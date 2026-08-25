using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class CaptureSessionBootstrapTests
{
    private static readonly CaptureOwnerIdentity Parent = new("S-1-5-21-1000", 4100, 638900000000000000);

    [TestMethod]
    public async Task Enabled_CreatesFreshBoundSessionProofAndOpeningStaging()
    {
        using TemporaryDirectory temporary = new();
        CaptureSessionBootstrap bootstrap = new(temporary.Path);

        CaptureBootstrapResult result = await bootstrap.InitializeAsync(
            CapturePreference.Enabled,
            Parent,
            "2.0",
            isHostedFeature001Session: false);

        Assert.AreEqual(CaptureBootstrapStatus.Active, result.Status);
        Assert.IsNotNull(result.Proof);
        Assert.AreEqual(Parent, result.Proof.Owner);
        Assert.AreEqual("2.0", result.Proof.IntegrationVersion);
        Assert.AreEqual(result.Proof.SessionId.ToString("N"), Path.GetFileName(result.SessionDirectory));
        Assert.IsTrue(Directory.Exists(result.SessionDirectory));
        Assert.AreEqual(result.Proof.SessionId.ToString("N"), result.Environment[CaptureSessionIdentity.SessionEnvironmentVariable]);
        Assert.AreEqual(result.Proof.Nonce, result.Environment[CaptureSessionIdentity.NonceEnvironmentVariable]);
        Assert.IsTrue(File.Exists(Path.Combine(result.SessionDirectory!, CaptureSessionBootstrap.OwnerManifestFileName)));
        Assert.IsTrue(File.Exists(Path.Combine(result.SessionDirectory!, CaptureSessionBootstrap.OpeningBoundaryFileName)));
        Assert.AreEqual(Path.Combine(result.SessionDirectory!, "staging-00000000000000000001.txt"), result.StagingPath);
    }

    [TestMethod]
    public async Task DisabledAndHostedSessions_CreateNoCommandContentStorage()
    {
        using TemporaryDirectory temporary = new();
        CaptureSessionBootstrap bootstrap = new(temporary.Path);

        CaptureBootstrapResult disabled = await bootstrap.InitializeAsync(
            CapturePreference.Disabled, Parent, "2.0", false);
        CaptureBootstrapResult hosted = await bootstrap.InitializeAsync(
            CapturePreference.Enabled, Parent, "2.0", true);

        Assert.AreEqual(CaptureBootstrapStatus.Disabled, disabled.Status);
        Assert.AreEqual(CaptureBootstrapStatus.HostedFeature001Excluded, hosted.Status);
        Assert.AreEqual(0, Directory.GetDirectories(temporary.Path).Length);
    }

    [TestMethod]
    public async Task NestedNormalShells_ReceiveIndependentIdentityNonceOwnerAndStorage()
    {
        using TemporaryDirectory temporary = new();
        CaptureSessionBootstrap bootstrap = new(temporary.Path);
        CaptureOwnerIdentity nested = new(Parent.UserSid, 4200, Parent.ProcessStartUtcTicks + 100);

        CaptureBootstrapResult outer = await bootstrap.InitializeAsync(CapturePreference.Enabled, Parent, "2.0", false);
        CaptureBootstrapResult inner = await bootstrap.InitializeAsync(CapturePreference.Enabled, nested, "2.0", false);

        Assert.AreNotEqual(outer.Proof!.SessionId, inner.Proof!.SessionId);
        Assert.AreNotEqual(outer.Proof.Nonce, inner.Proof.Nonce);
        Assert.AreNotEqual(outer.SessionDirectory, inner.SessionDirectory);
        Assert.AreEqual(Parent, outer.Proof.Owner);
        Assert.AreEqual(nested, inner.Proof.Owner);
    }

    [TestMethod]
    public async Task StorageOrMetadataFailure_ReturnsUnavailableAndRemovesPartialSession()
    {
        using TemporaryDirectory temporary = new();
        CaptureSessionBootstrap bootstrap = new(
            temporary.Path,
            afterSessionDirectoryCreated: _ => throw new UnauthorizedAccessException("synthetic ACL failure"));

        CaptureBootstrapResult result = await bootstrap.InitializeAsync(
            CapturePreference.Enabled, Parent, "2.0", false);

        Assert.AreEqual(CaptureBootstrapStatus.Unavailable, result.Status);
        Assert.AreEqual(CaptureFailureReason.Storage, result.FailureReason);
        Assert.AreEqual(CaptureHealthNotification.CaptureUnavailable, result.Notification);
        Assert.AreEqual(0, Directory.GetDirectories(temporary.Path).Length);
        Assert.IsNull(result.Proof);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-bootstrap-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
