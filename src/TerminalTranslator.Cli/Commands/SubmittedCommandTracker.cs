using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerminalTranslator.Cli.Commands;

public enum AnalysisLineDisposition
{
    ProgramOutput,
    PowerShellArtifact,
    TerminalTranslatorControlOutput,
}

public sealed partial class SubmittedCommandTracker
{
    private const int MaximumRecentCommands = 16;

    private readonly object _gate = new();
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly StringBuilder _currentLine = new();
    private readonly Queue<SubmittedCommand> _recent = new();
    private InputState _state;
    private bool _lastWasCarriageReturn;
    private bool _controlOutputActive;
    private bool _controlOutputPending;
    private string? _hereStringTerminator;

    public void Observe(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            int charCount = _decoder.GetCharCount(bytes.Span, flush: false);
            char[] characters = new char[charCount];
            _decoder.GetChars(bytes.Span, characters, flush: false);
            foreach (char character in characters)
            {
                Consume(character);
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
            return TryMatchSubmitted(normalized, allowConsumed: false, out _) ||
                string.Equals(Normalize(_currentLine.ToString()), normalized, StringComparison.Ordinal);
        }
    }

    public AnalysisLineDisposition ClassifyAnalysisLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            Match prompt = PowerShellPromptLineRegex().Match(line);
            if (prompt.Success)
            {
                _controlOutputActive = false;
                _controlOutputPending = false;
                string tail = prompt.Groups["tail"].Value.Trim();
                if (tail.Length == 0)
                {
                    return AnalysisLineDisposition.PowerShellArtifact;
                }

                if (TryMatchSubmitted(tail, allowConsumed: true, out SubmittedCommand? submitted))
                {
                    ActivateControlOwnership(submitted);
                    return AnalysisLineDisposition.PowerShellArtifact;
                }

                return AnalysisLineDisposition.ProgramOutput;
            }

            Match continuation = PowerShellContinuationLineRegex().Match(line);
            if (continuation.Success)
            {
                string tail = continuation.Groups["tail"].Value.Trim();
                if (tail.Length == 0)
                {
                    return AnalysisLineDisposition.PowerShellArtifact;
                }

                if (TryMatchSubmitted(tail, allowConsumed: true, out SubmittedCommand? submitted))
                {
                    ActivateControlOwnership(submitted);
                    return AnalysisLineDisposition.PowerShellArtifact;
                }

                return IsControlOutputOwned
                    ? AnalysisLineDisposition.TerminalTranslatorControlOutput
                    : AnalysisLineDisposition.ProgramOutput;
            }

            if (IsControlOutputOwned)
            {
                return AnalysisLineDisposition.TerminalTranslatorControlOutput;
            }

            string normalized = Normalize(line);
            return normalized.Length > 0 &&
                (TryMatchSubmitted(normalized, allowConsumed: false, out _) ||
                 string.Equals(Normalize(_currentLine.ToString()), normalized, StringComparison.Ordinal))
                ? AnalysisLineDisposition.PowerShellArtifact
                : AnalysisLineDisposition.ProgramOutput;
        }
    }

    private bool IsControlOutputOwned => _controlOutputActive || _controlOutputPending;

    private void ActivateControlOwnership(SubmittedCommand? submitted)
    {
        if (submitted?.IsTerminalTranslatorControl == true)
        {
            _controlOutputActive = true;
            _controlOutputPending = false;
        }
    }

    private bool TryMatchSubmitted(
        string candidate,
        bool allowConsumed,
        out SubmittedCommand? matched)
    {
        string normalized = Normalize(candidate);
        string candidateWithoutPromptTail = RemovePromptTail(normalized);
        foreach (SubmittedCommand submitted in _recent.Reverse())
        {
            if (submitted.Consumed && !allowConsumed)
            {
                continue;
            }

            if (string.Equals(submitted.Text, normalized, StringComparison.Ordinal) ||
                string.Equals(submitted.Text, candidateWithoutPromptTail, StringComparison.Ordinal))
            {
                submitted.Consumed = true;
                matched = submitted;
                return true;
            }

            if ((normalized.Length > 0 && submitted.Text.StartsWith(normalized, StringComparison.Ordinal)) ||
                (candidateWithoutPromptTail.Length > 0 &&
                 submitted.Text.StartsWith(candidateWithoutPromptTail, StringComparison.Ordinal)))
            {
                matched = submitted;
                return true;
            }
        }

        matched = null;
        return false;
    }

    private void Consume(char character)
    {
        if (_state == InputState.Escape)
        {
            _state = character == '[' ? InputState.Csi : InputState.Normal;
            return;
        }

        if (_state == InputState.Csi)
        {
            if (character is >= '@' and <= '~')
            {
                _state = InputState.Normal;
            }

            return;
        }

        if (character == '\u001b')
        {
            _state = InputState.Escape;
            return;
        }

        switch (character)
        {
            case '\r':
                Commit();
                _lastWasCarriageReturn = true;
                break;
            case '\n':
                if (!_lastWasCarriageReturn)
                {
                    Commit();
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

    private void Commit()
    {
        string inputLine = _currentLine.ToString();
        string normalized = Normalize(inputLine);
        _currentLine.Clear();
        if (normalized.Length == 0)
        {
            return;
        }

        bool insideHereString = _hereStringTerminator is not null;
        Match hereStringStart = insideHereString
            ? Match.Empty
            : HereStringStartRegex().Match(inputLine.TrimEnd());
        SubmittedCommand submitted = new(
            normalized,
            !insideHereString && !hereStringStart.Success &&
            TerminalTranslatorControlCommandRegex().IsMatch(normalized));
        _recent.Enqueue(submitted);
        if (submitted.IsTerminalTranslatorControl)
        {
            _controlOutputPending = true;
        }

        if (insideHereString &&
            string.Equals(inputLine.Trim(), _hereStringTerminator, StringComparison.Ordinal))
        {
            _hereStringTerminator = null;
        }
        else if (hereStringStart.Success)
        {
            _hereStringTerminator = hereStringStart.Groups["quote"].Value + "@";
        }

        while (_recent.Count > MaximumRecentCommands)
        {
            _recent.Dequeue();
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

    private static string RemovePromptTail(string candidate)
    {
        int promptEnd = candidate.IndexOf('>');
        if (promptEnd < 0 || promptEnd == candidate.Length - 1)
        {
            return candidate;
        }

        string remainder = candidate[(promptEnd + 1)..].TrimStart();
        return remainder.Length == 0 ? candidate : remainder;
    }

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

    [GeneratedRegex(@"^\s*>+\s*(?<tail>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellContinuationLineRegex();

    [GeneratedRegex(
        @"^(?:&\s*)?(?:[\""'][^\""']*tt(?:\.exe)?[\""']|(?:\S*\\)?tt(?:\.exe)?)(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TerminalTranslatorControlCommandRegex();

    [GeneratedRegex(@"@(?<quote>[\""'])\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HereStringStartRegex();

    private sealed class SubmittedCommand(string text, bool isTerminalTranslatorControl)
    {
        public string Text { get; } = text;

        public bool IsTerminalTranslatorControl { get; } = isTerminalTranslatorControl;

        public bool Consumed { get; set; }
    }

    private enum InputState { Normal, Escape, Csi }
}
