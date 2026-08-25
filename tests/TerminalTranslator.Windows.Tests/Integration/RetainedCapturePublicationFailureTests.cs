using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class RetainedCapturePublicationFailureTests
{
    [TestMethod]
    [DataRow(RetainedPublicationPhase.CandidateWritten)]
    [DataRow(RetainedPublicationPhase.ManifestTemporaryWritten)]
    [DataRow(RetainedPublicationPhase.BeforeManifestSwap)]
    public async Task PreCommitFault_LeavesOldGenerationOnly(RetainedPublicationPhase fault)
    {
        using TemporaryDirectory temporary = new();
        RetainedCaptureStore store = new(temporary.Path);
        CapturedCommandFinalizer finalizer = new(store);
        Assert.AreEqual(FinalizationOutcome.Published, (await finalizer.FinalizeAsync(Candidate(1))).Outcome);
        store.PublicationObserver = phase =>
        {
            if (phase == fault)
            {
                throw new IOException("injected");
            }
        };

        FinalizationResult result = await finalizer.FinalizeAsync(Candidate(2));

        Assert.AreEqual(FinalizationOutcome.Failed, result.Outcome);
        Assert.IsFalse(result.CommitOccurred);
        Assert.AreEqual(1L, (await store.LoadCommittedAsync())!.Records.Single().Sequence);
    }

    [TestMethod]
    [DataRow(RetainedPublicationPhase.AfterManifestSwap)]
    [DataRow(RetainedPublicationPhase.SupersededDelete)]
    public async Task PostCommitFault_LeavesNewBoundedGenerationOnly(RetainedPublicationPhase fault)
    {
        using TemporaryDirectory temporary = new();
        RetainedCaptureStore store = new(temporary.Path);
        CapturedCommandFinalizer finalizer = new(store);
        Assert.AreEqual(FinalizationOutcome.Published, (await finalizer.FinalizeAsync(Candidate(1))).Outcome);
        store.PublicationObserver = phase =>
        {
            if (phase == fault)
            {
                throw new IOException("injected");
            }
        };

        FinalizationResult result = await finalizer.FinalizeAsync(Candidate(2));

        Assert.AreEqual(FinalizationOutcome.Failed, result.Outcome);
        Assert.IsTrue(result.CommitOccurred);
        RetainedGeneration generation = (await store.LoadCommittedAsync())!;
        Assert.AreEqual(2L, generation.Records.Last().Sequence);
        Assert.IsLessThanOrEqualTo(RetainedCaptureStore.MaximumRetainedBytes, generation.RetainedBytes);
    }

    [TestMethod]
    public async Task UnreliableCandidate_IsNeverPublished()
    {
        using TemporaryDirectory temporary = new();
        CapturedCommandFinalizer finalizer = new(new RetainedCaptureStore(temporary.Path));
        FinalizationResult result = await finalizer.FinalizeAsync(Candidate(1) with { IsReliable = false });
        Assert.AreEqual(FinalizationOutcome.RejectedUnreliable, result.Outcome);
        Assert.IsFalse(File.Exists(Path.Combine(temporary.Path, "committed-manifest.json")));
    }

    [TestMethod]
    public async Task SupersededDeletionFailure_RegistersOneBlockingResidueSet()
    {
        using TemporaryDirectory temporary = new();
        RetainedCaptureStore store = new(temporary.Path);
        FinalizationResidueManager residue = new(temporary.Path);
        CapturedCommandFinalizer finalizer = new(store, residue);
        Assert.AreEqual(FinalizationOutcome.Published, (await finalizer.FinalizeAsync(Candidate(1))).Outcome);
        store.ArtifactDelete = path => !Path.GetFileName(path).StartsWith("g00000000000000000001-", StringComparison.Ordinal);

        FinalizationResult failed = await finalizer.FinalizeAsync(Candidate(2));

        Assert.AreEqual(FinalizationOutcome.Failed, failed.Outcome);
        Assert.IsTrue(failed.CommitOccurred);
        Assert.IsGreaterThan(0, failed.ResiduePaths.Count);
        Assert.IsFalse(residue.CanStartCapture);
        Assert.AreEqual(FinalizationOutcome.Failed, (await finalizer.FinalizeAsync(Candidate(3))).Outcome);
    }

    private static CapturedCommandCandidate Candidate(long sequence) => new(
        Guid.NewGuid().ToString("N"),
        sequence,
        System.Text.Encoding.UTF8.GetBytes($"content-{sequence}"),
        System.Text.Encoding.UTF8.GetBytes($"metadata-{sequence}"),
        true);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-publish-fault-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
