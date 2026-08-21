using System.Text;
using TerminalTranslator.Core.Models;

namespace TerminalTranslator.Core.Parsing;

public sealed record ExtractedText(string Text, LayoutHints Layout, SourceBoundary Boundary);

public sealed class VtTextExtractor
{
    public const int MaximumCandidateBytes = 8 * 1024;
    public const int MaximumControlSequenceCharacters = 4 * 1024;

    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _control = new();
    private readonly List<string> _pendingLines = [];
    private int _viewportColumns;
    private ParserState _state;
    private ParserState _controlStringState;
    private bool _pendingCarriageReturn;
    private bool _alternateBuffer;
    private bool _previousLineReachedMargin;
    private bool _previousLineEndedWithWhitespace;
    private bool _currentLineContinuesPrevious;
    private bool _currentLineStartedAtRightMargin;
    private bool _cursorAtRightMargin;

    public VtTextExtractor(int viewportColumns = 120)
    {
        _viewportColumns = Math.Max(1, viewportColumns);
    }

    public void Resize(int viewportColumns) =>
        Volatile.Write(ref _viewportColumns, Math.Max(1, viewportColumns));

    public IReadOnlyList<ExtractedText> Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return [];
        }

        int charCount = _decoder.GetCharCount(bytes, flush: false);
        char[] chars = new char[charCount];
        _decoder.GetChars(bytes, chars, flush: false);
        List<ExtractedText> output = [];
        foreach (char value in chars)
        {
            Consume(value, output);
        }

        if (_state == ParserState.Normal && !_pendingCarriageReturn && _line.Length == 0 &&
            !MayContinuePendingLine())
        {
            FlushPending(output, SourceBoundary.Line);
        }

        return output;
    }

    public IReadOnlyList<ExtractedText> Flush()
    {
        List<ExtractedText> output = [];
        if (_pendingCarriageReturn || _line.Length > 0)
        {
            CompleteLine(output);
            _pendingCarriageReturn = false;
        }

        FlushPending(output, SourceBoundary.Line);
        return output;
    }

    public IReadOnlyList<ExtractedText> FlushIdle()
    {
        if (_alternateBuffer || (_line.Length == 0 && _pendingLines.Count == 0))
        {
            return [];
        }

        List<ExtractedText> output = [];
        if (_line.Length > 0)
        {
            string line = _line.ToString().TrimEnd();
            _line.Clear();
            if (line.Length > 0)
            {
                _pendingLines.Add(line);
            }
        }

        FlushPending(output, SourceBoundary.IdlePrompt);
        return output;
    }

    private void Consume(char value, List<ExtractedText> output)
    {
        if (_state != ParserState.Normal)
        {
            ConsumeControl(value);
            return;
        }

        if (_pendingCarriageReturn)
        {
            if (value == '\n')
            {
                _pendingCarriageReturn = false;
                CompleteLine(output);
                return;
            }

            if (value == '\u001b')
            {
                _state = ParserState.Escape;
                _control.Clear();
                return;
            }

            _pendingCarriageReturn = false;
            _line.Clear();
        }

        if (value == '\u001b')
        {
            _state = ParserState.Escape;
            _control.Clear();
            return;
        }

        if (_alternateBuffer)
        {
            return;
        }

        switch (value)
        {
            case '\r':
                _pendingCarriageReturn = true;
                break;
            case '\n':
                CompleteLine(output);
                break;
            case '\b':
                if (_line.Length > 0)
                {
                    _line.Length--;
                }
                break;
            case '\t':
                _line.Append("    ");
                break;
            default:
                if (!char.IsControl(value))
                {
                    if (_line.Length == 0 && _pendingLines.Count > 0 &&
                        (_previousLineReachedMargin || _cursorAtRightMargin))
                    {
                        if (!_currentLineContinuesPrevious)
                        {
                            _currentLineContinuesPrevious = true;
                            _currentLineStartedAtRightMargin = _cursorAtRightMargin;
                        }

                        if (_cursorAtRightMargin && _pendingLines[^1].EndsWith(value))
                        {
                            _cursorAtRightMargin = false;
                            break;
                        }
                    }

                    _cursorAtRightMargin = false;
                    _line.Append(value);
                }
                break;
        }
    }

    private void ConsumeControl(char value)
    {
        if (_state == ParserState.Escape)
        {
            _state = value switch
            {
                '[' => ParserState.Csi,
                ']' => ParserState.Osc,
                'P' or '^' or '_' => ParserState.Dcs,
                _ => ParserState.Normal,
            };
            return;
        }

        if (_state is ParserState.Osc or ParserState.Dcs)
        {
            if (_state == ParserState.Osc && value == '\a')
            {
                ResetControl();
            }
            else if (value == '\u001b')
            {
                _controlStringState = _state;
                _state = ParserState.ControlStringEscape;
            }
            else
            {
                _control.Append(value);
                if (_control.Length > MaximumControlSequenceCharacters)
                {
                    InvalidateCandidate();
                    ResetControl();
                }
            }

            return;
        }

        if (_state == ParserState.ControlStringEscape)
        {
            _state = value == '\\' ? ParserState.Normal : _controlStringState;
            if (_state == ParserState.Normal)
            {
                _control.Clear();
            }

            return;
        }

        _control.Append(value);
        if (_control.Length > MaximumControlSequenceCharacters)
        {
            InvalidateCandidate();
            ResetControl();
            return;
        }

        if (value is >= '@' and <= '~')
        {
            ApplyCsi(_control.ToString());
            ResetControl();
        }
    }

    private void ApplyCsi(string sequence)
    {
        if (sequence is "?1049h" or "?1047h" or "?47h")
        {
            _alternateBuffer = true;
            InvalidateCandidate();
        }
        else if (sequence is "?1049l" or "?1047l" or "?47l")
        {
            _alternateBuffer = false;
            InvalidateCandidate();
        }
        else if (sequence.EndsWith('J') || sequence.EndsWith('K'))
        {
            InvalidateCandidate();
        }
        else if (sequence.EndsWith('H') || sequence.EndsWith('f'))
        {
            string[] parts = sequence[..^1].Split(';');
            if (parts.Length >= 2 && int.TryParse(parts[^1], out int column))
            {
                _cursorAtRightMargin = column >= Volatile.Read(ref _viewportColumns);
            }
        }
    }

    private void ResetControl()
    {
        _state = ParserState.Normal;
        _control.Clear();
    }

    private void InvalidateCandidate()
    {
        _line.Clear();
        _pendingLines.Clear();
        _pendingCarriageReturn = false;
        _previousLineReachedMargin = false;
        _previousLineEndedWithWhitespace = false;
        _currentLineContinuesPrevious = false;
        _currentLineStartedAtRightMargin = false;
        _cursorAtRightMargin = false;
    }

    private void CompleteLine(List<ExtractedText> output)
    {
        if (_alternateBuffer)
        {
            _line.Clear();
            return;
        }

        string rawLine = _line.ToString();
        string line = NormalizeLine(rawLine.TrimEnd());
        _line.Clear();
        if (line.Length == 0)
        {
            _previousLineReachedMargin = false;
            _previousLineEndedWithWhitespace = false;
            _currentLineContinuesPrevious = false;
            _currentLineStartedAtRightMargin = false;
            FlushPending(output, SourceBoundary.Line);
            return;
        }

        int indent = CountIndent(line);
        if (_currentLineContinuesPrevious && _pendingLines.Count > 0)
        {
            AppendContinuation(line, _currentLineStartedAtRightMargin);
        }
        else if (_pendingLines.Count > 0 && indent == 0 && _previousLineEndedWithWhitespace &&
            IsLexicalContinuation(line))
        {
            AppendContinuation(line, startedAtRightMargin: false);
        }
        else
        {
            if (_pendingLines.Count > 0 && indent == 0 && !_pendingLines[^1].TrimEnd().EndsWith(':'))
            {
                FlushPending(output, SourceBoundary.Line);
            }

            _pendingLines.Add(line);
        }

        int viewportColumns = Volatile.Read(ref _viewportColumns);
        int displayWidth = GetDisplayWidth(rawLine);
        _previousLineReachedMargin = rawLine.Length > 0 && rawLine[^1] <= 0x7F &&
            displayWidth >= viewportColumns - 1 && displayWidth <= viewportColumns;
        _previousLineEndedWithWhitespace = rawLine.Length > 0 && char.IsWhiteSpace(rawLine[^1]);
        _currentLineContinuesPrevious = false;
        _currentLineStartedAtRightMargin = false;
    }

    private void FlushPending(List<ExtractedText> output, SourceBoundary singleLineBoundary)
    {
        if (_pendingLines.Count == 0)
        {
            return;
        }

        string text = string.Join('\n', _pendingLines);
        if (Encoding.UTF8.GetByteCount(text) <= MaximumCandidateBytes)
        {
            int[] indent = _pendingLines.Select(CountIndent).ToArray();
            output.Add(new ExtractedText(
                text,
                new LayoutHints(_pendingLines.Count, indent),
                _pendingLines.Count == 1 ? singleLineBoundary : SourceBoundary.Block));
        }

        _pendingLines.Clear();
        _previousLineReachedMargin = false;
        _previousLineEndedWithWhitespace = false;
        _currentLineContinuesPrevious = false;
        _currentLineStartedAtRightMargin = false;
    }

    private bool MayContinuePendingLine()
    {
        if (_pendingLines.Count == 0)
        {
            return false;
        }

        return _previousLineReachedMargin || _previousLineEndedWithWhitespace;
    }

    private bool IsLexicalContinuation(string line)
    {
        string current = line.TrimStart();
        if (current.Length == 0)
        {
            return false;
        }

        char first = current[0];
        return char.IsLower(first) || (_pendingLines[^1].Length > 0 && _pendingLines[^1][^1] == first);
    }

    private void AppendContinuation(string line, bool startedAtRightMargin)
    {
        string continuation = startedAtRightMargin ? line : line.TrimStart();
        if (_pendingLines[^1].Length > 0 && continuation.Length > 0 &&
            _pendingLines[^1][^1] == continuation[0])
        {
            continuation = continuation[1..];
        }

        if (!startedAtRightMargin && _previousLineEndedWithWhitespace &&
            !_pendingLines[^1].EndsWith(' '))
        {
            _pendingLines[^1] += ' ';
        }

        _pendingLines[^1] += continuation;
    }

    private static int CountIndent(string text)
    {
        int count = 0;
        while (count < text.Length && text[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static string NormalizeLine(string line)
    {
        int markerLength = 0;
        while (markerLength < line.Length && line[markerLength] == '>')
        {
            markerLength++;
        }

        if (markerLength == line.Length)
        {
            return string.Empty;
        }

        return markerLength > 0 && !char.IsWhiteSpace(line[markerLength]) ? line[markerLength..] : line;
    }

    private static int GetDisplayWidth(string text)
    {
        int width = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            width += IsWide(rune.Value) ? 2 : 1;
        }

        return width;
    }

    private static bool IsWide(int value) =>
        value is >= 0x1100 and <= 0x115F or
        >= 0x2E80 and <= 0xA4CF or
        >= 0xAC00 and <= 0xD7A3 or
        >= 0xF900 and <= 0xFAFF or
        >= 0xFE10 and <= 0xFE6F or
        >= 0xFF00 and <= 0xFF60 or
        >= 0xFFE0 and <= 0xFFE6 or
        >= 0x1F300 and <= 0x1FAFF;

    private enum ParserState { Normal, Escape, Csi, Osc, Dcs, ControlStringEscape }
}
