using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class TranslationRuntimeTimingTests
{
    private const string ProviderFingerprint = "timing-provider";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(15_000)]
    public async Task DefaultTenSecondDeadline_StartsAtProviderCall_AndReportsTrueTimeout()
    {
        ProviderSettings settings = ProviderSettings.Create(
            new Uri("https://provider.example/v1/chat/completions"),
            "timing-model",
            "TT_TIMING_TEST_KEY",
            ProviderSettings.DefaultRequestTimeout,
            requestTimeoutIsDefault: true);
        TestHttpMessageHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertFailedException("The deadline must cancel the HTTP request.");
        });
        using HttpClient httpClient = new(handler);
        ChatCompletionTranslationProvider provider = new(settings, httpClient, _ => "fixture-key");
        SystemClock clock = new();
        RuntimeObserver observer = new();
        TranslationCoordinator coordinator = new(
            provider,
            new NullEventSink(),
            clock,
            settings.RequestTimeout,
            runtimeObserver: observer);
        OutputSegment segment = CreateSegment(clock, sequence: 1);

        TranslationProviderException exception =
            await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
                coordinator.TranslateAsync(segment, CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Timeout, exception.Code);
        TranslationRuntimeEvent failure = observer.Events.Single(item =>
            item.Stage == TranslationRuntimeStage.ProviderFailed && item.Sequence == 1);
        Assert.AreEqual(TranslationCancellationReason.ProviderDeadline, failure.CancelReason);
        Assert.AreEqual(TranslationErrorCode.Timeout, failure.NormalizedProviderError);
        Assert.IsGreaterThanOrEqualTo(9_000d, failure.ProviderElapsed!.Value.TotalMilliseconds);
        Assert.IsLessThan(12_000d, failure.ProviderElapsed.Value.TotalMilliseconds);
        Assert.AreEqual(1, handler.Requests.Count);
        TestContext.WriteLine(FormatTiming("provider-deadline", failure));
    }

    [TestMethod]
    public async Task QueueWaitWithinExpiry_DoesNotConsumeProviderDeadline()
    {
        ManualClock clock = new();
        RuntimeObserver observer = new();
        ControlledProvider provider = new();
        TranslationWorkQueue queue = new(clock, observer);
        TranslationCoordinator coordinator = new(
            provider,
            new NullEventSink(),
            clock,
            TimeSpan.FromSeconds(10),
            runtimeObserver: observer);
        await using TranslationWorker worker = new(
            queue,
            coordinator,
            runtimeObserver: observer,
            runtimeClock: clock);

        Assert.AreEqual(TranslationWorkOfferResult.Accepted, worker.Offer(CreateSegment(clock, 1)));
        ProviderCall first = await provider.ReadCallAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(TranslationWorkOfferResult.Accepted, worker.Offer(CreateSegment(clock, 2)));
        clock.Advance(TimeSpan.FromMilliseconds(1400));
        provider.CompleteFirst();
        ProviderCall second = await provider.ReadCallAsync(TimeSpan.FromSeconds(2));
        TranslationRuntimeEvent dequeued = await observer.WaitForAsync(
            TranslationRuntimeStage.QueueDequeued,
            sequence: 2,
            TimeSpan.FromSeconds(2));

        Assert.AreEqual(1UL, first.Request.SegmentSequence);
        Assert.AreEqual(2UL, second.Request.SegmentSequence);
        Assert.AreEqual(TimeSpan.FromSeconds(10), second.Request.Deadline);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1400), dequeued.QueueAge);
        Assert.IsFalse(observer.Events.Any(item =>
            item.Sequence == 2 && item.NormalizedProviderError == TranslationErrorCode.Timeout));
        TestContext.WriteLine(FormatTiming("queue-within-expiry", dequeued));
    }

    [TestMethod]
    public async Task QueueWaitBeyondExpiry_ExpiresWithoutCallingProvider()
    {
        ManualClock clock = new();
        RuntimeObserver observer = new();
        ControlledProvider provider = new();
        TranslationWorkQueue queue = new(clock, observer);
        TranslationCoordinator coordinator = new(
            provider,
            new NullEventSink(),
            clock,
            TimeSpan.FromSeconds(10),
            runtimeObserver: observer);
        await using TranslationWorker worker = new(
            queue,
            coordinator,
            runtimeObserver: observer,
            runtimeClock: clock);

        worker.Offer(CreateSegment(clock, 1));
        _ = await provider.ReadCallAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(1));
        worker.Offer(CreateSegment(clock, 2));
        clock.Advance(TimeSpan.FromMilliseconds(1501));
        provider.CompleteFirst();
        TranslationRuntimeEvent expired = await observer.WaitForAsync(
            TranslationRuntimeStage.QueueExpired,
            sequence: 2,
            TimeSpan.FromSeconds(2));

        Assert.AreEqual(TimeSpan.FromMilliseconds(1501), expired.QueueAge);
        Assert.AreEqual(TranslationCancellationReason.QueueExpired, expired.CancelReason);
        Assert.AreEqual(1, provider.RequestCount);
        Assert.IsFalse(observer.Events.Any(item =>
            item.Sequence == 2 && item.Stage == TranslationRuntimeStage.ProviderStarted));
        TestContext.WriteLine(FormatTiming("queue-expired", expired));
    }

    [TestMethod]
    public async Task GenerationCancellation_IsObservedAsCancellation_NotProviderTimeout()
    {
        ManualClock clock = new();
        RuntimeObserver observer = new();
        using TranslationSession session = CreateEnabledSession(clock);
        CancelAwareProvider provider = new();
        TranslationCoordinator coordinator = new(
            provider,
            new NullEventSink(),
            clock,
            TimeSpan.FromSeconds(10),
            session: session,
            providerFingerprint: ProviderFingerprint,
            runtimeObserver: observer);
        Task<TranslationItem?> pending = coordinator.TranslateAsync(
            CreateSegment(session, clock, 1),
            CancellationToken.None);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        session.Disable();

        Assert.IsNull(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        TranslationRuntimeEvent canceled = observer.Events.Single(item =>
            item.Sequence == 1 && item.Stage == TranslationRuntimeStage.CancelObserved);
        Assert.AreEqual(TranslationCancellationReason.GenerationChange, canceled.CancelReason);
        Assert.AreEqual(TranslationErrorCode.Canceled, canceled.NormalizedProviderError);
        Assert.IsFalse(observer.Events.Any(item =>
            item.NormalizedProviderError == TranslationErrorCode.Timeout));
        TestContext.WriteLine(FormatTiming("generation-canceled", canceled));
    }

    [TestMethod]
    public async Task WorkerShutdown_RecordsShutdownRequest_WithoutProviderTimeout()
    {
        SystemClock clock = new();
        RuntimeObserver observer = new();
        CancelAwareProvider provider = new();
        TranslationWorkQueue queue = new(clock, observer);
        TranslationCoordinator coordinator = new(
            provider,
            new NullEventSink(),
            clock,
            TimeSpan.FromSeconds(10),
            runtimeObserver: observer);
        await using TranslationWorker worker = new(
            queue,
            coordinator,
            runtimeObserver: observer,
            runtimeClock: clock);
        worker.Offer(CreateSegment(clock, 1));
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        TranslationRuntimeEvent requested = observer.Events.Single(item =>
            item.Sequence == 1 && item.Stage == TranslationRuntimeStage.CancelRequested);
        Assert.AreEqual(TranslationCancellationReason.Shutdown, requested.CancelReason);
        Assert.IsFalse(observer.Events.Any(item =>
            item.NormalizedProviderError == TranslationErrorCode.Timeout));
        TestContext.WriteLine(FormatTiming("shutdown-requested", requested));
    }

    [TestMethod]
    public async Task TranslationDisabled_CancelsLegacyWorkWithoutFaultingWorkerOrReportingTimeout()
    {
        SystemClock clock = new();
        RuntimeObserver observer = new();
        CancelAwareProvider provider = new();
        TranslationWorkQueue queue = new(clock, observer);
        TranslationCoordinator coordinator = new(
            provider,
            new NullEventSink(),
            clock,
            TimeSpan.FromSeconds(10),
            runtimeObserver: observer);
        await using TranslationWorker worker = new(
            queue,
            coordinator,
            runtimeObserver: observer,
            runtimeClock: clock);
        worker.Offer(CreateSegment(clock, 1));
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        worker.DiscardPending();
        await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        TranslationRuntimeEvent observed = observer.Events.Single(item =>
            item.Sequence == 1 &&
            item.Stage == TranslationRuntimeStage.CancelObserved &&
            item.CancelReason == TranslationCancellationReason.TranslationDisabled);
        Assert.AreEqual(TranslationErrorCode.Canceled, observed.NormalizedProviderError);
        Assert.IsFalse(observer.Events.Any(item =>
            item.NormalizedProviderError == TranslationErrorCode.Timeout));
        TestContext.WriteLine(FormatTiming("translation-disabled", observed));
    }

    private static string FormatTiming(string scenario, TranslationRuntimeEvent runtimeEvent) =>
        $"scenario={scenario} sequence={runtimeEvent.Sequence} " +
        $"queueAgeMs={runtimeEvent.QueueAge?.TotalMilliseconds.ToString("0.000") ?? "-"} " +
        $"providerElapsedMs={runtimeEvent.ProviderElapsed?.TotalMilliseconds.ToString("0.000") ?? "-"} " +
        $"totalAgeMs={runtimeEvent.TotalAge?.TotalMilliseconds.ToString("0.000") ?? "-"} " +
        $"cancelReason={runtimeEvent.CancelReason} " +
        $"normalizedProviderError={runtimeEvent.NormalizedProviderError}";

    private static TranslationSession CreateEnabledSession(IClock clock)
    {
        TranslationSession session = new(Guid.NewGuid(), clock);
        session.Start();
        session.Enable(ProviderFingerprint, consent: true);
        return session;
    }

    private static OutputSegment CreateSegment(IClock clock, ulong sequence) => new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        1,
        sequence,
        $"The timing fixture output number {sequence} completed successfully.",
        new LayoutHints(1, [0]),
        SourceBoundary.Line,
        TranslationPriority.Normal,
        null,
        clock.MonotonicNow);

    private static OutputSegment CreateSegment(
        TranslationSession session,
        IClock clock,
        ulong sequence) => new(
        session.SessionId,
        session.Generation,
        sequence,
        "The generation cancellation fixture remains content safe.",
        new LayoutHints(1, [0]),
        SourceBoundary.Line,
        TranslationPriority.Normal,
        null,
        clock.MonotonicNow);

    private sealed class ManualClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);

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

    private sealed class RuntimeObserver : ITranslationRuntimeObserver
    {
        private readonly ConcurrentQueue<TranslationRuntimeEvent> _events = new();
        private readonly SemaphoreSlim _signal = new(0);

        public IReadOnlyList<TranslationRuntimeEvent> Events => _events.ToArray();

        public void Record(TranslationRuntimeEvent runtimeEvent)
        {
            _events.Enqueue(runtimeEvent);
            _signal.Release();
        }

        public async Task<TranslationRuntimeEvent> WaitForAsync(
            TranslationRuntimeStage stage,
            ulong sequence,
            TimeSpan timeout)
        {
            using CancellationTokenSource deadline = new(timeout);
            while (true)
            {
                TranslationRuntimeEvent? match = _events.FirstOrDefault(item =>
                    item.Stage == stage && item.Sequence == sequence);
                if (match is not null)
                {
                    return match;
                }

                await _signal.WaitAsync(deadline.Token);
            }
        }
    }

    private sealed record ProviderCall(int Number, TranslationRequest Request);

    private sealed class ControlledProvider : ITranslationProvider
    {
        private readonly Channel<ProviderCall> _calls = Channel.CreateUnbounded<ProviderCall>();
        private readonly TaskCompletionSource<TranslationResult> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            int number = Interlocked.Increment(ref _requestCount);
            _calls.Writer.TryWrite(new ProviderCall(number, request));
            return number == 1
                ? _first.Task
                : Task.FromResult(new TranslationResult("controlled translation"));
        }

        public void CompleteFirst() =>
            _first.TrySetResult(new TranslationResult("first controlled translation"));

        public async Task<ProviderCall> ReadCallAsync(TimeSpan timeout) =>
            await _calls.Reader.ReadAsync().AsTask().WaitAsync(timeout);
    }

    private sealed class CancelAwareProvider : ITranslationProvider
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertFailedException("Canceled provider work must not complete normally.");
        }
    }

    private sealed class NullEventSink : ITranslationEventSink
    {
        public ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
