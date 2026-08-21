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

    public TranslationCoordinator(
        ITranslationProvider provider,
        ITranslationEventSink eventSink,
        IClock clock,
        TimeSpan providerTimeout,
        SecretDetector? secretDetector = null,
        TranslationSession? session = null,
        string? providerFingerprint = null,
        Func<PrivacyDecision, CancellationToken, ValueTask>? privacyDecisionSink = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _providerTimeout = providerTimeout;
        _secretDetector = secretDetector;
        _session = session;
        _providerFingerprint = providerFingerprint;
        _privacyDecisionSink = privacyDecisionSink;
    }

    public async Task<TranslationItem?> TranslateAsync(OutputSegment segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        cancellationToken.ThrowIfCancellationRequested();

        if (_secretDetector is not null)
        {
            PrivacyDecision privacy = _secretDetector.Screen(segment.Sequence, segment.NormalizedText);
            if (privacy.Outcome == PrivacyOutcome.Skip)
            {
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

        Task<TranslationResult> providerTask;
        try
        {
            providerTask = _provider.TranslateAsync(request, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && generationToken.IsCancellationRequested)
        {
            return null;
        }

        TranslationResult result;
        try
        {
            result = await providerTask.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && generationToken.IsCancellationRequested)
        {
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
            ObserveDetached(providerTask);
            throw;
        }

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

    private static void ObserveDetached(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
