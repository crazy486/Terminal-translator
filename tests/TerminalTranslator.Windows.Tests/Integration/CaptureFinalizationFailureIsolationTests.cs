using System.Text;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class CaptureFinalizationFailureIsolationTests
{
    [TestMethod]
    public async Task SnapshotTimeoutExclusiveReadAndMutation_AllFailClosed()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "staging.txt");
        await File.WriteAllTextAsync(path, "stable content");
        await using (FileStream held = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            StableTranscriptSnapshotResult blocked = await StableTranscriptSnapshot.AcquireAsync(
                path, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1));
            Assert.IsFalse(blocked.IsStable);
        }

        StableTranscriptSnapshotResult stable = await StableTranscriptSnapshot.AcquireAsync(path, TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(stable.IsStable);
        await File.AppendAllTextAsync(path, " mutation");
        Assert.IsFalse(await stable.MatchesFileAsync(path));
    }

    [TestMethod]
    public async Task CandidateAndManifestPreSwapFailures_LeaveOnlyPriorGenerationAuthoritative()
    {
        using TemporaryDirectory temporary = new();
        RetainedCaptureStore store = new(temporary.Path);
        CapturedCommandFinalizer finalizer = new(store);
        FinalizationResult first = await finalizer.FinalizeAsync(Candidate("first", 1));
        Assert.AreEqual(FinalizationOutcome.Published, first.Outcome);

        store.PublicationObserver = phase =>
        {
            if (phase == RetainedPublicationPhase.BeforeManifestSwap)
            {
                throw new IOException("synthetic pre-swap failure");
            }
        };
        FinalizationResult failed = await finalizer.FinalizeAsync(Candidate("second", 2));

        Assert.AreEqual(FinalizationOutcome.Failed, failed.Outcome);
        Assert.IsFalse(failed.CommitOccurred);
        RetainedGeneration authoritative = (await store.LoadCommittedAsync())!;
        Assert.AreEqual(1L, authoritative.Generation);
        Assert.AreEqual(1L, authoritative.Records.Single().Sequence);
        Assert.AreEqual(0, Directory.GetFiles(temporary.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task PostSwapCleanupFailure_LeavesNewGenerationAndExactlyOneBlockingResidueSet()
    {
        using TemporaryDirectory temporary = new();
        RetainedCaptureStore store = new(temporary.Path);
        FinalizationResidueManager residue = new(temporary.Path, _ => false);
        CapturedCommandFinalizer finalizer = new(store, residue);
        Assert.AreEqual(FinalizationOutcome.Published, (await finalizer.FinalizeAsync(Candidate("first", 1))).Outcome);
        store.ArtifactDelete = _ => false;

        FinalizationResult second = await finalizer.FinalizeAsync(Candidate("second", 2));
        FinalizationResult blocked = await finalizer.FinalizeAsync(Candidate("third", 3));

        Assert.AreEqual(FinalizationOutcome.Failed, second.Outcome);
        Assert.IsTrue(second.CommitOccurred);
        Assert.IsFalse(residue.CanStartCapture);
        Assert.AreEqual(FinalizationOutcome.Failed, blocked.Outcome);
        Assert.IsFalse(blocked.CommitOccurred);
        Assert.AreEqual(1, Directory.GetFiles(temporary.Path, ".finalization-residue.json").Length);
        RetainedGeneration authoritative = (await store.LoadCommittedAsync())!;
        Assert.AreEqual(2L, authoritative.Generation);
        Assert.AreEqual(2L, authoritative.Records.Last().Sequence);
    }

    [TestMethod]
    public async Task CorruptLatestAndRetentionFailure_NeverFallBackToOlderCommand()
    {
        using TemporaryDirectory temporary = new();
        RetainedCaptureStore store = new(temporary.Path);
        CapturedCommandFinalizer finalizer = new(store);
        Assert.AreEqual(FinalizationOutcome.Published, (await finalizer.FinalizeAsync(Candidate("first", 1))).Outcome);
        FinalizationResult oversized = await finalizer.FinalizeAsync(new CapturedCommandCandidate(
            "oversized",
            2,
            new byte[checked((int)RetainedCaptureStore.MaximumRetainedBytes)],
            [1],
            true));
        Assert.AreEqual(FinalizationOutcome.RequiresLocalReduction, oversized.Outcome);

        RetainedGeneration generation = (await store.LoadCommittedAsync())!;
        string content = Path.Combine(temporary.Path, generation.Records.Single().ContentFile);
        await File.AppendAllTextAsync(content, "corrupt");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadCommittedAsync());

        CaptureSessionId session = new(Guid.NewGuid(), "nonce");
        CommandBoundary boundary = new(session, 1, "older", true, 0, false);
        PreviousCommandResult retrieval = PreviousCommandRetriever.Retrieve(new PreviousCommandRetrievalState(
            CapturePreference.Enabled,
            CaptureHealthState.Unavailable,
            session,
            [new CapturedCommand(session, 1, "older", "old output", boundary, LocalCaptureCompleteness.Complete, 10, false)],
            IsStoreReliable: false)
        {
            LatestFinalizationSucceeded = false,
            HashesMatch = false,
        });
        Assert.AreEqual(PreviousCommandResultKind.CaptureUnavailable, retrieval.Kind);
        Assert.IsNull(retrieval.Snapshot);
    }

    private static CapturedCommandCandidate Candidate(string id, long sequence) => new(
        id,
        sequence,
        Encoding.UTF8.GetBytes($"content-{sequence}"),
        Encoding.UTF8.GetBytes($"metadata-{sequence}"),
        true);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-finalization-fault-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
