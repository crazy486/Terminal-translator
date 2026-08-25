using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class StaleCaptureCleanupTests
{
    private const string UserSid = "S-1-5-21-1000";

    [TestMethod]
    public async Task DeadAndPidReusedOwners_AreDeletedByIdentityNotTimestamp()
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult dead = await CreateSessionAsync(temporary.Path, 6001);
        CaptureBootstrapResult reused = await CreateSessionAsync(temporary.Path, 6002);
        Directory.SetLastWriteTimeUtc(dead.SessionDirectory!, DateTime.UtcNow);
        Directory.SetLastWriteTimeUtc(reused.SessionDirectory!, DateTime.UtcNow.AddYears(-5));
        MapProbe probe = new(new Dictionary<int, CaptureOwnerStatus>
        {
            [6001] = CaptureOwnerStatus.Dead,
            [6002] = CaptureOwnerStatus.ReusedOrDifferentOwner,
        });

        StaleCaptureCleanupResult result = await new StaleCaptureCleaner(temporary.Path, probe).CleanupAsync(UserSid);

        Assert.AreEqual(2, result.Deleted);
        Assert.IsFalse(Directory.Exists(dead.SessionDirectory));
        Assert.IsFalse(Directory.Exists(reused.SessionDirectory));
    }

    [TestMethod]
    public async Task LiveUncertainWrongUserAndMalformedProof_ArePreserved()
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult live = await CreateSessionAsync(temporary.Path, 6101);
        CaptureBootstrapResult uncertain = await CreateSessionAsync(temporary.Path, 6102);
        CaptureBootstrapResult wrongUser = await new CaptureSessionBootstrap(temporary.Path).InitializeAsync(
            CapturePreference.Enabled,
            new CaptureOwnerIdentity("S-1-5-21-OTHER", 6103, 638900000000000003),
            "2.0",
            false);
        string malformed = Path.Combine(temporary.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(malformed);
        await File.WriteAllTextAsync(Path.Combine(malformed, CaptureSessionBootstrap.OwnerManifestFileName), "not-json");
        MapProbe probe = new(new Dictionary<int, CaptureOwnerStatus>
        {
            [6101] = CaptureOwnerStatus.ExactOwnerAlive,
            [6102] = CaptureOwnerStatus.Uncertain,
            [6103] = CaptureOwnerStatus.Dead,
        });

        StaleCaptureCleanupResult result = await new StaleCaptureCleaner(temporary.Path, probe).CleanupAsync(UserSid);

        Assert.AreEqual(0, result.Deleted);
        Assert.AreEqual(4, result.Preserved);
        Assert.IsTrue(Directory.Exists(live.SessionDirectory));
        Assert.IsTrue(Directory.Exists(uncertain.SessionDirectory));
        Assert.IsTrue(Directory.Exists(wrongUser.SessionDirectory));
        Assert.IsTrue(Directory.Exists(malformed));
    }

    [TestMethod]
    public async Task QuestionOnlyInitialization_InspectsOwnerMetadataButNeverOpensCommandContent()
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult stale = await CreateSessionAsync(temporary.Path, 6201);
        string contentPath = Path.Combine(stale.SessionDirectory!, "synthetic-sensitive.content");
        await File.WriteAllTextAsync(contentPath, "SECRET-SHOULD-NOT-BE-READ");
        List<string> metadataReads = [];
        List<string> deleted = [];
        StaleCaptureCleaner cleaner = new(
            temporary.Path,
            new MapProbe(new Dictionary<int, CaptureOwnerStatus> { [6201] = CaptureOwnerStatus.Dead }),
            deleteDirectory: path => { deleted.Add(path); Directory.Delete(path, true); },
            ownerManifestRead: path => metadataReads.Add(path));

        StaleCaptureCleanupResult result = await cleaner.CleanupForQuestionOnlyInitializationAsync(UserSid);

        Assert.AreEqual(1, result.Deleted);
        Assert.AreEqual(1, metadataReads.Count);
        Assert.AreEqual(CaptureSessionBootstrap.OwnerManifestFileName, Path.GetFileName(metadataReads.Single()));
        CollectionAssert.AreEqual(new[] { stale.SessionDirectory! }, deleted);
    }

    [TestMethod]
    public async Task StaleProof_IsNeverEligibleForAnotherSessionBeforeCleanup()
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult stale = await CreateSessionAsync(temporary.Path, 6301);
        CaptureSessionProof current = CaptureSessionIdentity.Create(
            new CaptureOwnerIdentity(UserSid, 6302, 638900000000000004), "2.0");

        Assert.IsFalse(CaptureSessionIdentity.ValidateAssociation(
            stale.Proof!,
            current.SessionId,
            current.Nonce,
            current.Owner,
            "2.0",
            ownerIsLive: true,
            isNestedSession: false,
            matchingManifestCount: 1));
    }

    private static Task<CaptureBootstrapResult> CreateSessionAsync(string root, int processId) =>
        new CaptureSessionBootstrap(root).InitializeAsync(
            CapturePreference.Enabled,
            new CaptureOwnerIdentity(UserSid, processId, 638900000000000000 + processId),
            "2.0",
            false);

    private sealed class MapProbe(IReadOnlyDictionary<int, CaptureOwnerStatus> statuses) : ICaptureOwnerProcessProbe
    {
        public CaptureOwnerStatus Observe(CaptureOwnerIdentity owner) =>
            statuses.TryGetValue(owner.ProcessId, out CaptureOwnerStatus status) ? status : CaptureOwnerStatus.Uncertain;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-stale-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
