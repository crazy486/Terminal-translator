using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Sessions;

namespace TerminalTranslator.Core.Translation;

public sealed class TranslationWorker : IAsyncDisposable
{
    private readonly TranslationWorkQueue _queue;
    private readonly TranslationCoordinator _coordinator;
    private readonly TranslationSession? _session;
    private readonly Func<TranslationErrorCode, CancellationToken, ValueTask>? _providerErrorSink;
    private readonly object _generationGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _generationCancellation = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Task _runTask;
    private int _wakePending;
    private int _stopping;

    public TranslationWorker(
        TranslationWorkQueue queue,
        TranslationCoordinator coordinator,
        TranslationSession? session = null,
        Func<TranslationErrorCode, CancellationToken, ValueTask>? providerErrorSink = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _session = session;
        _providerErrorSink = providerErrorSink;
        _runTask = RunAsync(_shutdown.Token);
    }

    public TranslationWorkOfferResult Offer(OutputSegment segment)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return TranslationWorkOfferResult.DroppedCapacity;
        }

        TranslationWorkOfferResult result = _queue.Offer(segment);
        if (result is TranslationWorkOfferResult.Accepted or
            TranslationWorkOfferResult.Replaced or
            TranslationWorkOfferResult.AcceptedWithEviction)
        {
            Signal();
        }

        return result;
    }

    public int HighQueued => _queue.HighCount;

    public int NormalQueued => _queue.NormalCount;

    public void DiscardPending()
    {
        _queue.Clear();
        lock (_generationGate)
        {
            _generationCancellation.Cancel();
            _generationCancellation.Dispose();
            _generationCancellation = new CancellationTokenSource();
        }

        Signal();
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0)
        {
            _queue.Clear();
            _shutdown.Cancel();
            lock (_generationGate)
            {
                _generationCancellation.Cancel();
            }

            Signal();
        }

        await _runTask.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _wakePending, 0);
                while (_queue.TryTake(out OutputSegment? segment))
                {
                    if (segment is null || !IsCurrent(segment))
                    {
                        continue;
                    }

                    try
                    {
                        using CancellationTokenSource workCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                cancellationToken,
                                GetGenerationToken());
                        await _coordinator.TranslateAsync(segment, workCancellation.Token).ConfigureAwait(false);
                    }
                    catch (TranslationProviderException exception)
                    {
                        await PublishProviderFailureAsync(exception.Code, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                    {
                        await PublishProviderFailureAsync(
                            TranslationErrorCode.Unavailable,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool IsCurrent(OutputSegment segment) =>
        _session is null ||
        (_session.Generation == segment.Generation && _session.State == SessionState.Enabled);

    private CancellationToken GetGenerationToken()
    {
        lock (_generationGate)
        {
            return _generationCancellation.Token;
        }
    }

    private async ValueTask PublishProviderFailureAsync(
        TranslationErrorCode code,
        CancellationToken cancellationToken)
    {
        if (_providerErrorSink is null)
        {
            return;
        }

        try
        {
            await _providerErrorSink(code, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void Signal()
    {
        if (Interlocked.Exchange(ref _wakePending, 1) == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_generationGate)
        {
            _generationCancellation.Dispose();
        }

        _shutdown.Dispose();
        _wake.Dispose();
    }
}
