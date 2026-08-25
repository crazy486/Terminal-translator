using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class CaptureCleanupWatcherTests
{
    private static readonly CaptureOwnerIdentity Owner = new("S-1-5-21-1000", 5001, 638900000000000000);

    [TestMethod]
    public void LoaderRegistersBoundedHiddenExitCleanupAndOneWatcherRole()
    {
        string loader = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TerminalTranslator.Profile.ps1"));
        StringAssert.Contains(loader, "Register-EngineEvent -SourceIdentifier PowerShell.Exiting");
        StringAssert.Contains(loader, "'__capture', 'cleanup'");
        StringAssert.Contains(loader, "'__capture', 'watch'");
        StringAssert.Contains(loader, "-WindowStyle Hidden");
        StringAssert.Contains(loader, "TtCaptureWatcherStarted");
        Assert.IsFalse(loader.Contains("HttpClient", StringComparison.Ordinal));
        Assert.IsFalse(loader.Contains("Provider", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExactDeadOwner_IsDeletedWithoutOpeningCaptureContent()
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult session = await CreateSessionAsync(temporary.Path);
        string content = Path.Combine(session.SessionDirectory!, "sensitive.content");
        await File.WriteAllTextAsync(content, "synthetic-secret");
        RecordingProbe probe = new(CaptureOwnerStatus.Dead);
        List<string> deleted = [];
        CaptureCleanupWatcher watcher = new(
            temporary.Path,
            probe,
            path => { deleted.Add(path); Directory.Delete(path, true); });

        CaptureCleanupWatcherOutcome outcome = await watcher.RunAsync(
            session.SessionDirectory!, session.Proof!, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(CaptureCleanupWatcherOutcome.Cleaned, outcome);
        Assert.AreEqual(1, probe.Observed.Count);
        CollectionAssert.AreEqual(new[] { session.SessionDirectory! }, deleted);
        Assert.IsFalse(Directory.Exists(session.SessionDirectory));
    }

    [TestMethod]
    public async Task LiveOrUncertainOwner_IsNeverDeletedAndWaitIsBounded()
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult live = await CreateSessionAsync(temporary.Path);
        CaptureCleanupWatcher liveWatcher = new(temporary.Path, new RecordingProbe(CaptureOwnerStatus.ExactOwnerAlive));
        CaptureCleanupWatcherOutcome liveOutcome = await liveWatcher.RunAsync(
            live.SessionDirectory!, live.Proof!, TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(CaptureCleanupWatcherOutcome.OwnerStillAlive, liveOutcome);
        Assert.IsTrue(Directory.Exists(live.SessionDirectory));

        CaptureBootstrapResult uncertain = await CreateSessionAsync(temporary.Path, Owner with { ProcessId = 5002 });
        CaptureCleanupWatcher uncertainWatcher = new(temporary.Path, new RecordingProbe(CaptureOwnerStatus.Uncertain));
        CaptureCleanupWatcherOutcome uncertainOutcome = await uncertainWatcher.RunAsync(
            uncertain.SessionDirectory!, uncertain.Proof!, TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(CaptureCleanupWatcherOutcome.OwnerUncertain, uncertainOutcome);
        Assert.IsTrue(Directory.Exists(uncertain.SessionDirectory));
    }

    [TestMethod]
    public async Task AtMostOneWatcherOwnsAnExactSessionAndFailureLeavesResidue()
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult session = await CreateSessionAsync(temporary.Path);
        TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockingProbe probe = new(observed);
        CaptureCleanupWatcher first = new(temporary.Path, probe);
        Task<CaptureCleanupWatcherOutcome> firstRun = first.RunAsync(
            session.SessionDirectory!, session.Proof!, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(5));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        CaptureCleanupWatcher second = new(temporary.Path, new RecordingProbe(CaptureOwnerStatus.Dead));
        CaptureCleanupWatcherOutcome duplicate = await second.RunAsync(
            session.SessionDirectory!, session.Proof!, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(CaptureCleanupWatcherOutcome.AlreadyRunning, duplicate);
        probe.Release();
        Assert.AreEqual(CaptureCleanupWatcherOutcome.OwnerStillAlive, await firstRun);
        Assert.IsTrue(Directory.Exists(session.SessionDirectory));

        CaptureCleanupWatcher failing = new(
            temporary.Path,
            new RecordingProbe(CaptureOwnerStatus.Dead),
            _ => throw new IOException("synthetic cleanup failure"));
        Assert.AreEqual(
            CaptureCleanupWatcherOutcome.Failed,
            await failing.RunAsync(session.SessionDirectory!, session.Proof!, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(1)));
        Assert.IsTrue(Directory.Exists(session.SessionDirectory));
    }

    [TestMethod]
    public async Task WrongProofOrOutsidePath_IsRejectedWithoutDeletion()
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult session = await CreateSessionAsync(temporary.Path);
        CaptureSessionProof wrong = CaptureSessionIdentity.Create(Owner, "2.0");
        CaptureCleanupWatcher watcher = new(temporary.Path, new RecordingProbe(CaptureOwnerStatus.Dead));

        Assert.AreEqual(
            CaptureCleanupWatcherOutcome.InvalidProof,
            await watcher.RunAsync(session.SessionDirectory!, wrong, TimeSpan.Zero, TimeSpan.Zero));
        Assert.IsTrue(Directory.Exists(session.SessionDirectory));
    }

    private static Task<CaptureBootstrapResult> CreateSessionAsync(string root, CaptureOwnerIdentity? owner = null) =>
        new CaptureSessionBootstrap(root).InitializeAsync(CapturePreference.Enabled, owner ?? Owner, "2.0", false);

    private sealed class RecordingProbe(CaptureOwnerStatus status) : ICaptureOwnerProcessProbe
    {
        public List<CaptureOwnerIdentity> Observed { get; } = [];
        public CaptureOwnerStatus Observe(CaptureOwnerIdentity owner)
        {
            Observed.Add(owner);
            return status;
        }
    }

    private sealed class BlockingProbe(TaskCompletionSource observed) : ICaptureOwnerProcessProbe
    {
        private readonly ManualResetEventSlim _release = new(false);
        public CaptureOwnerStatus Observe(CaptureOwnerIdentity owner)
        {
            observed.TrySetResult();
            _release.Wait(TimeSpan.FromSeconds(1));
            return CaptureOwnerStatus.ExactOwnerAlive;
        }
        public void Release() => _release.Set();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-watcher-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
