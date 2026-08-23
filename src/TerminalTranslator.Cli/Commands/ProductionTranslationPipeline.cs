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
    private readonly ISubmittedCommandTracker? _submittedCommandTracker;
    private readonly SecretDetector? _secretDetector;
    private readonly Func<PrivacyDecision, CancellationToken, ValueTask>? _privacyDecisionSink;
    private readonly Func<int, CancellationToken, ValueTask>? _overloadSink;
    private readonly ITranslationRuntimeObserver? _runtimeObserver;
    private readonly TranslationSession? _session;
    private readonly string? _providerFingerprint;
    private readonly Channel<AnalysisChunk> _analysis;
    private readonly List<ExtractedText> _pendingExtracted = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private long _sequence;
    private long _processingGeneration = -1;
    private long _legacyObservationEpoch;
    private bool _processingTranslationEnabled;
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
        ISubmittedCommandTracker? submittedCommandTracker = null,
        TranslationSession? session = null,
        string? providerFingerprint = null,
        SecretDetector? secretDetector = null,
        Func<PrivacyDecision, CancellationToken, ValueTask>? privacyDecisionSink = null,
        Func<int, CancellationToken, ValueTask>? overloadSink = null,
        ITranslationRuntimeObserver? runtimeObserver = null)
    {
        _sessionId = sessionId;
        _extractor = extractor;
        _classifier = classifier;
        _clock = clock;
        _translationWorker = translationWorker;
        _commandEchoFilter = commandEchoFilter;
        _analysisLineFilter = analysisLineFilter;
        _submittedCommandTracker = submittedCommandTracker;
        _session = session;
        _providerFingerprint = providerFingerprint;
        _secretDetector = secretDetector;
        _privacyDecisionSink = privacyDecisionSink;
        _overloadSink = overloadSink;
        _runtimeObserver = runtimeObserver;
        _analysis = Channel.CreateBounded<AnalysisChunk>(new BoundedChannelOptions(analysisCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
        _submittedCommandTracker?.BeginObservationEpoch(_session?.Generation ?? 0);
        _worker = ProcessAsync(_shutdown.Token);
    }

    public bool IsEnabled => _session?.State == SessionState.Enabled ||
        (_session is null && Volatile.Read(ref _enabled) != 0);

    public bool IsObserving => IsEnabled;

    internal int HighQueued => _translationWorker.HighQueued;

    internal int NormalQueued => _translationWorker.NormalQueued;

    public void Enable()
    {
        if (_session is null)
        {
            if (Interlocked.Exchange(ref _enabled, 1) == 0)
            {
                long epoch = Interlocked.Increment(ref _legacyObservationEpoch);
                _submittedCommandTracker?.BeginObservationEpoch(epoch);
            }

            return;
        }

        if (_session.Enable(_providerFingerprint ?? "legacy-provider", consent: true))
        {
            _submittedCommandTracker?.BeginObservationEpoch(_session.Generation);
        }
    }

    public bool Enable(
        string providerFingerprint,
        bool consent,
        bool claimDormantEnableControl = false)
    {
        bool changed = _session?.Enable(providerFingerprint, consent) ?? EnableLegacy(consent);
        if (changed)
        {
            _submittedCommandTracker?.BeginObservationEpoch(
                _session?.Generation ?? Volatile.Read(ref _legacyObservationEpoch),
                claimDormantEnableControl);
        }

        return changed;
    }

    public bool Disable()
    {
        if (_session is not null)
        {
            bool disabled = _session.Disable();
            if (disabled)
            {
                _translationWorker.DiscardPending();
                _submittedCommandTracker?.BeginObservationEpoch(_session.Generation);
            }

            return disabled;
        }

        bool legacyDisabled = Interlocked.Exchange(ref _enabled, 0) != 0;
        if (legacyDisabled)
        {
            _translationWorker.DiscardPending();
            long epoch = Interlocked.Increment(ref _legacyObservationEpoch);
            _submittedCommandTracker?.BeginObservationEpoch(epoch);
        }

        return legacyDisabled;
    }

    public void Resize(int viewportColumns, int? viewportRows = null) =>
        _extractor.Resize(viewportColumns, viewportRows);

    public bool TryOffer(ReadOnlyMemory<byte> bytes)
    {
        AnalysisCaptureState capture = CaptureState();
        if (!capture.TranslationEnabled || bytes.IsEmpty)
        {
            return false;
        }

        return TryOfferCore(bytes, capture);
    }

    public bool TryObserve(ReadOnlyMemory<byte> bytes) => TryOffer(bytes);

    public bool TryObserveSubmittedInput(ReadOnlyMemory<byte> bytes)
    {
        AnalysisCaptureState capture = CaptureState();
        if (bytes.IsEmpty || _submittedCommandTracker is null)
        {
            return false;
        }

        if (!capture.TranslationEnabled)
        {
            _ = _submittedCommandTracker.ObserveDormantInput(capture.Generation, bytes);
            return false;
        }

        return _submittedCommandTracker.Observe(capture.Generation, bytes);
    }

    private bool TryOfferCore(ReadOnlyMemory<byte> bytes, AnalysisCaptureState capture) =>
        _analysis.Writer.TryWrite(new AnalysisChunk(
            capture.Generation,
            capture.TranslationEnabled,
            bytes.ToArray()));

    private AnalysisCaptureState CaptureState()
    {
        if (_session is null)
        {
            return new AnalysisCaptureState(
                Volatile.Read(ref _legacyObservationEpoch),
                Volatile.Read(ref _enabled) != 0);
        }

        long generation = _session.Generation;
        bool enabled = _session.State == SessionState.Enabled;
        if (_session.Generation != generation)
        {
            enabled = false;
        }

        return new AnalysisCaptureState(generation, enabled);
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
                            _processingTranslationEnabled,
                            cancellationToken).ConfigureAwait(false);
                        await FlushBufferedAsync(
                            _processingGeneration,
                            _processingTranslationEnabled,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (ChannelClosedException)
                    {
                        await BufferExtractedAsync(
                            _extractor.Flush(),
                            _processingGeneration,
                            _processingTranslationEnabled,
                            cancellationToken).ConfigureAwait(false);
                        await FlushBufferedAsync(
                            _processingGeneration,
                            _processingTranslationEnabled,
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
            }

            await BufferExtractedAsync(
                _extractor.Flush(),
                _processingGeneration,
                _processingTranslationEnabled,
                cancellationToken).ConfigureAwait(false);
            await FlushBufferedAsync(
                _processingGeneration,
                _processingTranslationEnabled,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessBytesAsync(AnalysisChunk chunk, CancellationToken cancellationToken)
    {
        if (_processingGeneration >= 0 &&
            (chunk.Generation != _processingGeneration ||
             chunk.TranslationEnabled != _processingTranslationEnabled))
        {
            await BufferExtractedAsync(
                _extractor.Flush(),
                _processingGeneration,
                _processingTranslationEnabled,
                cancellationToken).ConfigureAwait(false);
            await FlushBufferedAsync(
                _processingGeneration,
                _processingTranslationEnabled,
                cancellationToken).ConfigureAwait(false);
            // Output produced while disabled never enters this pipeline. A later
            // enabled chunk therefore begins a new observable terminal epoch;
            // cursor/redraw state from the prior generation cannot be reused.
            _extractor.Reset();
        }

        _submittedCommandTracker?.BeginObservationEpoch(chunk.Generation);
        _processingGeneration = chunk.Generation;
        _processingTranslationEnabled = chunk.TranslationEnabled;

        await BufferExtractedAsync(
            _extractor.Feed(chunk.Bytes),
            chunk.Generation,
            chunk.TranslationEnabled,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task BufferExtractedAsync(
        IReadOnlyList<ExtractedText> extractedItems,
        long generation,
        bool translationEnabled,
        CancellationToken cancellationToken)
    {
        foreach (ExtractedText extracted in extractedItems)
        {
            Record(TranslationRuntimeStage.VtUnitEmitted, generation);
            int textBytes = Encoding.UTF8.GetByteCount(extracted.Text);
            int separatorBytes = _pendingExtracted.Count == 0 ? 0 : 1;
            if (_pendingExtracted.Count > 0 &&
                _pendingExtractedBytes + separatorBytes + textBytes > VtTextExtractor.MaximumCandidateBytes)
            {
                await FlushBufferedAsync(
                    generation,
                    translationEnabled,
                    cancellationToken).ConfigureAwait(false);
                separatorBytes = 0;
            }

            _pendingExtracted.Add(extracted);
            _pendingExtractedBytes += separatorBytes + textBytes;
        }
    }

    private async Task FlushBufferedAsync(
        long generation,
        bool translationEnabled,
        CancellationToken cancellationToken)
    {
        if (_pendingExtracted.Count == 0)
        {
            return;
        }

        ExtractedText[] extractedItems = _pendingExtracted.ToArray();
        ClearBuffered();
        await TranslateAsync(
            extractedItems,
            generation,
            translationEnabled,
            cancellationToken).ConfigureAwait(false);
    }

    private void ClearBuffered()
    {
        _pendingExtracted.Clear();
        _pendingExtractedBytes = 0;
    }

    private async Task TranslateAsync(
        IReadOnlyList<ExtractedText> extractedItems,
        long generation,
        bool translationEnabled,
        CancellationToken cancellationToken)
    {
        if (!CanTranslate(generation, translationEnabled))
        {
            return;
        }

        List<ExtractedText> eligibleGroup = [];
        int eligibleGroupBytes = 0;
        foreach (ExtractedText extracted in extractedItems)
        {
            foreach (AnalysisFragment fragment in SplitAtPowerShellSemanticBoundaries(
                         extracted,
                         generation))
            {
                if (fragment.IsHardBoundary)
                {
                    Record(TranslationRuntimeStage.SemanticBoundary, generation);
                    await FlushEligibleGroupAsync(
                        eligibleGroup,
                        generation,
                        translationEnabled,
                        cancellationToken).ConfigureAwait(false);
                    eligibleGroupBytes = 0;
                    continue;
                }

                ExtractedText? candidate = fragment.Candidate;
                if (candidate is null)
                {
                    continue;
                }

                if (!CanTranslate(generation, translationEnabled))
                {
                    await FlushEligibleGroupAsync(
                        eligibleGroup,
                        generation,
                        translationEnabled,
                        cancellationToken).ConfigureAwait(false);
                    eligibleGroupBytes = 0;
                    continue;
                }

                CandidateClassification classification = _classifier.Classify(
                    candidate.Text,
                    candidate.Layout,
                    candidate.Boundary);
                Record(TranslationRuntimeStage.ClassifierInvoked, generation);
                if (!classification.IsEligible)
                {
                    Record(TranslationRuntimeStage.ClassifierRejected, generation);
                    await FlushEligibleGroupAsync(
                        eligibleGroup,
                        generation,
                        translationEnabled,
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
                        translationEnabled,
                        cancellationToken).ConfigureAwait(false);
                    eligibleGroupBytes = 0;
                    separatorBytes = 0;
                }

                eligibleGroup.Add(candidate);
                Record(
                    eligibleGroup.Count == 1
                        ? TranslationRuntimeStage.OutputBlockOpened
                        : TranslationRuntimeStage.OutputBlockAppended,
                    generation);
                eligibleGroupBytes += separatorBytes + candidateBytes;
            }
        }

        await FlushEligibleGroupAsync(
            eligibleGroup,
            generation,
            translationEnabled,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushEligibleGroupAsync(
        List<ExtractedText> eligibleGroup,
        long generation,
        bool translationEnabled,
        CancellationToken cancellationToken)
    {
        if (eligibleGroup.Count == 0)
        {
            return;
        }

        if (!CanTranslate(generation, translationEnabled))
        {
            eligibleGroup.Clear();
            return;
        }

        ExtractedText candidate = Combine(eligibleGroup);
        eligibleGroup.Clear();
        Record(TranslationRuntimeStage.OutputBlockClosed, generation);
        ulong sequence = unchecked((ulong)Interlocked.Increment(ref _sequence));
        if (_secretDetector is not null)
        {
            Record(TranslationRuntimeStage.PrivacyInvoked, generation, sequence);
            PrivacyDecision privacy = _secretDetector.Screen(sequence, candidate.Text);
            if (privacy.Outcome == PrivacyOutcome.Skip)
            {
                Record(TranslationRuntimeStage.PrivacyRejected, generation, sequence);
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
        Record(TranslationRuntimeStage.ClassifierInvoked, generation, sequence);
        TimeSpan createdAt = _clock.MonotonicNow;
        OutputSegment segment = new(
            _sessionId,
            generation,
            sequence,
            candidate.Text,
            candidate.Layout,
            candidate.Boundary,
            classification.Priority,
            classification.DeduplicationKey,
            createdAt);
        Record(new TranslationRuntimeEvent(
            TranslationRuntimeStage.SegmentCreated,
            createdAt,
            sequence,
            generation,
            CreatedAt: createdAt));
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

    private bool CanTranslate(long generation, bool translationEnabled) =>
        translationEnabled &&
        generation >= 0 &&
        (_session is null || generation == _session.Generation) &&
        IsEnabled;

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

    private IReadOnlyList<AnalysisFragment> SplitAtPowerShellSemanticBoundaries(
        ExtractedText extracted,
        long generation)
    {
        string[] lines = extracted.Text.ReplaceLineEndings("\n").Split('\n');
        List<AnalysisFragment> fragments = [];
        List<string> retainedLines = new(lines.Length);
        List<int> retainedIndent = new(lines.Length);

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            AnalysisLineDisposition disposition = ClassifyAnalysisLine(line, generation);
            if (disposition == AnalysisLineDisposition.PowerShellHardBoundary)
            {
                Record(TranslationRuntimeStage.SemanticBoundary, generation);
                AddRetainedFragment();
                fragments.Add(new AnalysisFragment(null, IsHardBoundary: true));
                continue;
            }

            if (disposition != AnalysisLineDisposition.ProgramOutput)
            {
                Record(TranslationRuntimeStage.OwnershipRejectedArtifact, generation);
                continue;
            }

            Record(TranslationRuntimeStage.OwnershipAcceptedProgramOutput, generation);
            retainedLines.Add(line);
            retainedIndent.Add(index < extracted.Layout.Indent.Count
                ? extracted.Layout.Indent[index]
                : CountIndent(line));
        }

        AddRetainedFragment();
        return fragments;

        void AddRetainedFragment()
        {
            if (retainedLines.Count == 0)
            {
                return;
            }

            string text = string.Join('\n', retainedLines);
            SourceBoundary boundary = retainedLines.Count > 1
                ? SourceBoundary.Block
                : extracted.Boundary == SourceBoundary.IdlePrompt && lines.Length == 1
                    ? SourceBoundary.IdlePrompt
                    : SourceBoundary.Line;
            fragments.Add(new AnalysisFragment(
                new ExtractedText(
                    text,
                    new LayoutHints(retainedLines.Count, retainedIndent.ToArray()),
                    boundary),
                IsHardBoundary: false));
            retainedLines.Clear();
            retainedIndent.Clear();
        }
    }

    private AnalysisLineDisposition ClassifyAnalysisLine(string line, long generation)
    {
        if (_submittedCommandTracker is not null)
        {
            return _submittedCommandTracker.ClassifyAnalysisLine(generation, line);
        }

        if (_analysisLineFilter is not null)
        {
            return _analysisLineFilter(line);
        }

        if (PowerShellPromptRegex().IsMatch(line))
        {
            return AnalysisLineDisposition.PowerShellHardBoundary;
        }

        if (ContinuationPromptRegex().IsMatch(line))
        {
            return AnalysisLineDisposition.PowerShellArtifact;
        }

        if (LooksLikePowerShellEcho(line) && _commandEchoFilter?.Invoke(line) is true)
        {
            return PowerShellPromptWithTailRegex().IsMatch(line)
                ? AnalysisLineDisposition.PowerShellHardBoundary
                : AnalysisLineDisposition.PowerShellArtifact;
        }

        return AnalysisLineDisposition.ProgramOutput;
    }

    private static bool LooksLikePowerShellEcho(string line) =>
        PowerShellPromptWithTailRegex().IsMatch(line) || ContinuationEchoPrefixRegex().IsMatch(line);

    private void Record(
        TranslationRuntimeStage stage,
        long generation,
        ulong sequence = 0) =>
        Record(new TranslationRuntimeEvent(
            stage,
            _clock.MonotonicNow,
            sequence,
            generation));

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

        bool changed = Interlocked.Exchange(ref _enabled, 1) == 0;
        if (changed)
        {
            _ = Interlocked.Increment(ref _legacyObservationEpoch);
        }

        return changed;
    }

    private readonly record struct AnalysisChunk(
        long Generation,
        bool TranslationEnabled,
        byte[] Bytes);

    private readonly record struct AnalysisCaptureState(
        long Generation,
        bool TranslationEnabled);

    private readonly record struct AnalysisFragment(ExtractedText? Candidate, bool IsHardBoundary);
}
