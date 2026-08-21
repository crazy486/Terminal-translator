using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerminalTranslator.Cli.Commands;

public sealed partial class SubmittedCommandTracker
{
    private const int MaximumRecentCommands = 16;

    private readonly object _gate = new();
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly StringBuilder _currentLine = new();
    private readonly Queue<SubmittedCommand> _recent = new();
    private InputState _state;
    private bool _lastWasCarriageReturn;

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
            foreach (SubmittedCommand submitted in _recent.Reverse())
            {
                if (submitted.Consumed)
                {
                    continue;
                }

                if (string.Equals(submitted.Text, normalized, StringComparison.Ordinal))
                {
                    submitted.Consumed = true;
                    return true;
                }

                if (submitted.Text.StartsWith(normalized, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return string.Equals(Normalize(_currentLine.ToString()), normalized, StringComparison.Ordinal);
        }
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
        string normalized = Normalize(_currentLine.ToString());
        _currentLine.Clear();
        if (normalized.Length == 0)
        {
            return;
        }

        _recent.Enqueue(new SubmittedCommand(normalized));
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

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^PS\s+(?:[A-Za-z]:\\|\\\\|/).*?>\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellPromptPrefixRegex();

    [GeneratedRegex(@"^(?:>\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex ContinuationPrefixRegex();

    private sealed class SubmittedCommand(string text)
    {
        public string Text { get; } = text;

        public bool Consumed { get; set; }
    }

    private enum InputState { Normal, Escape, Csi }
}
