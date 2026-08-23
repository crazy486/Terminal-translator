using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Parsing;
using TerminalTranslator.Core.Privacy;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Windows.Console;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class ProductionPipelineRawLossRegressionTests
{
    private const string RecoveryText = "Recovery output completed successfully.";

    [TestMethod]
    public async Task CsiMiddleLoss_DiscardsAffectedUnitAndRecoversAtNextBoundary() =>
        await AssertDroppedUnitIsFailClosedAsync(
            "\u001b["u8.ToArray(),
            "31m"u8.ToArray(),
            "ERROR: operation failed.\r\n"u8.ToArray());

    [TestMethod]
    public async Task Utf8MiddleLoss_DiscardsAffectedUnitAndRecoversAtNextBoundary()
    {
        byte[] character = Encoding.UTF8.GetBytes("中");
        await AssertDroppedUnitIsFailClosedAsync(
            character[..1],
            character[1..],
            "The operation failed.\r\n"u8.ToArray());
    }

    [TestMethod]
    public async Task RedrawMiddleLoss_DiscardsAffectedUnitAndRecoversAtNextBoundary() =>
        await AssertDroppedUnitIsFailClosedAsync(
            "The old operation failed.\r\u001b["u8.ToArray(),
            "2K"u8.ToArray(),
            "The new operation completed successfully.\r\n"u8.ToArray());

    [TestMethod]
    public async Task SecretPrefixLoss_DiscardsAffectedUnitBeforePrivacyAndProvider() =>
        await AssertDroppedUnitIsFailClosedAsync(
            "API key is "u8.ToArray(),
            "sk-"u8.ToArray(),
            "probeSecretValue1234567890\r\n"u8.ToArray(),
            new SecretDetector());

    [TestMethod]
    public async Task SecretMiddleLoss_DiscardsAffectedUnitBeforePrivacyAndProvider() =>
        await AssertDroppedUnitIsFailClosedAsync(
            "API key is sk-"u8.ToArray(),
            "probeSecretValue1234"u8.ToArray(),
            "567890\r\n"u8.ToArray(),
            new SecretDetector());

    [TestMethod]
    public async Task CompleteSecretsBeforeAndAfterRecoveryNeverReachProvider()
    {
        RecordingProvider provider = new();
        BlockingTracker tracker = new(blockOnCall: 4);
        await using ProductionTranslationPipeline pipeline = CreatePipeline(
            provider, tracker, analysisCapacity: 1, secretDetector: new SecretDetector());
        pipeline.Enable();

        Assert.IsTrue(pipeline.TryOffer("API key is sk-beforeLossSecret1234567890\r\n"u8.ToArray()));
        await Task.Delay(180);
        Assert.AreEqual(0, provider.Sources.Count);

        Assert.IsTrue(pipeline.TryOffer("\u001b[0m"u8.ToArray()));
        await tracker.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(pipeline.TryOffer("affected prefix "u8.ToArray()));
        Assert.IsFalse(pipeline.TryOffer("dropped middle"u8.ToArray()));
        tracker.Release.TrySetResult();
        await OfferEventuallyAsync(
            pipeline,
            "affected suffix is discarded.\r\n"u8.ToArray(),
            TimeSpan.FromSeconds(2));
        await Task.Delay(180);

        Assert.IsTrue(pipeline.TryOffer("API key is sk-afterResetSecret1234567890\r\n"u8.ToArray()));
        await Task.Delay(180);
        Assert.AreEqual(0, provider.Sources.Count);
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(RecoveryText + "\r\n")));
        await WaitUntilAsync(() => provider.Sources.Count > 0, TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { RecoveryText }, provider.Sources.ToArray());
    }

    [TestMethod]
    public async Task DefaultCapacity_SixtyFifthQueuedChunkIsDetectedWithoutChangingProgramPaneBytes()
    {
        RecordingProvider provider = new();
        BlockingTracker tracker = new(blockOnCall: 3);
        await using ProductionTranslationPipeline pipeline = CreatePipeline(
            provider, tracker, analysisCapacity: 64, secretDetector: new SecretDetector());
        pipeline.Enable();
        Assert.IsTrue(pipeline.TryOffer("\u001b[0m"u8.ToArray()));
        await tracker.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        List<byte[]> chunks = [];
        for (int index = 0; index < 65; index++)
        {
            chunks.Add(Encoding.UTF8.GetBytes($"fragment-{index:D2} "));
        }

        byte[] expectedRaw = chunks.SelectMany(static chunk => chunk).ToArray();
        await using MemoryStream programPane = new();
        RejectionRecordingSink analysis = new(pipeline);
        await new ConsoleOutputRelay(new ChunkedReadStream(chunks), programPane, analysis)
            .CopyAsync(CancellationToken.None);
        CollectionAssert.AreEqual(expectedRaw, programPane.ToArray());
        Assert.AreEqual(1, analysis.RejectedCount);

        tracker.Release.TrySetResult();
        await OfferEventuallyAsync(
            pipeline,
            "affected suffix must be discarded.\r\n"u8.ToArray(),
            TimeSpan.FromSeconds(2));
        await Task.Delay(180);
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(RecoveryText + "\r\n")));
        await WaitUntilAsync(() => provider.Sources.Count > 0, TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(new[] { RecoveryText }, provider.Sources.ToArray());
    }

    private static async Task AssertDroppedUnitIsFailClosedAsync(
        byte[] retainedPrefix,
        byte[] droppedMiddle,
        byte[] affectedSuffix,
        SecretDetector? secretDetector = null)
    {
        RecordingProvider provider = new();
        BlockingTracker tracker = new(blockOnCall: 3);
        await using ProductionTranslationPipeline pipeline = CreatePipeline(
            provider, tracker, analysisCapacity: 1, secretDetector);
        pipeline.Enable();
        Assert.IsTrue(pipeline.TryOffer("\u001b[0m"u8.ToArray()));
        await tracker.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(pipeline.TryOffer(retainedPrefix));
        bool acceptedMiddle = pipeline.TryOffer(droppedMiddle);
        tracker.Release.TrySetResult();
        await OfferEventuallyAsync(pipeline, affectedSuffix, TimeSpan.FromSeconds(2));
        await Task.Delay(180);
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(RecoveryText + "\r\n")));
        await WaitUntilAsync(() => provider.Sources.Count > 0, TimeSpan.FromSeconds(2));

        Assert.IsFalse(acceptedMiddle, "A rejected raw chunk must be reported so parser recovery can fail closed.");
        CollectionAssert.AreEqual(new[] { RecoveryText }, provider.Sources.ToArray());
    }

    private static ProductionTranslationPipeline CreatePipeline(
        ITranslationProvider provider,
        ISubmittedCommandTracker tracker,
        int analysisCapacity,
        SecretDetector? secretDetector)
    {
        SystemClock clock = new();
        TranslationWorkQueue queue = new(clock);
        TranslationCoordinator coordinator = new(
            provider,
            new NullSink(),
            clock,
            TimeSpan.FromSeconds(2),
            secretDetector: secretDetector);
        TranslationWorker worker = new(queue, coordinator);
        return new ProductionTranslationPipeline(
            Guid.NewGuid(),
            new VtTextExtractor(),
            new EnglishCandidateClassifier(),
            clock,
            worker,
            analysisCapacity: analysisCapacity,
            submittedCommandTracker: tracker,
            secretDetector: secretDetector);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < timeout)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), "Timed out waiting for the recovery provider request.");
    }

    private static async Task OfferEventuallyAsync(
        ProductionTranslationPipeline pipeline,
        byte[] bytes,
        TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!pipeline.TryOffer(bytes) && watch.Elapsed < timeout)
        {
            await Task.Delay(5);
        }

        Assert.IsTrue(watch.Elapsed < timeout, "Timed out waiting for analysis capacity.");
    }

    private sealed class RecordingProvider : ITranslationProvider
    {
        public ConcurrentQueue<string> Sources { get; } = new();

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            Sources.Enqueue(request.SourceText);
            return Task.FromResult(new TranslationResult("translation"));
        }
    }

    private sealed class NullSink : ITranslationEventSink
    {
        public ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class RejectionRecordingSink(INonBlockingAnalysisSink inner) : INonBlockingAnalysisSink
    {
        private int _rejectedCount;
        public bool IsEnabled => inner.IsEnabled;
        public bool IsObserving => inner.IsObserving;
        public int RejectedCount => Volatile.Read(ref _rejectedCount);

        public bool TryOffer(ReadOnlyMemory<byte> bytes) => TryObserve(bytes);

        public bool TryObserve(ReadOnlyMemory<byte> bytes)
        {
            bool accepted = inner.TryObserve(bytes);
            if (!accepted)
            {
                Interlocked.Increment(ref _rejectedCount);
            }

            return accepted;
        }
    }

    private sealed class BlockingTracker(int blockOnCall) : ISubmittedCommandTracker
    {
        private int _callCount;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BeginObservationEpoch(long epoch) => BeginObservationEpoch(epoch, false);

        public void BeginObservationEpoch(long epoch, bool claimDormantEnableControl)
        {
            int call = Interlocked.Increment(ref _callCount);
            if (call == blockOnCall)
            {
                Blocked.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
            }
        }

        public bool Observe(long epoch, ReadOnlyMemory<byte> bytes) => true;
        public bool ObserveDormantInput(long epoch, ReadOnlyMemory<byte> bytes) => true;
        public AnalysisLineDisposition ClassifyAnalysisLine(long epoch, string line) =>
            AnalysisLineDisposition.ProgramOutput;

    }

    private sealed class ChunkedReadStream(IReadOnlyList<byte[]> chunks) : Stream
    {
        private int _index;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_index >= chunks.Count)
            {
                return ValueTask.FromResult(0);
            }

            byte[] chunk = chunks[_index++];
            chunk.CopyTo(buffer);
            return ValueTask.FromResult(chunk.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
