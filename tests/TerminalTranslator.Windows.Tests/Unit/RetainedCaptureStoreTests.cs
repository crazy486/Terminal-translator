using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.Tests.Unit;

[TestClass]
public sealed class RetainedCaptureStoreTests
{
    [TestMethod]
    public async Task PublishAsync_ExposesOnlyCommittedManifestGeneration()
    {
        using TemporaryDirectory temporary = new();
        RetainedCaptureStore store = new(temporary.Path);
        RetainedRecordWrite first = Record(1, "first");
        await store.PublishAsync([first]);

        RetainedGeneration loaded = (await store.LoadCommittedAsync())!;
        Assert.AreEqual(1L, loaded.Generation);
        Assert.AreEqual(1L, loaded.Records.Single().Sequence);
        CollectionAssert.AreEqual(first.Content, await store.ReadContentAsync(loaded.Records.Single()));
        Assert.IsLessThanOrEqualTo(RetainedCaptureStore.MaximumRetainedBytes, loaded.RetainedBytes);
    }

    [TestMethod]
    public async Task FailureBeforeManifestSwap_LeavesPriorGenerationAuthoritative()
    {
        using TemporaryDirectory temporary = new();
        RetainedCaptureStore store = new(temporary.Path);
        await store.PublishAsync([Record(1, "first")]);
        store.PublicationObserver = phase =>
        {
            if (phase == RetainedPublicationPhase.BeforeManifestSwap)
            {
                throw new IOException("injected");
            }
        };

        RetainedPublicationException failure = await Assert.ThrowsExactlyAsync<RetainedPublicationException>(
            () => store.PublishAsync([Record(2, "second")]));
        Assert.IsFalse(failure.CommitOccurred);
        Assert.AreEqual(1L, (await store.LoadCommittedAsync())!.Records.Single().Sequence);
    }

    [TestMethod]
    public async Task FailureAfterManifestSwap_LeavesNewGenerationAuthoritative()
    {
        using TemporaryDirectory temporary = new();
        RetainedCaptureStore store = new(temporary.Path);
        await store.PublishAsync([Record(1, "first")]);
        store.PublicationObserver = phase =>
        {
            if (phase == RetainedPublicationPhase.AfterManifestSwap)
            {
                throw new IOException("injected");
            }
        };

        RetainedPublicationException failure = await Assert.ThrowsExactlyAsync<RetainedPublicationException>(
            () => store.PublishAsync([Record(2, "second")]));
        Assert.IsTrue(failure.CommitOccurred);
        Assert.AreEqual(2L, (await store.LoadCommittedAsync())!.Records.Single().Sequence);
    }

    private static RetainedRecordWrite Record(long sequence, string content) => new(
        Guid.NewGuid().ToString("N"),
        sequence,
        System.Text.Encoding.UTF8.GetBytes(content),
        System.Text.Encoding.UTF8.GetBytes($"metadata-{sequence}"));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-retained-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
