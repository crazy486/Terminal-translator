using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerminalTranslator.Cli.Commands;

public enum AnalysisLineDisposition
{
    ProgramOutput,
    PowerShellArtifact,
    PowerShellHardBoundary,
    TerminalTranslatorControlOutput,
}

public interface ISubmittedCommandTracker
{
    void BeginObservationEpoch(long epoch);

    void BeginObservationEpoch(long epoch, bool claimDormantEnableControl);

    bool Observe(long epoch, ReadOnlyMemory<byte> bytes);

    bool ObserveDormantInput(long epoch, ReadOnlyMemory<byte> bytes);

    AnalysisLineDisposition ClassifyAnalysisLine(long epoch, string line);
}

public sealed partial class SubmittedCommandTracker : ISubmittedCommandTracker
{
    private const int MaximumPendingLines = 64;
    private const int MaximumControlOutputLines = 16;
    private static readonly TimeSpan MaximumControlOwnershipDuration = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly StringBuilder _currentLine = new();
    private readonly Queue<SubmittedLine> _pending = new();
    private readonly Queue<SubmittedLine> _recentSubmitted = new();
    private InputParserState _inputParserState;
    private bool _lastWasCarriageReturn;
    private long _nextSubmissionSequence;
    private long _observationEpoch = -1;
    private CommandTransaction? _activeTransaction;
    private string? _dormantEnableControl;

    public void Observe(ReadOnlyMemory<byte> bytes)
    {
        lock (_gate)
        {
            EnsureLegacyEpoch();
            ObserveCore(bytes, dormant: false);
        }
    }

    public bool Observe(long epoch, ReadOnlyMemory<byte> bytes)
    {
        lock (_gate)
        {
            if (!TryEnterEpoch(epoch))
            {
                return false;
            }

            ObserveCore(bytes, dormant: false);
            return true;
        }
    }

    public bool ObserveDormantInput(long epoch, ReadOnlyMemory<byte> bytes)
    {
        lock (_gate)
        {
            if (!TryEnterEpoch(epoch))
            {
                return false;
            }

            ObserveCore(bytes, dormant: true);
            return true;
        }
    }

    public void BeginObservationEpoch(long epoch)
    {
        BeginObservationEpoch(epoch, claimDormantEnableControl: false);
    }

    public void BeginObservationEpoch(long epoch, bool claimDormantEnableControl)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        lock (_gate)
        {
            string? dormantEnableControl = claimDormantEnableControl
                ? _dormantEnableControl
                : null;
            bool entered = TryEnterEpoch(epoch);
            if (entered && dormantEnableControl is not null && epoch == _observationEpoch)
            {
                SubmittedLine root = new(
                    _observationEpoch,
                    ++_nextSubmissionSequence,
                    dormantEnableControl,
                    IsTerminalTranslatorControl: true);
                _activeTransaction = new CommandTransaction(root, allowControlOwnership: true)
                {
                    State = TransactionState.AwaitingRootEcho,
                };
                _dormantEnableControl = null;
            }
        }
    }

    public bool IsEcho(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string normalized = Normalize(candidate);
        if (normalized.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            PendingMatch pending = FindPending(normalized);
            if (pending.Kind == PendingMatchKind.Exact)
            {
                ConsumeThrough(pending.Index);
                return true;
            }

            return pending.Kind == PendingMatchKind.Prefix || MatchesCurrentInput(normalized);
        }
    }

    public AnalysisLineDisposition ClassifyAnalysisLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            EnsureLegacyEpoch();
            return ClassifyAnalysisLineCore(line);
        }
    }

    public AnalysisLineDisposition ClassifyAnalysisLine(long epoch, string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            if (!TryEnterEpoch(epoch))
            {
                return AnalysisLineDisposition.ProgramOutput;
            }

            return ClassifyAnalysisLineCore(line);
        }
    }

    private AnalysisLineDisposition ClassifyAnalysisLineCore(string line)
    {
        Match prompt = PowerShellPromptLineRegex().Match(line);
        if (prompt.Success)
        {
            return ClassifyPrimaryPrompt(prompt.Groups["tail"].Value);
        }

        Match wrappedPrimaryPrompt = PowerShellWrappedPrimaryPromptTailRegex().Match(line);
        if (wrappedPrimaryPrompt.Success &&
            TryClassifyWrappedPrimaryPrompt(
                wrappedPrimaryPrompt.Groups["tail"].Value,
                out AnalysisLineDisposition wrappedDisposition))
        {
            return wrappedDisposition;
        }

        Match continuation = PowerShellContinuationLineRegex().Match(line);
        if (continuation.Success)
        {
            return ClassifyContinuationPrompt(continuation.Groups["tail"].Value);
        }

        if (_activeTransaction?.IsTerminalTranslatorControl == true)
        {
            if (TryReleaseControlAtLaterSubmissionEcho(line, out AnalysisLineDisposition releaseDisposition))
            {
                return releaseDisposition;
            }

            if (_activeTransaction.TryOwnControlOutputLine())
            {
                ConfirmActiveRootWithoutEcho();
                return AnalysisLineDisposition.TerminalTranslatorControlOutput;
            }

            CompleteActiveTransaction();
        }

        string normalized = Normalize(line);
        if (normalized.Length == 0)
        {
            return AnalysisLineDisposition.PowerShellArtifact;
        }

        if (_activeTransaction?.State == TransactionState.CollectingContinuationInput)
        {
            PendingMatch continuationInput = FindPending(normalized);
            if (continuationInput.Kind == PendingMatchKind.Exact)
            {
                SubmittedLine submitted = ConsumeThrough(continuationInput.Index);
                _activeTransaction.Attach(submitted);
                return AnalysisLineDisposition.PowerShellArtifact;
            }

            if (continuationInput.Kind == PendingMatchKind.Prefix || MatchesCurrentInput(normalized))
            {
                return AnalysisLineDisposition.PowerShellArtifact;
            }

            _activeTransaction.State = TransactionState.ExecutingOrProducingOutput;
            return AnalysisLineDisposition.ProgramOutput;
        }

        PendingMatch root = FindPending(normalized);
        if (root.Kind == PendingMatchKind.Exact)
        {
            StartTransaction(ConsumeThrough(root.Index));
            return AnalysisLineDisposition.PowerShellHardBoundary;
        }

        if (root.Kind == PendingMatchKind.Prefix || MatchesCurrentInput(normalized))
        {
            return AnalysisLineDisposition.PowerShellArtifact;
        }

        return AnalysisLineDisposition.ProgramOutput;
    }

    private AnalysisLineDisposition ClassifyPrimaryPrompt(string tailValue)
    {
        string tail = RemoveLeadingContinuationMarker(tailValue.Trim());
        if (tail.Length > 0 && _activeTransaction?.MatchesAttachedLine(tail) == true)
        {
            ConfirmActiveRootEcho();
            return AnalysisLineDisposition.PowerShellHardBoundary;
        }

        if (tail.Length > 0 &&
            FindPending(tail, allowPsReadLineRedrawComposite: true).Kind == PendingMatchKind.None &&
            MatchesRecentSubmission(tail))
        {
            return AnalysisLineDisposition.PowerShellHardBoundary;
        }

        if (tail.Length == 0 &&
            _activeTransaction?.State == TransactionState.AwaitingRootEcho)
        {
            // Input observation intentionally happens before the ConPTY write.
            // The analysis worker can therefore receive the already-visible
            // pre-submission prompt after ownership has been established.
            return AnalysisLineDisposition.PowerShellHardBoundary;
        }

        CompleteActiveTransaction();
        if (tail.Length == 0)
        {
            return AnalysisLineDisposition.PowerShellHardBoundary;
        }

        if (TryConsumePendingSequence(tail, out SubmittedLine[] sequence))
        {
            StartTransaction(sequence[0]);
            foreach (SubmittedLine continuation in sequence.Skip(1))
            {
                _activeTransaction!.Attach(continuation);
            }

            return AnalysisLineDisposition.PowerShellHardBoundary;
        }

        if (TryConsumePendingControlPrefix(tail, out SubmittedLine controlRoot))
        {
            StartTransaction(controlRoot);
            return AnalysisLineDisposition.PowerShellHardBoundary;
        }

        PendingMatch root = FindPending(tail, allowPsReadLineRedrawComposite: true);
        if (root.Kind == PendingMatchKind.Exact)
        {
            StartTransaction(ConsumeThrough(root.Index));
            return AnalysisLineDisposition.PowerShellHardBoundary;
        }

        if (root.Kind == PendingMatchKind.Prefix || MatchesCurrentInput(tail))
        {
            return AnalysisLineDisposition.PowerShellHardBoundary;
        }

        return AnalysisLineDisposition.ProgramOutput;
    }

    private bool TryClassifyWrappedPrimaryPrompt(
        string tailValue,
        out AnalysisLineDisposition disposition)
    {
        string tail = Normalize(tailValue);
        PendingMatch pending = FindPending(tail, allowPsReadLineRedrawComposite: true);
        if (pending.Kind == PendingMatchKind.Exact)
        {
            SubmittedLine matched = _pending.ElementAt(pending.Index);
            if (matched.IsTerminalTranslatorControl)
            {
                // A row shaped as "> <pending command>" is ambiguous: narrow-pane
                // PSReadLine redraw and ordinary stdout are byte-for-byte identical.
                // It may release existing suppression, but it must never acquire or
                // renew control ownership. A later full primary prompt can confirm it.
                CompleteActiveTransaction();
                disposition = AnalysisLineDisposition.ProgramOutput;
                return true;
            }

            CompleteActiveTransaction();
            StartTransaction(ConsumeThrough(pending.Index));
            disposition = AnalysisLineDisposition.PowerShellHardBoundary;
            return true;
        }

        if (pending.Kind == PendingMatchKind.Prefix || MatchesCurrentInput(tail))
        {
            disposition = AnalysisLineDisposition.PowerShellHardBoundary;
            return true;
        }

        disposition = default;
        return false;
    }

    private bool TryReleaseControlAtLaterSubmissionEcho(
        string line,
        out AnalysisLineDisposition disposition)
    {
        disposition = default;
        if (_activeTransaction?.IsTerminalTranslatorControl != true)
        {
            return false;
        }

        string candidate = Normalize(line);
        if (candidate.Length == 0)
        {
            return false;
        }

        SubmittedLine[] pending = _pending.ToArray();
        for (int index = 0; index < pending.Length; index++)
        {
            SubmittedLine submitted = pending[index];
            if (submitted.SubmissionSequence <= _activeTransaction.RootSequence ||
                !EndsWithSubmittedCommand(candidate, submitted.Text))
            {
                continue;
            }

            SubmittedLine boundary = ConsumeThrough(index);
            CompleteActiveTransaction();
            StartTransaction(boundary, allowControlOwnership: false);
            disposition = AnalysisLineDisposition.PowerShellHardBoundary;
            return true;
        }

        return false;
    }

    private static bool EndsWithSubmittedCommand(string candidate, string submitted) =>
        string.Equals(candidate, submitted, StringComparison.Ordinal) ||
        (candidate.Length > submitted.Length &&
         candidate.EndsWith(submitted, StringComparison.Ordinal) &&
         char.IsWhiteSpace(candidate[candidate.Length - submitted.Length - 1]));

    private AnalysisLineDisposition ClassifyContinuationPrompt(string tailValue)
    {
        string tail = RemoveLeadingContinuationMarker(tailValue.Trim());
        if (_activeTransaction is null)
        {
            PendingMatch orphanedContinuation = FindPending(tail);
            if (tail.Length > 0 && orphanedContinuation.Kind == PendingMatchKind.Exact)
            {
                SubmittedLine[] submittedLines = ConsumeLinesThrough(orphanedContinuation.Index);
                StartTransaction(submittedLines[0], collectingContinuation: true);
                foreach (SubmittedLine continuation in submittedLines.Skip(1))
                {
                    _activeTransaction!.Attach(continuation);
                }

                return AnalysisLineDisposition.PowerShellArtifact;
            }

            return AnalysisLineDisposition.ProgramOutput;
        }

        _activeTransaction.State = TransactionState.CollectingContinuationInput;
        if (tail.Length == 0)
        {
            return AnalysisLineDisposition.PowerShellArtifact;
        }

        PendingMatch pending = FindPending(tail);
        if (pending.Kind == PendingMatchKind.Exact)
        {
            _activeTransaction.Attach(ConsumeThrough(pending.Index));
            return AnalysisLineDisposition.PowerShellArtifact;
        }

        if (pending.Kind == PendingMatchKind.Prefix ||
            _activeTransaction.MatchesAttachedLine(tail) ||
            MatchesRecentSubmission(tail) ||
            MatchesCurrentInput(tail))
        {
            return AnalysisLineDisposition.PowerShellArtifact;
        }

        // Repeated redraws are matched above. Once all observed submitted
        // lines have been consumed, an otherwise unmatched row is the first
        // program-output candidate for the active transaction.
        _activeTransaction.State = TransactionState.ExecutingOrProducingOutput;
        return AnalysisLineDisposition.ProgramOutput;
    }

    private void StartTransaction(
        SubmittedLine root,
        bool collectingContinuation = false,
        bool allowControlOwnership = true)
    {
        if (root.ObservationEpoch != _observationEpoch)
        {
            return;
        }

        _activeTransaction = new CommandTransaction(root, allowControlOwnership)
        {
            State = collectingContinuation
                ? TransactionState.CollectingContinuationInput
                : TransactionState.ExecutingOrProducingOutput,
        };
    }

    private void CompleteActiveTransaction() => _activeTransaction = null;

    private void ConfirmActiveRootEcho()
    {
        if (_activeTransaction?.State != TransactionState.AwaitingRootEcho)
        {
            return;
        }

        ConsumePendingThroughSequence(_activeTransaction.RootSequence);
        _activeTransaction.State = TransactionState.ExecutingOrProducingOutput;
    }

    private void ConfirmActiveRootWithoutEcho()
    {
        if (_activeTransaction?.State != TransactionState.AwaitingRootEcho)
        {
            return;
        }

        ConsumePendingThroughSequence(_activeTransaction.RootSequence);
        _activeTransaction.State = TransactionState.ExecutingOrProducingOutput;
    }

    private void ConsumePendingThroughSequence(long submissionSequence)
    {
        SubmittedLine[] pending = _pending.ToArray();
        int index = Array.FindIndex(
            pending,
            line => line.SubmissionSequence == submissionSequence);
        if (index >= 0)
        {
            ConsumeLinesThrough(index);
        }
    }

    private PendingMatch FindPending(
        string candidate,
        bool allowPsReadLineRedrawComposite = false)
    {
        string[] candidates = CandidateForms(candidate);
        if (candidates.Length == 0)
        {
            return PendingMatch.None;
        }

        SubmittedLine[] pending = _pending.ToArray();
        PendingMatch prefix = PendingMatch.None;
        for (int index = 0; index < pending.Length; index++)
        {
            if (candidates.Any(candidateForm =>
                    string.Equals(pending[index].Text, candidateForm, StringComparison.Ordinal) ||
                    (allowPsReadLineRedrawComposite &&
                     IsPsReadLineRedrawComposite(candidateForm, pending[index].Text))))
            {
                return new PendingMatch(PendingMatchKind.Exact, index);
            }

            if (candidates.Any(candidateForm =>
                    pending[index].Text.StartsWith(candidateForm, StringComparison.Ordinal)) &&
                prefix.Kind == PendingMatchKind.None)
            {
                prefix = new PendingMatch(PendingMatchKind.Prefix, index);
            }
        }

        return prefix;
    }

    private static bool IsPsReadLineRedrawComposite(string candidate, string submitted)
    {
        if (candidate.Length <= submitted.Length ||
            !candidate.EndsWith(submitted, StringComparison.Ordinal))
        {
            return false;
        }

        int finalSubmissionStart = candidate.Length - submitted.Length;
        if (finalSubmissionStart == 0 ||
            !char.IsWhiteSpace(candidate[finalSubmissionStart - 1]))
        {
            return false;
        }

        string stalePrefix = candidate[..finalSubmissionStart].TrimEnd();
        return stalePrefix.Length > 0 &&
            stalePrefix.Length < submitted.Length &&
            submitted.StartsWith(stalePrefix, StringComparison.Ordinal);
    }

    private bool TryConsumePendingSequence(string candidate, out SubmittedLine[] sequence)
    {
        string remaining = Normalize(candidate);
        SubmittedLine[] pending = _pending.ToArray();
        List<SubmittedLine> matched = [];
        for (int index = 0; index < pending.Length; index++)
        {
            remaining = RemoveLeadingContinuationMarker(remaining);
            if (!remaining.StartsWith(pending[index].Text, StringComparison.Ordinal))
            {
                break;
            }

            matched.Add(pending[index]);
            remaining = remaining[pending[index].Text.Length..].TrimStart();
            if (RemoveLeadingContinuationMarker(remaining).Length == 0)
            {
                sequence = ConsumeLinesThrough(index);
                return true;
            }
        }

        sequence = [];
        return false;
    }

    private bool TryConsumePendingControlPrefix(
        string candidate,
        out SubmittedLine controlRoot)
    {
        string normalized = Normalize(candidate);
        SubmittedLine[] pending = _pending.ToArray();
        for (int index = 0; index < pending.Length; index++)
        {
            if (!pending[index].IsTerminalTranslatorControl ||
                normalized.Length <= pending[index].Text.Length ||
                !normalized.StartsWith(pending[index].Text, StringComparison.Ordinal))
            {
                continue;
            }

            controlRoot = ConsumeThrough(index);
            return true;
        }

        controlRoot = null!;
        return false;
    }

    private static string[] CandidateForms(string candidate)
    {
        string normalized = Normalize(candidate);
        if (normalized.Length == 0)
        {
            return [];
        }

        int promptEnd = normalized.IndexOf('>');
        if (promptEnd < 0 || promptEnd == normalized.Length - 1)
        {
            return [normalized];
        }

        string tail = Normalize(normalized[(promptEnd + 1)..]);
        return tail.Length == 0 || string.Equals(tail, normalized, StringComparison.Ordinal)
            ? [normalized]
            : [normalized, tail];
    }

    private SubmittedLine ConsumeThrough(int index)
    {
        SubmittedLine[] lines = ConsumeLinesThrough(index);
        return lines[^1];
    }

    private SubmittedLine[] ConsumeLinesThrough(int index)
    {
        SubmittedLine[] lines = new SubmittedLine[index + 1];
        for (int current = 0; current <= index; current++)
        {
            lines[current] = _pending.Dequeue();
        }

        return lines;
    }

    private bool MatchesCurrentInput(string candidate) =>
        string.Equals(
            Normalize(_currentLine.ToString()),
            Normalize(candidate),
            StringComparison.Ordinal);

    private bool MatchesRecentSubmission(string candidate)
    {
        string normalized = Normalize(candidate);
        return normalized.Length > 0 && _recentSubmitted.Any(line =>
            string.Equals(line.Text, normalized, StringComparison.Ordinal));
    }

    private void EnsureLegacyEpoch()
    {
        if (_observationEpoch < 0)
        {
            ResetForEpoch(0);
        }
    }

    private bool TryEnterEpoch(long epoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        if (epoch < _observationEpoch)
        {
            return false;
        }

        if (epoch > _observationEpoch)
        {
            ResetForEpoch(epoch);
        }

        return true;
    }

    private void ResetForEpoch(long epoch)
    {
        _decoder.Reset();
        _currentLine.Clear();
        _pending.Clear();
        _recentSubmitted.Clear();
        _inputParserState = InputParserState.Normal;
        _lastWasCarriageReturn = false;
        _activeTransaction = null;
        _dormantEnableControl = null;
        _observationEpoch = epoch;
    }

    private void ObserveCore(ReadOnlyMemory<byte> bytes, bool dormant)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        int charCount = _decoder.GetCharCount(bytes.Span, flush: false);
        char[] characters = new char[charCount];
        _decoder.GetChars(bytes.Span, characters, flush: false);
        foreach (char character in characters)
        {
            ConsumeInput(character, dormant);
        }
    }

    private void ConsumeInput(char character, bool dormant)
    {
        if (_inputParserState == InputParserState.Escape)
        {
            _inputParserState = character == '[' ? InputParserState.Csi : InputParserState.Normal;
            return;
        }

        if (_inputParserState == InputParserState.Csi)
        {
            if (character is >= '@' and <= '~')
            {
                _inputParserState = InputParserState.Normal;
            }

            return;
        }

        if (character == '\u001b')
        {
            _inputParserState = InputParserState.Escape;
            return;
        }

        switch (character)
        {
            case '\r':
                CommitInputLine(dormant);
                _lastWasCarriageReturn = true;
                break;
            case '\n':
                if (!_lastWasCarriageReturn)
                {
                    CommitInputLine(dormant);
                }

                _lastWasCarriageReturn = false;
                break;
            case '\b':
            case '\u007f':
                _lastWasCarriageReturn = false;
                RemoveLastTextElement();
                break;
            case '\u0003':
                _lastWasCarriageReturn = false;
                _currentLine.Clear();
                break;
            default:
                _lastWasCarriageReturn = false;
                if (!char.IsControl(character))
                {
                    _currentLine.Append(character);
                }

                break;
        }
    }

    private void CommitInputLine(bool dormant)
    {
        string normalized = Normalize(_currentLine.ToString());
        _currentLine.Clear();
        if (normalized.Length == 0)
        {
            return;
        }

        if (dormant)
        {
            if (TerminalTranslatorEnableCommandRegex().IsMatch(normalized))
            {
                _dormantEnableControl = normalized;
            }

            return;
        }

        bool mayStartControlTransaction = _activeTransaction is null && _pending.Count == 0;
        SubmittedLine submitted = new(
            _observationEpoch,
            ++_nextSubmissionSequence,
            normalized,
            TerminalTranslatorControlCommandRegex().IsMatch(normalized));
        _pending.Enqueue(submitted);
        _recentSubmitted.Enqueue(submitted);
        if (mayStartControlTransaction && submitted.IsTerminalTranslatorControl)
        {
            _activeTransaction = new CommandTransaction(submitted, allowControlOwnership: true)
            {
                State = TransactionState.AwaitingRootEcho,
            };
        }

        while (_pending.Count > MaximumPendingLines)
        {
            _pending.Dequeue();
        }

        while (_recentSubmitted.Count > MaximumPendingLines)
        {
            _recentSubmitted.Dequeue();
        }
    }

    private void RemoveLastTextElement()
    {
        if (_currentLine.Length == 0)
        {
            return;
        }

        int[] starts = StringInfo.ParseCombiningCharacters(_currentLine.ToString());
        _currentLine.Length = starts[^1];
    }

    private static string Normalize(string value)
    {
        string normalized = WhitespaceRegex().Replace(value.Trim(), " ");
        Match prompt = PowerShellPromptPrefixRegex().Match(normalized);
        if (prompt.Success)
        {
            normalized = normalized[prompt.Length..].TrimStart();
        }

        normalized = ContinuationPrefixRegex().Replace(normalized, string.Empty);
        return WhitespaceRegex().Replace(normalized.Trim(), " ");
    }

    private static string RemoveLeadingContinuationMarker(string value) =>
        ContinuationPrefixRegex().Replace(value, string.Empty).Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^PS\s+(?:[A-Za-z]:\\|\\\\|/).*?>\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellPromptPrefixRegex();

    [GeneratedRegex(@"^(?:>\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex ContinuationPrefixRegex();

    [GeneratedRegex(
        @"^\s*PS\s+(?:(?:[A-Za-z]:\\|\\\\|/)|(?:Microsoft\.PowerShell\.Core\\FileSystem::(?:[A-Za-z]:\\|\\\\))).*?>\s*(?<tail>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellPromptLineRegex();

    [GeneratedRegex(@"^\s*>(?!>)\s*(?<tail>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellWrappedPrimaryPromptTailRegex();

    [GeneratedRegex(@"^\s*>>+\s*(?<tail>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellContinuationLineRegex();

    [GeneratedRegex(
        @"^(?:&\s*)?(?:[\""'][^\""']*tt(?:\.exe)?[\""']|(?:\S*\\)?tt(?:\.exe)?)(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TerminalTranslatorControlCommandRegex();

    [GeneratedRegex(
        @"^(?:&\s*)?(?:[\""'][^\""']*tt(?:\.exe)?[\""']|(?:\S*\\)?tt(?:\.exe)?)\s+on\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TerminalTranslatorEnableCommandRegex();

    private sealed record SubmittedLine(
        long ObservationEpoch,
        long SubmissionSequence,
        string Text,
        bool IsTerminalTranslatorControl);

    private sealed class CommandTransaction(SubmittedLine root, bool allowControlOwnership)
    {
        private readonly List<SubmittedLine> _lines = [root];
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private int _ownedControlOutputLines;

        public bool IsTerminalTranslatorControl =>
            allowControlOwnership && root.IsTerminalTranslatorControl;

        public long ObservationEpoch => root.ObservationEpoch;

        public long RootSequence => root.SubmissionSequence;

        public TransactionState State { get; set; }

        public void Attach(SubmittedLine line)
        {
            if (line.ObservationEpoch != root.ObservationEpoch)
            {
                throw new InvalidOperationException(
                    "A command transaction cannot attach input from another observation epoch.");
            }

            _lines.Add(line);
        }

        public bool TryOwnControlOutputLine()
        {
            if (!IsTerminalTranslatorControl ||
                _ownedControlOutputLines >= MaximumControlOutputLines ||
                Stopwatch.GetElapsedTime(_startedAt) > MaximumControlOwnershipDuration)
            {
                return false;
            }

            _ownedControlOutputLines++;
            return true;
        }

        public bool MatchesAttachedLine(string candidate) =>
            _lines.Any(line => string.Equals(line.Text, Normalize(candidate), StringComparison.Ordinal));
    }

    private readonly record struct PendingMatch(PendingMatchKind Kind, int Index)
    {
        public static PendingMatch None => new(PendingMatchKind.None, -1);
    }

    private enum PendingMatchKind { None, Prefix, Exact }

    private enum TransactionState
    {
        AwaitingRootEcho,
        ExecutingOrProducingOutput,
        CollectingContinuationInput,
    }

    private enum InputParserState { Normal, Escape, Csi }
}
