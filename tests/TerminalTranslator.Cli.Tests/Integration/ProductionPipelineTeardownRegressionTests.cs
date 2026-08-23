using System.Collections.Concurrent;
using System.Diagnostics;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class ProductionPipelineTeardownRegressionTests
{
    private const string ProviderFingerprint = "teardown-regression-provider";

    [TestMethod]
    public async Task CancelAwareProvider_FullShellExitTeardownCompletesWithoutFault()
    {
        CancelAwareProvider provider = new();
        TeardownResult result = await RunFullTeardownAsync(provider);

        Assert.IsTrue(provider.CancellationObserved.Task.IsCompleted);
        Assert.IsTrue(result.CompletedWithinBound);
        Assert.IsNull(result.DisposeException);
        Assert.AreEqual(23, result.ExitCode);
        Assert.AreEqual(SessionState.Ended, result.SessionState);
        Assert.AreEqual(0, result.PublishedTranslations);
    }

    [TestMethod]
    public async Task CancellationIgnoringProvider_FullShellExitTeardownDoesNotWaitForProviderRelease()
    {
        IgnoringProvider provider = new();
        TeardownResult result = await RunFullTeardownAsync(provider);

        Assert.IsTrue(provider.CancellationObserved.Task.IsCompleted);
        Assert.IsTrue(result.CompletedWithinBound);
        Assert.IsNull(result.DisposeException);
        Assert.IsFalse(result.ProviderWasReleasedBeforeDisposeCompleted);
        Assert.AreEqual(23, result.ExitCode);
        Assert.AreEqual(SessionState.Ended, result.SessionState);
        Assert.AreEqual(0, result.PublishedTranslations);
    }

    private static async Task<TeardownResult> RunFullTeardownAsync(ControlledProvider provider)
    {
        SystemClock clock = new();
        using TranslationSession session = new(Guid.NewGuid(), clock);
        session.Start();
        session.Enable(ProviderFingerprint, consent: true);
        RecordingSink sink = new();
        ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            session.SessionId,
            provider,
            sink,
            TimeSpan.FromSeconds(30),
            session: session,
            providerFingerprint: ProviderFingerprint);
        Assert.IsTrue(pipeline.TryOffer("Translation remains active while the shell exits.\r\n"u8.ToArray()));
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        List<StatusEvent> statusEvents = [];
        SessionTeardown teardown = new(session, clock);
        await teardown.ExecuteAsync(
            "shell-exit",
            exitCode: 23,
            abnormal: false,
            _ => Task.CompletedTask,
            (status, _) =>
            {
                statusEvents.Add(status);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        await provider.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Stopwatch stopwatch = Stopwatch.StartNew();
        Task dispose = pipeline.DisposeAsync().AsTask();
        Task winner = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromMilliseconds(500)));
        bool completedWithinBound = winner == dispose;
        bool providerReleaseWasRequired = !completedWithinBound;
        Exception? disposeException = null;
        if (completedWithinBound)
        {
            try
            {
                await dispose;
            }
            catch (Exception exception)
            {
                disposeException = exception;
            }
        }

        provider.Release();
        try
        {
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (disposeException is null)
        {
            disposeException = exception;
        }

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
        return new TeardownResult(
            completedWithinBound,
            disposeException,
            ProviderWasReleasedBeforeDisposeCompleted: providerReleaseWasRequired,
            statusEvents.Single().Count,
            session.State,
            sink.Items.Count);
    }

    private abstract class ControlledProvider : ITranslationProvider
    {
        protected readonly TaskCompletionSource<TranslationResult> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Released { get; private set; }

        public abstract Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken);

        public void Release()
        {
            Released = true;
            Completion.TrySetResult(new TranslationResult("late translation"));
        }
    }

    private sealed class CancelAwareProvider : ControlledProvider
    {
        public override async Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                return await Completion.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class IgnoringProvider : ControlledProvider
    {
        public override Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            _ = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            return Completion.Task;
        }
    }

    private sealed class RecordingSink : ITranslationEventSink
    {
        public ConcurrentQueue<TranslationItem> Items { get; } = new();

        public ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken)
        {
            Items.Enqueue(item);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record TeardownResult(
        bool CompletedWithinBound,
        Exception? DisposeException,
        bool ProviderWasReleasedBeforeDisposeCompleted,
        int? ExitCode,
        SessionState SessionState,
        int PublishedTranslations);
}
