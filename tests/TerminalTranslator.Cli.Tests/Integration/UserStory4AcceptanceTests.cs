using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Windows.Console;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class UserStory4AcceptanceTests
{
    private const string ProviderFingerprint = "acceptance-fake-provider";

    [TestMethod]
    public async Task SustainedOutput_FailuresRemainAnOptionalBoundedSidePath()
    {
        const int lineCount = 12_000;
        Guid sessionId = Guid.NewGuid();
        ManualClock statusClock = new();
        using TranslationSession session = CreateEnabledSession(sessionId, statusClock);
        ControlledProvider provider = new();
        ReconnectableEventSink companion = new() { IsAvailable = false };
        StatusAggregator statusAggregator = new(sessionId, statusClock);
        ConcurrentQueue<StatusEvent> overloadNotices = new();
        await using ProductionTranslationPipeline pipeline =
            ProductionRuntimeComposition.CreateTranslationPipeline(
                sessionId,
                provider,
                companion,
                TimeSpan.FromSeconds(30),
                session: session,
                providerFingerprint: ProviderFingerprint,
                overloadSink: (count, _) =>
                {
                    StatusEvent? notice = statusAggregator.RecordOverload(count);
                    if (notice is not null)
                    {
                        overloadNotices.Enqueue(notice);
                    }

                    return ValueTask.CompletedTask;
                });

        byte[] sustainedOutput = CreateSustainedOutput(lineCount);
        await using MemoryStream programPane = new();
        Task rawDrain = new ConsoleOutputRelay(
            new MemoryStream(sustainedOutput),
            programPane,
            pipeline).CopyAsync(CancellationToken.None);

        await rawDrain.WaitAsync(TimeSpan.FromSeconds(3));
        ProviderCall firstCall = await provider.ReadCallAsync(TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(
            sustainedOutput,
            programPane.ToArray(),
            "SC-004 requires every raw byte to reach the program pane unchanged.");
        Assert.AreEqual(1, firstCall.Number);
        Assert.IsFalse(firstCall.Completion.IsCompleted, "The first fake provider request must remain slow.");
        Assert.IsTrue(
            overloadNotices.Count <= 1,
            "A sustained overload in one five-second window must not produce unbounded notices.");
        foreach (StatusEvent notice in overloadNotices)
        {
            Assert.AreEqual(StatusKind.Degraded, notice.Kind);
            Assert.AreEqual("overload", notice.Code);
        }

        byte[] shellInput = Encoding.UTF8.GetBytes("Write-Output 'shell remains responsive'\r\n");
        await using MemoryStream childInput = new();
        await new ConsoleInputRelay(new MemoryStream(shellInput), childInput)
            .CopyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        CollectionAssert.AreEqual(shellInput, childInput.ToArray());

        Assert.IsTrue(pipeline.Disable());
        Assert.IsTrue(pipeline.Enable(ProviderFingerprint, consent: true));
        provider.CompleteFirst(new TranslationResult("过期结果不得显示。", "old-generation"));

        byte[] unavailableOutput = Encoding.UTF8.GetBytes(
            "ERROR: The companion is unavailable but the shell must continue.\r\n");
        await AssertRawOutputUnchangedAsync(unavailableOutput, pipeline);
        ProviderCall unavailableCall = await provider.ReadCallAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, unavailableCall.Number);
        await companion.WaitForAttemptAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, companion.Items.Count);

        companion.IsAvailable = true;
        byte[] recoveredOutput = Encoding.UTF8.GetBytes(
            "ERROR: Translation resumes after the companion reconnects.\r\n");
        await AssertRawOutputUnchangedAsync(recoveredOutput, pipeline);
        ProviderCall recoveredCall = await provider.ReadCallAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(3, recoveredCall.Number);
        TranslationItem recovered = await companion.WaitForPublishedAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(session.Generation, recovered.Generation);
        Assert.AreEqual(recoveredCall.Request.SourceText, recovered.SourceText);
        Assert.AreEqual("可控的中文翻译。", recovered.TranslatedText);
        Assert.AreEqual(1, companion.Items.Count);
        Assert.IsFalse(
            childInput.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes("translation")) >= 0,
            "Translation/status data must never be injected into child input.");
    }

    [TestMethod]
    public void SustainedOffers_StayWithinFixedQueueAndMemoryBoundsAndReleaseBudget()
    {
        ManualClock clock = new();
        TranslationWorkQueue queue = new(clock);
        Guid sessionId = Guid.NewGuid();

        Parallel.For(0, 10_000, index =>
        {
            queue.Offer(CreateSegment(
                sessionId,
                (ulong)index,
                $"normal-{index:D5}",
                TranslationPriority.Normal,
                clock.MonotonicNow));
        });

        Assert.AreEqual(0, queue.HighCount);
        Assert.AreEqual(48, queue.NormalCount);
        Assert.IsTrue(queue.RetainedTextBytes <= 256 * 1024);

        queue.Clear();
        Parallel.For(0, 10_000, index =>
        {
            queue.Offer(CreateSegment(
                sessionId,
                (ulong)index,
                $"high-{index:D5}",
                TranslationPriority.High,
                clock.MonotonicNow));
        });

        Assert.AreEqual(16, queue.HighCount);
        Assert.AreEqual(0, queue.NormalCount);
        Assert.IsTrue(queue.RetainedTextBytes <= 256 * 1024);

        queue.Clear();
        string eightKiB = new('n', 8 * 1024);
        for (ulong sequence = 0; sequence < 32; sequence++)
        {
            Assert.AreEqual(
                TranslationWorkOfferResult.Accepted,
                queue.Offer(CreateSegment(
                    sessionId,
                    sequence,
                    eightKiB,
                    TranslationPriority.Normal,
                    clock.MonotonicNow)));
        }

        Assert.AreEqual(32, queue.NormalCount);
        Assert.AreEqual(256 * 1024, queue.RetainedTextBytes);
        Assert.AreEqual(
            TranslationWorkOfferResult.DroppedTextBudget,
            queue.Offer(CreateSegment(
                sessionId,
                100,
                "dropped",
                TranslationPriority.Normal,
                clock.MonotonicNow)));
        Assert.AreEqual(256 * 1024, queue.RetainedTextBytes, "A dropped item must not retain budget.");

        for (ulong sequence = 0; sequence < 16; sequence++)
        {
            Assert.AreEqual(
                TranslationWorkOfferResult.AcceptedWithEviction,
                queue.Offer(CreateSegment(
                    sessionId,
                    200 + sequence,
                    new string('h', 8 * 1024),
                    TranslationPriority.High,
                    clock.MonotonicNow)));
        }

        Assert.AreEqual(16, queue.HighCount);
        Assert.AreEqual(16, queue.NormalCount);
        Assert.AreEqual(256 * 1024, queue.RetainedTextBytes);

        clock.Advance(TimeSpan.FromMilliseconds(1501));
        Assert.AreEqual(0, queue.HighCount);
        Assert.AreEqual(0, queue.NormalCount);
        Assert.AreEqual(0, queue.RetainedTextBytes, "Expired work must release its text budget.");

        Assert.AreEqual(
            TranslationWorkOfferResult.Accepted,
            queue.Offer(CreateSegment(
                sessionId,
                999,
                "new work after expiry",
                TranslationPriority.Normal,
                clock.MonotonicNow)));
        Assert.IsTrue(queue.RetainedTextBytes > 0);
    }

    [TestMethod]
    public void SustainedDegradation_HasFixedFiveSecondContentFreeNoticeBounds()
    {
        const int eventCountPerCategory = 10_000;
        const string terminalSource = "ERROR: private customer terminal source";
        const string translation = "机密翻译文本";
        const string credential = "Authorization: Bearer sk-secret-token";
        const string responseBody = "{\"error\":\"private provider response\"}";
        ManualClock clock = new();
        StatusAggregator aggregator = new(Guid.NewGuid(), clock);
        List<StatusEvent> notices = [];

        for (int index = 0; index < eventCountPerCategory; index++)
        {
            AddIfPresent(notices, aggregator.RecordProviderFailure(
                TranslationErrorCode.Unavailable,
                1,
                TimeSpan.FromMilliseconds(index),
                new InvalidOperationException(
                    $"{terminalSource} {translation} {credential} {responseBody}")));
            AddIfPresent(notices, aggregator.RecordPrivacySkip(PrivacyReasonCode.TokenShape, 1));
            AddIfPresent(notices, aggregator.RecordOverload(1));
        }

        Assert.AreEqual(3, notices.Count, "Each category may emit only once in the first window.");

        clock.Advance(TimeSpan.FromSeconds(5));
        AddIfPresent(notices, aggregator.RecordProviderFailure(
            TranslationErrorCode.Unavailable,
            1,
            null,
            new InvalidOperationException(credential)));
        AddIfPresent(notices, aggregator.RecordPrivacySkip(PrivacyReasonCode.TokenShape, 1));
        AddIfPresent(notices, aggregator.RecordOverload(1));

        Assert.AreEqual(6, notices.Count, "Each category may emit one new aggregate after five seconds.");
        foreach (StatusEvent aggregate in notices.Skip(3))
        {
            Assert.AreEqual(eventCountPerCategory, aggregate.Count);
        }

        foreach (StatusEvent notice in notices)
        {
            string rendered = notice.ToString();
            Assert.IsFalse(rendered.Contains(terminalSource, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(rendered.Contains(translation, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(rendered.Contains(credential, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(rendered.Contains(responseBody, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(rendered.Contains("Authorization", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(rendered.Contains("sk-secret-token", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static TranslationSession CreateEnabledSession(Guid sessionId, IClock clock)
    {
        TranslationSession session = new(sessionId, clock);
        session.Start();
        Assert.IsTrue(session.Enable(ProviderFingerprint, consent: true));
        return session;
    }

    private static byte[] CreateSustainedOutput(int lineCount)
    {
        StringBuilder output = new(lineCount * 96);
        for (int index = 0; index < lineCount; index++)
        {
            output.Append("ERROR: Build item ")
                .Append(index.ToString("D5"))
                .Append(" failed because a required configuration file is missing.\r\n");
        }

        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static async Task AssertRawOutputUnchangedAsync(
        byte[] rawOutput,
        INonBlockingAnalysisSink pipeline)
    {
        await using MemoryStream programOutput = new();
        await new ConsoleOutputRelay(new MemoryStream(rawOutput), programOutput, pipeline)
            .CopyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        CollectionAssert.AreEqual(rawOutput, programOutput.ToArray());
    }

    private static OutputSegment CreateSegment(
        Guid sessionId,
        ulong sequence,
        string text,
        TranslationPriority priority,
        TimeSpan capturedAt) => new(
            sessionId,
            1,
            sequence,
            text,
            new LayoutHints(1, [0]),
            SourceBoundary.Line,
            priority,
            null,
            capturedAt);

    private static void AddIfPresent(List<StatusEvent> notices, StatusEvent? notice)
    {
        if (notice is not null)
        {
            notices.Add(notice);
        }
    }

    private sealed class ManualClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);

        public TimeSpan MonotonicNow { get; private set; }

        public void Advance(TimeSpan duration)
        {
            MonotonicNow += duration;
            UtcNow += duration;
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Advance(delay);
            return Task.CompletedTask;
        }
    }

    private sealed record ProviderCall(
        int Number,
        TranslationRequest Request,
        Task<TranslationResult> Completion);

    private sealed class ControlledProvider : ITranslationProvider
    {
        private readonly Channel<ProviderCall> _calls = Channel.CreateUnbounded<ProviderCall>();
        private readonly TaskCompletionSource<TranslationResult> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            int number = Interlocked.Increment(ref _requestCount);
            Task<TranslationResult> completion = number == 1
                ? _first.Task
                : Task.FromResult(new TranslationResult("可控的中文翻译。", $"fake-{number}"));
            _calls.Writer.TryWrite(new ProviderCall(number, request, completion));
            return completion;
        }

        public void CompleteFirst(TranslationResult result) => _first.TrySetResult(result);

        public async Task<ProviderCall> ReadCallAsync(TimeSpan timeout) =>
            await _calls.Reader.ReadAsync().AsTask().WaitAsync(timeout);
    }

    private sealed class ReconnectableEventSink : ITranslationEventSink
    {
        private readonly object _gate = new();
        private readonly Channel<bool> _attempts = Channel.CreateUnbounded<bool>();
        private readonly Channel<TranslationItem> _published = Channel.CreateUnbounded<TranslationItem>();
        private readonly List<TranslationItem> _items = [];

        public bool IsAvailable { get; set; }

        public IReadOnlyList<TranslationItem> Items
        {
            get { lock (_gate) { return _items.ToArray(); } }
        }

        public ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken)
        {
            _attempts.Writer.TryWrite(true);
            if (!IsAvailable)
            {
                throw new IOException("The acceptance companion is intentionally unavailable.");
            }

            lock (_gate)
            {
                _items.Add(item);
            }

            _published.Writer.TryWrite(item);
            return ValueTask.CompletedTask;
        }

        public async Task WaitForAttemptAsync(TimeSpan timeout) =>
            _ = await _attempts.Reader.ReadAsync().AsTask().WaitAsync(timeout);

        public async Task<TranslationItem> WaitForPublishedAsync(TimeSpan timeout) =>
            await _published.Reader.ReadAsync().AsTask().WaitAsync(timeout);
    }
}
