using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Sessions;

namespace TerminalTranslator.Core.Translation;

public interface ITranslationEventSink
{
    ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken);
}

public sealed class TranslationCoordinator(
    ITranslationProvider provider,
    ITranslationEventSink eventSink,
    IClock clock,
    TimeSpan providerTimeout)
{
    public async Task<TranslationItem?> TranslateAsync(OutputSegment segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        cancellationToken.ThrowIfCancellationRequested();

        TranslationRequest request = new(
            segment.Generation,
            segment.Sequence,
            segment.NormalizedText,
            "en",
            "zh-Hans",
            providerTimeout);

        TranslationResult result = await provider.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.TranslatedText))
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
            clock.MonotonicNow);

        await eventSink.PublishAsync(item, cancellationToken).ConfigureAwait(false);
        return item;
    }
}
