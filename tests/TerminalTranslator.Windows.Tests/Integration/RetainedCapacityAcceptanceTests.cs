using System.Text;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class RetainedCapacityAcceptanceTests
{
    private static readonly CaptureSessionId Session = new(Guid.Parse("12121212-1212-1212-1212-121212121212"), "nonce");

    [TestMethod]
    public async Task LocallyTruncatedNewestRecordAndCommittedGenerationStayWithinExactCap()
    {
        using TemporaryDirectory directory = new();
        RetainedCaptureStore store = new(directory.Path);
        foreach (int delta in new[] { -1, 0, 1 })
        {
            int size = checked((int)CaptureRetentionPolicy.MaximumRetainedBytes + delta);
            CapturedCommand source = Command(new string('x', size), delta + 2);
            LocalRetentionResult bounded = OversizedCommandRetention.Create(source, 512, 256, 2048);
            Assert.IsTrue(bounded.Supported);
            Assert.IsLessThanOrEqualTo(bounded.RetainedLogicalBytes, CaptureRetentionPolicy.MaximumRetainedBytes);
        }

        CapturedCommand oversized = Command(new string('z', checked((int)CaptureRetentionPolicy.MaximumRetainedBytes + 1)), 10);
        LocalRetentionResult retained = OversizedCommandRetention.Create(oversized, 512, 256, 4096);
        byte[] content = Encoding.UTF8.GetBytes(retained.Command!.Output);
        byte[] metadata = Encoding.UTF8.GetBytes("local=LocalHeadTail;original=10180001;session=trusted;boundary=trusted");
        RetainedGeneration generation = await store.PublishAsync([
            new RetainedRecordWrite("newest", 10, content, metadata),
        ]);

        Assert.IsLessThanOrEqualTo(RetainedCaptureStore.MaximumRetainedBytes, generation.RetainedBytes);
        Assert.AreEqual(10L, generation.Records.Single().Sequence);
        StringAssert.Contains(Encoding.UTF8.GetString(await store.ReadContentAsync(generation.Records.Single())), "z");
    }

    [TestMethod]
    public async Task ProductionFinalizer_PersistsLocalHeadTailAndRetrieverRoundTripsTrustedFacts()
    {
        using TemporaryDirectory directory = new();
        RetainedCaptureStore store = new(directory.Path);
        string output = "HEAD-MARKER-" +
            new string('x', checked((int)CaptureRetentionPolicy.MaximumRetainedBytes)) +
            "-TAIL-MARKER";
        CapturedCommand source = Command(output, 11, historyId: 77);
        CapturedCommandCandidate candidate = new(
            "oversized-production",
            source.Sequence,
            Encoding.UTF8.GetBytes(source.Output),
            RetainedCommandRecordCodec.SerializeMetadata(source),
            true,
            source);

        FinalizationResult finalized = await new CapturedCommandFinalizer(store).FinalizeAsync(candidate);
        RetainedGeneration generation = (await store.LoadCommittedAsync())!;
        RetainedRecordDescriptor descriptor = generation.Records.Single();
        CapturedCommand persisted = RetainedCommandRecordCodec.Deserialize(
            await store.ReadContentAsync(descriptor),
            await store.ReadMetadataAsync(descriptor));
        PreviousCommandResult retrieval = PreviousCommandRetriever.Retrieve(new PreviousCommandRetrievalState(
            CapturePreference.Enabled,
            CaptureHealthState.Healthy,
            Session,
            [persisted],
            IsStoreReliable: true));

        Assert.AreEqual(FinalizationOutcome.Published, finalized.Outcome);
        Assert.IsLessThanOrEqualTo(CaptureRetentionPolicy.MaximumRetainedBytes, generation.RetainedBytes);
        Assert.AreEqual(PreviousCommandResultKind.Success, retrieval.Kind);
        PreviousCommandSnapshot snapshot = retrieval.Snapshot!;
        Assert.AreEqual(LocalCaptureCompleteness.LocalHeadTail, snapshot.LocalCompleteness);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(output), snapshot.OriginalOutputBytes);
        Assert.AreEqual(Session, snapshot.Session);
        Assert.AreEqual(11L, snapshot.Sequence);
        Assert.AreEqual("generate", snapshot.CommandText);
        Assert.AreEqual(77L, persisted.HistoryId);
        Assert.IsTrue(persisted.Boundary.IsReliable);
        Assert.IsTrue(persisted.Boundary.PowerShellSucceeded);
        Assert.AreEqual(0, persisted.Boundary.NativeExitCode);
        Assert.IsTrue(snapshot.PowerShellSucceeded);
        Assert.AreEqual(0, snapshot.NativeExitCode);
        Assert.IsTrue(snapshot.Output.StartsWith("HEAD-MARKER", StringComparison.Ordinal));
        Assert.IsTrue(snapshot.Output.EndsWith("TAIL-MARKER", StringComparison.Ordinal));
        Assert.IsLessThan(output.Length, snapshot.Output.Length);
    }

    [TestMethod]
    public async Task ProductionFinalizer_EssentialMetadataOverflowFailsClosedWithoutPublishing()
    {
        using TemporaryDirectory directory = new();
        string commandText = new('c', checked((int)CaptureRetentionPolicy.MaximumRetainedBytes));
        CommandBoundary boundary = new(Session, 12, commandText, true, 1, false);
        CapturedCommand source = new(
            Session,
            12,
            commandText,
            "English failure",
            boundary,
            LocalCaptureCompleteness.Complete,
            Encoding.UTF8.GetByteCount("English failure"),
            false,
            88);
        RetainedCaptureStore store = new(directory.Path);

        FinalizationResult result = await new CapturedCommandFinalizer(store).FinalizeAsync(
            new CapturedCommandCandidate(
                "metadata-overflow",
                source.Sequence,
                Encoding.UTF8.GetBytes(source.Output),
                RetainedCommandRecordCodec.SerializeMetadata(source),
                true,
                source));

        Assert.AreEqual(FinalizationOutcome.Failed, result.Outcome);
        Assert.IsFalse(result.CommitOccurred);
        Assert.IsNull(await store.LoadCommittedAsync());
    }

    private static CapturedCommand Command(string output, long sequence, long historyId = 0)
    {
        CommandBoundary boundary = new(Session, sequence, "generate", true, 0, false, powerShellSucceeded: true);
        return new(Session, sequence, "generate", output, boundary, LocalCaptureCompleteness.Complete,
            Encoding.UTF8.GetByteCount(output), false, historyId);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-capacity-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
