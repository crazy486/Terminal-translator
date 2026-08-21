using System.Threading.Channels;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Parsing;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Windows.Console;

namespace TerminalTranslator.Cli.Commands;

public sealed class ProductionTranslationPipeline : INonBlockingAnalysisSink, IAsyncDisposable
{
    private readonly Guid _sessionId;
    private readonly VtTextExtractor _extractor;
    private readonly EnglishCandidateClassifier _classifier;
    private readonly TranslationCoordinator _coordinator;
    private readonly Channel<byte[]> _analysis;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private long _sequence;
    private int _enabled;

    public ProductionTranslationPipeline(
        Guid sessionId,
        VtTextExtractor extractor,
        EnglishCandidateClassifier classifier,
        TranslationCoordinator coordinator,
        int analysisCapacity = 64)
    {
        _sessionId = sessionId;
        _extractor = extractor;
        _classifier = classifier;
        _coordinator = coordinator;
        _analysis = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(analysisCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
        _worker = ProcessAsync(_shutdown.Token);
    }

    public bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    public void Enable() => Volatile.Write(ref _enabled, 1);

    public bool TryOffer(ReadOnlyMemory<byte> bytes)
    {
        if (!IsEnabled || bytes.IsEmpty)
        {
            return false;
        }

        return _analysis.Writer.TryWrite(bytes.ToArray());
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (byte[] bytes in _analysis.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!IsEnabled)
                {
                    continue;
                }

                await TranslateAsync(_extractor.Feed(bytes), cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken).ConfigureAwait(false);
                await TranslateAsync(_extractor.FlushIdle(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task TranslateAsync(
        IReadOnlyList<ExtractedText> extractedItems,
        CancellationToken cancellationToken)
    {
        foreach (ExtractedText extracted in extractedItems)
        {
            CandidateClassification classification = _classifier.Classify(
                extracted.Text,
                extracted.Layout,
                extracted.Boundary);
            if (!classification.IsEligible)
            {
                continue;
            }

            OutputSegment segment = new(
                _sessionId,
                1,
                unchecked((ulong)Interlocked.Increment(ref _sequence)),
                extracted.Text,
                extracted.Layout,
                extracted.Boundary,
                classification.Priority,
                classification.DeduplicationKey,
                TimeSpan.Zero);
            try
            {
                await _coordinator.TranslateAsync(segment, cancellationToken).ConfigureAwait(false);
            }
            catch (TranslationProviderException)
            {
                // Provider failure isolation is intentionally content-free. Full aggregation is US4.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _analysis.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }
}
