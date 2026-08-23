using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Privacy;
using TerminalTranslator.Core.Sessions;
using System.Text;

namespace TerminalTranslator.Core.Translation;

public interface ITranslationEventSink
{
    ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken);
}

public sealed class TranslationCoordinator
{
    private readonly ITranslationProvider _provider;
    private readonly ITranslationEventSink _eventSink;
    private readonly IClock _clock;
    private readonly TimeSpan _providerTimeout;
    private readonly SecretDetector? _secretDetector;
    private readonly TranslationSession? _session;
    private readonly string? _providerFingerprint;
    private readonly Func<PrivacyDecision, CancellationToken, ValueTask>? _privacyDecisionSink;
    private readonly ITranslationRuntimeObserver? _runtimeObserver;

    public TranslationCoordinator(
        ITranslationProvider provider,
        ITranslationEventSink eventSink,
        IClock clock,
        TimeSpan providerTimeout,
        SecretDetector? secretDetector = null,
        TranslationSession? session = null,
        string? providerFingerprint = null,
        Func<PrivacyDecision, CancellationToken, ValueTask>? privacyDecisionSink = null,
        ITranslationRuntimeObserver? runtimeObserver = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _providerTimeout = providerTimeout;
        _secretDetector = secretDetector;
        _session = session;
        _providerFingerprint = providerFingerprint;
        _privacyDecisionSink = privacyDecisionSink;
        _runtimeObserver = runtimeObserver;
    }

    public Task<TranslationItem?> TranslateAsync(
        OutputSegment segment,
        CancellationToken cancellationToken) =>
        TranslateAsync(segment, cancellationToken, callerCancellationReason: null);

    internal async Task<TranslationItem?> TranslateAsync(
        OutputSegment segment,
        CancellationToken cancellationToken,
        Func<TranslationCancellationReason>? callerCancellationReason)
    {
        ArgumentNullException.ThrowIfNull(segment);
        cancellationToken.ThrowIfCancellationRequested();

        if (_secretDetector is not null)
        {
            Record(new TranslationRuntimeEvent(
                TranslationRuntimeStage.PrivacyInvoked,
                _clock.MonotonicNow,
                segment.Sequence,
                segment.Generation,
                CreatedAt: segment.CapturedAt,
                TotalAge: _clock.MonotonicNow - segment.CapturedAt));
            PrivacyDecision privacy = _secretDetector.Screen(segment.Sequence, segment.NormalizedText);
            if (privacy.Outcome == PrivacyOutcome.Skip)
            {
                Record(new TranslationRuntimeEvent(
                    TranslationRuntimeStage.PrivacyRejected,
                    _clock.MonotonicNow,
                    segment.Sequence,
                    segment.Generation,
                    CreatedAt: segment.CapturedAt,
                    TotalAge: _clock.MonotonicNow - segment.CapturedAt));
                _session?.RecordPrivacySkip();
                if (_privacyDecisionSink is not null)
                {
                    await _privacyDecisionSink(privacy, cancellationToken).ConfigureAwait(false);
                }

                return null;
            }
        }

        CancellationToken generationToken = CancellationToken.None;
        if (_session is not null)
        {
            if (string.IsNullOrWhiteSpace(_providerFingerprint) ||
                !_session.IsAuthorized(segment.Generation, _providerFingerprint))
            {
                return null;
            }

            if (!_session.TryTrackSegment(segment))
            {
                return null;
            }

            generationToken = _session.TranslationCancellationToken;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            generationToken);

        TranslationRequest request = new(
            segment.Generation,
            segment.Sequence,
            segment.NormalizedText,
            "en",
            "zh-Hans",
            _providerTimeout);

        TimeSpan providerStartedAt = _clock.MonotonicNow;
        Record(new TranslationRuntimeEvent(
            TranslationRuntimeStage.ProviderStarted,
            providerStartedAt,
            segment.Sequence,
            segment.Generation,
            CreatedAt: segment.CapturedAt,
            ProviderStartedAt: providerStartedAt,
            TotalAge: providerStartedAt - segment.CapturedAt));

        Task<TranslationResult> providerTask;
        try
        {
            providerTask = _provider.TranslateAsync(request, linked.Token);
        }
        catch (TranslationProviderException exception)
        {
            RecordProviderFailure(segment, providerStartedAt, exception.Code);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && generationToken.IsCancellationRequested)
        {
            RecordCancellation(
                segment,
                providerStartedAt,
                TranslationCancellationReason.GenerationChange);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RecordCancellation(
                segment,
                providerStartedAt,
                callerCancellationReason?.Invoke() ?? TranslationCancellationReason.Caller);
            throw;
        }

        TranslationResult result;
        try
        {
            result = await providerTask.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (TranslationProviderException exception)
        {
            RecordProviderFailure(segment, providerStartedAt, exception.Code);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && generationToken.IsCancellationRequested)
        {
            RecordCancellation(
                segment,
                providerStartedAt,
                TranslationCancellationReason.GenerationChange);
            if (_session?.State is SessionState.Stopping or SessionState.Ended or SessionState.Faulted)
            {
                result = await providerTask.ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
                if (!providerTask.IsCompleted)
                {
                    ObserveDetached(providerTask);
                    return null;
                }

                try
                {
                    result = await providerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RecordCancellation(
                segment,
                providerStartedAt,
                callerCancellationReason?.Invoke() ?? TranslationCancellationReason.Caller);
            ObserveDetached(providerTask);
            throw;
        }

        TimeSpan providerCompletedAt = _clock.MonotonicNow;
        Record(new TranslationRuntimeEvent(
            TranslationRuntimeStage.ProviderCompleted,
            providerCompletedAt,
            segment.Sequence,
            segment.Generation,
            CreatedAt: segment.CapturedAt,
            ProviderStartedAt: providerStartedAt,
            ProviderCompletedAt: providerCompletedAt,
            ProviderElapsed: providerCompletedAt - providerStartedAt,
            TotalAge: providerCompletedAt - segment.CapturedAt));

        if (string.IsNullOrWhiteSpace(result.TranslatedText) ||
            Encoding.UTF8.GetByteCount(result.TranslatedText) > 16 * 1024)
        {
            return null;
        }

        if (_session is not null &&
            !_session.IsAuthorized(segment.Generation, _providerFingerprint!))
        {
            return null;
        }

        TranslationItem item = new(
            segment.SessionId,
            segment.Generation,
            segment.Sequence,
            segment.NormalizedText,
            result.TranslatedText,
            segment.Layout,
            result.ProviderRequestId,
            _clock.MonotonicNow);

        Record(new TranslationRuntimeEvent(
            TranslationRuntimeStage.TranslationItemCreated,
            item.CompletedAt,
            segment.Sequence,
            segment.Generation,
            CreatedAt: segment.CapturedAt,
            ProviderStartedAt: providerStartedAt,
            ProviderCompletedAt: providerCompletedAt,
            ProviderElapsed: providerCompletedAt - providerStartedAt,
            TotalAge: item.CompletedAt - segment.CapturedAt));

        if (_session is not null && !_session.TryTrackTranslation(item))
        {
            return null;
        }

        try
        {
            Task publishTask = _eventSink.PublishAsync(item, linked.Token).AsTask();
            await publishTask.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && generationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return null;
        }

        return item;
    }

    private void RecordProviderFailure(
        OutputSegment segment,
        TimeSpan providerStartedAt,
        TranslationErrorCode error)
    {
        TimeSpan observedAt = _clock.MonotonicNow;
        Record(new TranslationRuntimeEvent(
            TranslationRuntimeStage.ProviderFailed,
            observedAt,
            segment.Sequence,
            segment.Generation,
            CreatedAt: segment.CapturedAt,
            ProviderStartedAt: providerStartedAt,
            ProviderCompletedAt: observedAt,
            CancelObservedAt: error == TranslationErrorCode.Timeout ? observedAt : null,
            ProviderElapsed: observedAt - providerStartedAt,
            TotalAge: observedAt - segment.CapturedAt,
            CancelReason: error == TranslationErrorCode.Timeout
                ? TranslationCancellationReason.ProviderDeadline
                : TranslationCancellationReason.None,
            NormalizedProviderError: error));
    }

    private void RecordCancellation(
        OutputSegment segment,
        TimeSpan providerStartedAt,
        TranslationCancellationReason reason)
    {
        TimeSpan observedAt = _clock.MonotonicNow;
        Record(new TranslationRuntimeEvent(
            TranslationRuntimeStage.CancelObserved,
            observedAt,
            segment.Sequence,
            segment.Generation,
            CreatedAt: segment.CapturedAt,
            ProviderStartedAt: providerStartedAt,
            CancelObservedAt: observedAt,
            ProviderElapsed: observedAt - providerStartedAt,
            TotalAge: observedAt - segment.CapturedAt,
            CancelReason: reason,
            NormalizedProviderError: TranslationErrorCode.Canceled));
    }

    private void Record(TranslationRuntimeEvent runtimeEvent)
    {
        try
        {
            _runtimeObserver?.Record(runtimeEvent);
        }
        catch
        {
            // Diagnostics are an optional, content-free side path.
        }
    }

    private static void ObserveDetached(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
