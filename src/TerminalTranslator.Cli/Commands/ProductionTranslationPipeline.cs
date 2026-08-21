using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Parsing;
using TerminalTranslator.Core.Privacy;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Windows.Console;

namespace TerminalTranslator.Cli.Commands;

public sealed partial class ProductionTranslationPipeline : INonBlockingAnalysisSink, IAsyncDisposable
{
    private readonly Guid _sessionId;
    private readonly VtTextExtractor _extractor;
    private readonly EnglishCandidateClassifier _classifier;
    private readonly IClock _clock;
    private readonly TranslationWorker _translationWorker;
    private readonly Func<string, bool>? _commandEchoFilter;
    private readonly Func<string, AnalysisLineDisposition>? _analysisLineFilter;
    private readonly SecretDetector? _secretDetector;
    private readonly Func<PrivacyDecision, CancellationToken, ValueTask>? _privacyDecisionSink;
    private readonly Func<int, CancellationToken, ValueTask>? _overloadSink;
    private readonly TranslationSession? _session;
    private readonly string? _providerFingerprint;
    private readonly Channel<AnalysisChunk> _analysis;
    private readonly List<ExtractedText> _pendingExtracted = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private long _sequence;
    private long _processingGeneration = -1;
    private int _pendingExtractedBytes;
    private int _enabled;

    public ProductionTranslationPipeline(
        Guid sessionId,
        VtTextExtractor extractor,
        EnglishCandidateClassifier classifier,
        IClock clock,
        TranslationWorker translationWorker,
        int analysisCapacity = 64,
        Func<string, bool>? commandEchoFilter = null,
        Func<string, AnalysisLineDisposition>? analysisLineFilter = null,
        TranslationSession? session = null,
        string? providerFingerprint = null,
        SecretDetector? secretDetector = null,
        Func<PrivacyDecision, CancellationToken, ValueTask>? privacyDecisionSink = null,
        Func<int, CancellationToken, ValueTask>? overloadSink = null)
    {
        _sessionId = sessionId;
        _extractor = extractor;
        _classifier = classifier;
        _clock = clock;
        _translationWorker = translationWorker;
        _commandEchoFilter = commandEchoFilter;
        _analysisLineFilter = analysisLineFilter;
        _session = session;
        _providerFingerprint = providerFingerprint;
        _secretDetector = secretDetector;
        _privacyDecisionSink = privacyDecisionSink;
        _overloadSink = overloadSink;
        _analysis = Channel.CreateBounded<AnalysisChunk>(new BoundedChannelOptions(analysisCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
        _worker = ProcessAsync(_shutdown.Token);
    }

    public bool IsEnabled => _session?.State == SessionState.Enabled ||
        (_session is null && Volatile.Read(ref _enabled) != 0);

    internal int HighQueued => _translationWorker.HighQueued;

    internal int NormalQueued => _translationWorker.NormalQueued;

    public void Enable()
    {
        if (_session is null)
        {
            Volatile.Write(ref _enabled, 1);
            return;
        }

        _session.Enable(_providerFingerprint ?? "legacy-provider", consent: true);
    }

    public bool Enable(string providerFingerprint, bool consent) =>
        _session?.Enable(providerFingerprint, consent) ?? EnableLegacy(consent);

    public bool Disable()
    {
        if (_session is not null)
        {
            bool disabled = _session.Disable();
            if (disabled)
            {
                _translationWorker.DiscardPending();
            }

            return disabled;
        }

        bool legacyDisabled = Interlocked.Exchange(ref _enabled, 0) != 0;
        if (legacyDisabled)
        {
            _translationWorker.DiscardPending();
        }

        return legacyDisabled;
    }

    public void Resize(int viewportColumns) => _extractor.Resize(viewportColumns);

    public bool TryOffer(ReadOnlyMemory<byte> bytes)
    {
        if (!IsEnabled || bytes.IsEmpty)
        {
            return false;
        }

        long generation = _session?.Generation ?? 1;
        return _analysis.Writer.TryWrite(new AnalysisChunk(generation, bytes.ToArray()));
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _analysis.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_analysis.Reader.TryRead(out AnalysisChunk bytes))
                {
                    await ProcessBytesAsync(bytes, cancellationToken).ConfigureAwait(false);
                }

                while (true)
                {
                    using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idle.CancelAfter(TimeSpan.FromMilliseconds(120));
                    try
                    {
                        AnalysisChunk bytes = await _analysis.Reader.ReadAsync(idle.Token).ConfigureAwait(false);
                        await ProcessBytesAsync(bytes, cancellationToken).ConfigureAwait(false);
                        while (_analysis.Reader.TryRead(out AnalysisChunk queuedBytes))
                        {
                            await ProcessBytesAsync(queuedBytes, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        await BufferExtractedAsync(
                            _extractor.FlushIdle(),
                            _processingGeneration,
                            cancellationToken).ConfigureAwait(false);
                        await FlushBufferedAsync(
                            _processingGeneration,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (ChannelClosedException)
                    {
                        await BufferExtractedAsync(
                            _extractor.Flush(),
                            _processingGeneration,
                            cancellationToken).ConfigureAwait(false);
                        await FlushBufferedAsync(
                            _processingGeneration,
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
            }

            await BufferExtractedAsync(
                _extractor.Flush(),
                _processingGeneration,
                cancellationToken).ConfigureAwait(false);
            await FlushBufferedAsync(
                _processingGeneration,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessBytesAsync(AnalysisChunk chunk, CancellationToken cancellationToken)
    {
        long currentGeneration = _session?.Generation ?? 1;
        if (chunk.Generation != currentGeneration || !IsEnabled)
        {
            return;
        }

        if (_processingGeneration != chunk.Generation)
        {
            _extractor.Reset();
            ClearBuffered();
            _processingGeneration = chunk.Generation;
        }

        await BufferExtractedAsync(
            _extractor.Feed(chunk.Bytes),
            chunk.Generation,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task BufferExtractedAsync(
        IReadOnlyList<ExtractedText> extractedItems,
        long generation,
        CancellationToken cancellationToken)
    {
        foreach (ExtractedText extracted in extractedItems)
        {
            int textBytes = Encoding.UTF8.GetByteCount(extracted.Text);
            int separatorBytes = _pendingExtracted.Count == 0 ? 0 : 1;
            if (_pendingExtracted.Count > 0 &&
                _pendingExtractedBytes + separatorBytes + textBytes > VtTextExtractor.MaximumCandidateBytes)
            {
                await FlushBufferedAsync(generation, cancellationToken).ConfigureAwait(false);
                separatorBytes = 0;
            }

            _pendingExtracted.Add(extracted);
            _pendingExtractedBytes += separatorBytes + textBytes;
        }
    }

    private async Task FlushBufferedAsync(long generation, CancellationToken cancellationToken)
    {
        if (_pendingExtracted.Count == 0)
        {
            return;
        }

        ExtractedText[] extractedItems = _pendingExtracted.ToArray();
        ClearBuffered();
        await TranslateAsync(extractedItems, generation, cancellationToken).ConfigureAwait(false);
    }

    private void ClearBuffered()
    {
        _pendingExtracted.Clear();
        _pendingExtractedBytes = 0;
    }

    private async Task TranslateAsync(
        IReadOnlyList<ExtractedText> extractedItems,
        long generation,
        CancellationToken cancellationToken)
    {
        List<ExtractedText> eligibleGroup = [];
        int eligibleGroupBytes = 0;
        foreach (ExtractedText extracted in extractedItems)
        {
            if (generation < 0 ||
                generation != (_session?.Generation ?? 1) ||
                !IsEnabled)
            {
                return;
            }

            ExtractedText? candidate = RemovePowerShellAnalysisArtifacts(extracted);
            if (candidate is null)
            {
                continue;
            }

            CandidateClassification classification = _classifier.Classify(
                candidate.Text,
                candidate.Layout,
                candidate.Boundary);
            if (!classification.IsEligible)
            {
                await FlushEligibleGroupAsync(
                    eligibleGroup,
                    generation,
                    cancellationToken).ConfigureAwait(false);
                eligibleGroupBytes = 0;
                continue;
            }

            int candidateBytes = Encoding.UTF8.GetByteCount(candidate.Text);
            int separatorBytes = eligibleGroup.Count == 0 ? 0 : 1;
            if (eligibleGroup.Count > 0 &&
                eligibleGroupBytes + separatorBytes + candidateBytes > VtTextExtractor.MaximumCandidateBytes)
            {
                await FlushEligibleGroupAsync(
                    eligibleGroup,
                    generation,
                    cancellationToken).ConfigureAwait(false);
                eligibleGroupBytes = 0;
                separatorBytes = 0;
            }

            eligibleGroup.Add(candidate);
            eligibleGroupBytes += separatorBytes + candidateBytes;
        }

        await FlushEligibleGroupAsync(
            eligibleGroup,
            generation,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushEligibleGroupAsync(
        List<ExtractedText> eligibleGroup,
        long generation,
        CancellationToken cancellationToken)
    {
        if (eligibleGroup.Count == 0)
        {
            return;
        }

        if (generation < 0 ||
            generation != (_session?.Generation ?? 1) ||
            !IsEnabled)
        {
            eligibleGroup.Clear();
            return;
        }

        ExtractedText candidate = Combine(eligibleGroup);
        eligibleGroup.Clear();
        ulong sequence = unchecked((ulong)Interlocked.Increment(ref _sequence));
        if (_secretDetector is not null)
        {
            PrivacyDecision privacy = _secretDetector.Screen(sequence, candidate.Text);
            if (privacy.Outcome == PrivacyOutcome.Skip)
            {
                _session?.RecordPrivacySkip();
                if (_privacyDecisionSink is not null)
                {
                    await _privacyDecisionSink(privacy, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
        }

        CandidateClassification classification = _classifier.Classify(
            candidate.Text,
            candidate.Layout,
            candidate.Boundary);
        OutputSegment segment = new(
            _sessionId,
            generation,
            sequence,
            candidate.Text,
            candidate.Layout,
            candidate.Boundary,
            classification.Priority,
            classification.DeduplicationKey,
            _clock.MonotonicNow);
        TranslationWorkOfferResult offer = _translationWorker.Offer(segment);
        if (offer is TranslationWorkOfferResult.DroppedCapacity or
            TranslationWorkOfferResult.DroppedTextBudget)
        {
            _session?.RecordOverloadDrop();
            if (_overloadSink is not null)
            {
                await _overloadSink(1, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static ExtractedText Combine(IReadOnlyList<ExtractedText> items)
    {
        if (items.Count == 1)
        {
            return items[0];
        }

        return new ExtractedText(
            string.Join('\n', items.Select(item => item.Text)),
            new LayoutHints(
                items.Sum(item => item.Layout.LineCount),
                items.SelectMany(item => item.Layout.Indent).ToArray()),
            SourceBoundary.Block);
    }

    private ExtractedText? RemovePowerShellAnalysisArtifacts(ExtractedText extracted)
    {
        string[] lines = extracted.Text.ReplaceLineEndings("\n").Split('\n');
        List<string> retainedLines = new(lines.Length);
        List<int> retainedIndent = new(lines.Length);

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (_analysisLineFilter is not null)
            {
                if (_analysisLineFilter(line) != AnalysisLineDisposition.ProgramOutput)
                {
                    continue;
                }

                retainedLines.Add(line);
                retainedIndent.Add(index < extracted.Layout.Indent.Count
                    ? extracted.Layout.Indent[index]
                    : CountIndent(line));
                continue;
            }

            if (PowerShellPromptRegex().IsMatch(line) || ContinuationPromptRegex().IsMatch(line))
            {
                continue;
            }

            if (LooksLikePowerShellEcho(line) && _commandEchoFilter?.Invoke(line) is true)
            {
                continue;
            }

            retainedLines.Add(line);
            retainedIndent.Add(index < extracted.Layout.Indent.Count
                ? extracted.Layout.Indent[index]
                : CountIndent(line));
        }

        if (retainedLines.Count == 0)
        {
            return null;
        }

        string text = string.Join('\n', retainedLines);
        if (_analysisLineFilter is null && _commandEchoFilter?.Invoke(text) is true)
        {
            return null;
        }

        SourceBoundary boundary = retainedLines.Count > 1
            ? SourceBoundary.Block
            : extracted.Boundary == SourceBoundary.IdlePrompt && lines.Length == 1
                ? SourceBoundary.IdlePrompt
                : SourceBoundary.Line;
        return new ExtractedText(text, new LayoutHints(retainedLines.Count, retainedIndent), boundary);
    }

    private static bool LooksLikePowerShellEcho(string line) =>
        PowerShellPromptWithTailRegex().IsMatch(line) || ContinuationEchoPrefixRegex().IsMatch(line);

    private static int CountIndent(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    [GeneratedRegex(
        @"^\s*PS\s+(?:(?:[A-Za-z]:\\|\\\\|/)|(?:Microsoft\.PowerShell\.Core\\FileSystem::(?:[A-Za-z]:\\|\\\\))).*>\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellPromptRegex();

    [GeneratedRegex(@"^\s*>>\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ContinuationPromptRegex();

    [GeneratedRegex(
        @"^\s*PS\s+(?:(?:[A-Za-z]:\\|\\\\|/)|(?:Microsoft\.PowerShell\.Core\\FileSystem::(?:[A-Za-z]:\\|\\\\))).*>\s*\S",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellPromptWithTailRegex();

    [GeneratedRegex(@"^\s*>+\s+\S", RegexOptions.CultureInvariant)]
    private static partial Regex ContinuationEchoPrefixRegex();

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
            try
            {
                await _translationWorker.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _shutdown.Dispose();
            }
        }
    }

    private bool EnableLegacy(bool consent)
    {
        if (!consent)
        {
            return false;
        }

        return Interlocked.Exchange(ref _enabled, 1) == 0;
    }

    private readonly record struct AnalysisChunk(long Generation, byte[] Bytes);
}
