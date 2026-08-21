using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Privacy;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Tests.TestDoubles;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Core.Tests.Integration;

[TestClass]
public sealed class SessionLifecycleTests
{
    [TestMethod]
    public async Task DisableUnderLoad_CancelsInFlightAndRejectsNewTransmission()
    {
        FakeClock clock = new();
        using TranslationSession session = CreateEnabledSession(clock);
        CancelAwareProvider provider = new();
        RecordingSink sink = new();
        TranslationCoordinator coordinator = new(
            provider,
            sink,
            clock,
            TimeSpan.FromSeconds(2),
            new SecretDetector(),
            session,
            "provider-a");
        Task<TranslationItem?> inFlight = coordinator.TranslateAsync(
            CreateSegment(session, 1, "The first operation is still running."),
            CancellationToken.None);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        session.Disable();
        TranslationItem? canceled = await inFlight.WaitAsync(TimeSpan.FromSeconds(1));
        TranslationItem? rejected = await coordinator.TranslateAsync(
            CreateSegment(session, 2, "The second operation must stay local."),
            CancellationToken.None);

        Assert.IsNull(canceled);
        Assert.IsNull(rejected);
        Assert.IsTrue(provider.Canceled);
        Assert.AreEqual(1, provider.RequestCount);
        Assert.AreEqual(0, sink.Items.Count);
    }

    [TestMethod]
    public async Task ProviderIgnoringCancellation_OldGenerationResultIsSuppressed()
    {
        FakeClock clock = new();
        using TranslationSession session = CreateEnabledSession(clock);
        DeferredProvider provider = new();
        RecordingSink sink = new();
        TranslationCoordinator coordinator = new(
            provider,
            sink,
            clock,
            TimeSpan.FromSeconds(2),
            new SecretDetector(),
            session,
            "provider-a");
        Task<TranslationItem?> translate = coordinator.TranslateAsync(
            CreateSegment(session, 1, "The operation completed successfully."),
            CancellationToken.None);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        session.Disable();
        provider.Result.SetResult(new TranslationResult("操作已成功完成。"));

        Assert.IsNull(await translate.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(0, sink.Items.Count);
        Assert.AreEqual(0, session.RetainedTranslationCount);
    }

    [TestMethod]
    [DataRow(false, "shell-exit", SessionState.Ended)]
    [DataRow(true, "host-fault", SessionState.Faulted)]
    public async Task Teardown_NormalAndAbnormal_ClearsContentAndPublishesContentFreeEnd(
        bool abnormal,
        string reason,
        SessionState expectedState)
    {
        FakeClock clock = new();
        using TranslationSession session = CreateEnabledSession(clock);
        session.TrackSegment(CreateSegment(session, 1, "Transient source must be cleared."));
        session.TrackTranslation(new TranslationItem(
            session.SessionId,
            session.Generation,
            1,
            "Transient source must be cleared.",
            "临时翻译必须被清除。",
            new LayoutHints(1, [0]),
            null,
            TimeSpan.Zero));
        List<StatusEvent> events = [];
        bool drained = false;
        SessionTeardown teardown = new(session, clock);

        await teardown.ExecuteAsync(
            reason,
            exitCode: abnormal ? null : 23,
            abnormal,
            _ =>
            {
                drained = true;
                return Task.CompletedTask;
            },
            (status, _) =>
            {
                events.Add(status);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.IsTrue(drained);
        Assert.AreEqual(expectedState, session.State);
        Assert.AreEqual(0, session.RetainedSegmentCount);
        Assert.AreEqual(0, session.RetainedTranslationCount);
        Assert.AreEqual(StatusKind.SessionEnded, events.Single().Kind);
        Assert.AreEqual(reason, events.Single().Code);
        Assert.IsFalse(events.Single().ToString().Contains("Transient", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Teardown_CreatesNoContentFiles()
    {
        using TemporaryDirectory temporary = new();
        string[] before = Directory.GetFiles(temporary.Path, "*", SearchOption.AllDirectories);
        FakeClock clock = new();
        using TranslationSession session = CreateEnabledSession(clock);
        session.TrackSegment(CreateSegment(session, 1, "No content may be persisted."));
        SessionTeardown teardown = new(session, clock);

        await teardown.ExecuteAsync(
            "shell-exit",
            0,
            abnormal: false,
            _ => Task.CompletedTask,
            null,
            CancellationToken.None);

        CollectionAssert.AreEqual(before, Directory.GetFiles(temporary.Path, "*", SearchOption.AllDirectories));
    }

    private static TranslationSession CreateEnabledSession(FakeClock clock)
    {
        TranslationSession session = new(Guid.NewGuid(), clock);
        session.Start();
        session.Enable("provider-a", consent: true);
        return session;
    }

    private static OutputSegment CreateSegment(
        TranslationSession session,
        ulong sequence,
        string text) => new(
        session.SessionId,
        session.Generation,
        sequence,
        text,
        new LayoutHints(1, [0]),
        SourceBoundary.Line,
        TranslationPriority.Normal,
        null,
        TimeSpan.Zero);

    private sealed class RecordingSink : ITranslationEventSink
    {
        public List<TranslationItem> Items { get; } = [];

        public ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken)
        {
            Items.Add(item);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelAwareProvider : ITranslationProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount { get; private set; }

        public bool Canceled { get; private set; }

        public async Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new AssertFailedException("Provider unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                Canceled = true;
                throw;
            }
        }
    }

    private sealed class DeferredProvider : ITranslationProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<TranslationResult> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            Started.SetResult();
            return Result.Task;
        }
    }
}
