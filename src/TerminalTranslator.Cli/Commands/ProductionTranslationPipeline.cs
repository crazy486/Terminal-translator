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
    private readonly Func<TranslationErrorCode, CancellationToken, ValueTask>? _providerErrorSink;
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
        Func<TranslationErrorCode, CancellationToken, ValueTask>? providerErrorSink = null,
        int analysisCapacity = 64)
    {
        _sessionId = sessionId;
        _extractor = extractor;
        _classifier = classifier;
        _coordinator = coordinator;
        _providerErrorSink = providerErrorSink;
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

    public void Resize(int viewportColumns) => _extractor.Resize(viewportColumns);

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
            while (await _analysis.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_analysis.Reader.TryRead(out byte[]? bytes))
                {
                    await ProcessBytesAsync(bytes, cancellationToken).ConfigureAwait(false);
                }

                while (true)
                {
                    using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idle.CancelAfter(TimeSpan.FromMilliseconds(120));
                    try
                    {
                        byte[] bytes = await _analysis.Reader.ReadAsync(idle.Token).ConfigureAwait(false);
                        await ProcessBytesAsync(bytes, cancellationToken).ConfigureAwait(false);
                        while (_analysis.Reader.TryRead(out byte[]? queuedBytes))
                        {
                            await ProcessBytesAsync(queuedBytes, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        await TranslateAsync(_extractor.FlushIdle(), cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (ChannelClosedException)
                    {
                        await TranslateAsync(_extractor.Flush(), cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
            }

            await TranslateAsync(_extractor.Flush(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        if (IsEnabled)
        {
            await TranslateAsync(_extractor.Feed(bytes), cancellationToken).ConfigureAwait(false);
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
            catch (TranslationProviderException exception)
            {
                if (_providerErrorSink is not null)
                {
                    await _providerErrorSink(exception.Code, cancellationToken).ConfigureAwait(false);
                }
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
