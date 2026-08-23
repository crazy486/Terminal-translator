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
    private int _viewportRows;
    private ParserState _state;
    private ParserState _controlStringState;
    private bool _pendingCarriageReturn;
    private bool _alternateBuffer;
    private bool _previousLineReachedMargin;
    private bool _previousLineEndedWithWhitespace;
    private bool _currentLineContinuesPrevious;
    private bool _currentLineStartedAtRightMargin;
    private bool _cursorAtRightMargin;
    private int _cursorRow = 1;
    private int _cursorColumn;
    private int _lineRow = 1;
    private int _lineOriginColumn;

    public VtTextExtractor(int viewportColumns = 120, int viewportRows = 30)
    {
        _viewportColumns = Math.Max(1, viewportColumns);
        _viewportRows = Math.Max(1, viewportRows);
    }

    public void Resize(int viewportColumns, int? viewportRows = null)
    {
        Volatile.Write(ref _viewportColumns, Math.Max(1, viewportColumns));
        if (viewportRows.HasValue)
        {
            Volatile.Write(ref _viewportRows, Math.Max(1, viewportRows.Value));
        }
    }

    public void Reset()
    {
        _decoder.Reset();
        InvalidateCandidate();
        ResetControl();
        _alternateBuffer = false;
        _cursorRow = 1;
        _cursorColumn = 0;
        _lineRow = 1;
        _lineOriginColumn = 0;
    }

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
            // Idle is a temporal flush boundary, not a terminal line break.
            // Finalize through the normal line path so a right-margin visual
            // continuation is reconstructed before the pending block is emitted.
            CompleteLine(output);
        }

        _pendingCarriageReturn = false;
        FlushPending(output, SourceBoundary.IdlePrompt);
        return output;
    }

    private void Consume(char value, List<ExtractedText> output)
    {
        if (_state != ParserState.Normal)
        {
            ConsumeControl(value, output);
            return;
        }

        if (_pendingCarriageReturn)
        {
            if (value == '\n')
            {
                _pendingCarriageReturn = false;
                CompleteLine(output);
                AdvanceLine();
                return;
            }

            if (value == '\r')
            {
                _cursorColumn = 0;
                _cursorAtRightMargin = false;
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
            _lineRow = _cursorRow;
            _lineOriginColumn = _cursorColumn;
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
                _cursorColumn = 0;
                _cursorAtRightMargin = false;
                break;
            case '\n':
                CompleteLine(output);
                AdvanceLine();
                break;
            case '\b':
                _cursorColumn = Math.Max(0, _cursorColumn - 1);
                break;
            case '\t':
                int nextTabStop = ((_cursorColumn / 4) + 1) * 4;
                while (_cursorColumn < nextTabStop)
                {
                    WriteCharacter(' ', output);
                }
                break;
            default:
                if (!char.IsControl(value))
                {
                    WriteCharacter(value, output);
                }
                break;
        }
    }

    private void ConsumeControl(char value, List<ExtractedText> output)
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
            ApplyCsi(_control.ToString(), output);
            ResetControl();
        }
    }

    private void ApplyCsi(string sequence, List<ExtractedText> output)
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
        else if (sequence.EndsWith('J'))
        {
            InvalidateCandidate();
        }
        else if (sequence.EndsWith('K'))
        {
            ApplyEraseInLine(sequence);
        }
        else if (sequence.EndsWith('H') || sequence.EndsWith('f'))
        {
            int[] parameters = ParseParameters(sequence[..^1], defaultValue: 1);
            int row = parameters.Length > 0 ? parameters[0] : 1;
            int column = parameters.Length > 1 ? parameters[1] : 1;
            MoveCursor(Math.Max(1, row), Math.Max(0, column - 1), output);
        }
        else if (sequence.EndsWith('G'))
        {
            int[] parameters = ParseParameters(sequence[..^1], defaultValue: 1);
            MoveCursor(_cursorRow, Math.Max(0, parameters[0] - 1), output);
        }
        else if (sequence.EndsWith('d'))
        {
            int[] parameters = ParseParameters(sequence[..^1], defaultValue: 1);
            MoveCursor(Math.Max(1, parameters[0]), _cursorColumn, output);
        }
        else if (sequence.EndsWith('A'))
        {
            MoveCursor(Math.Max(1, _cursorRow - ParameterOrOne(sequence)), _cursorColumn, output);
        }
        else if (sequence.EndsWith('B'))
        {
            MoveCursor(_cursorRow + ParameterOrOne(sequence), _cursorColumn, output);
        }
        else if (sequence.EndsWith('C'))
        {
            MoveCursor(_cursorRow, _cursorColumn + ParameterOrOne(sequence), output);
        }
        else if (sequence.EndsWith('D'))
        {
            MoveCursor(_cursorRow, Math.Max(0, _cursorColumn - ParameterOrOne(sequence)), output);
        }
        else if (sequence.EndsWith('E'))
        {
            MoveCursor(_cursorRow + ParameterOrOne(sequence), 0, output);
        }
        else if (sequence.EndsWith('F'))
        {
            MoveCursor(Math.Max(1, _cursorRow - ParameterOrOne(sequence)), 0, output);
        }
    }

    private void WriteCharacter(char value, List<ExtractedText> output)
    {
        bool continuesAfterExplicitCursorMove = _cursorAtRightMargin;
        bool preservesLeadingWhitespaceAfterMargin =
            _previousLineReachedMargin && char.IsWhiteSpace(value);
        bool continuesAfterCrLfAtMargin = _previousLineReachedMargin &&
            (preservesLeadingWhitespaceAfterMargin || IsLexicalContinuation(value.ToString()));
        if (_line.Length == 0 && _pendingLines.Count > 0 &&
            (continuesAfterExplicitCursorMove || continuesAfterCrLfAtMargin))
        {
            if (!_currentLineContinuesPrevious)
            {
                _currentLineContinuesPrevious = true;
                _currentLineStartedAtRightMargin =
                    continuesAfterExplicitCursorMove || preservesLeadingWhitespaceAfterMargin;
            }

            if (continuesAfterExplicitCursorMove && _pendingLines[^1].EndsWith(value))
            {
                _cursorAtRightMargin = false;
                _cursorColumn++;
                return;
            }
        }

        if (_line.Length == 0)
        {
            _lineRow = _cursorRow;
            _lineOriginColumn = _cursorColumn;
        }
        else if (_lineRow != _cursorRow)
        {
            if (_cursorRow > _lineRow)
            {
                CompleteLine(output);
            }
            else
            {
                InvalidateCandidate();
            }

            _lineRow = _cursorRow;
            _lineOriginColumn = _cursorColumn;
        }

        int relativeColumn = _cursorColumn - _lineOriginColumn;
        if (relativeColumn < 0)
        {
            _line.Insert(0, new string(' ', -relativeColumn));
            _lineOriginColumn = _cursorColumn;
            relativeColumn = 0;
        }

        while (_line.Length < relativeColumn)
        {
            _line.Append(' ');
        }

        if (relativeColumn < _line.Length)
        {
            _line[relativeColumn] = value;
        }
        else
        {
            _line.Append(value);
        }

        _cursorColumn++;
        _cursorAtRightMargin = _cursorColumn >= Volatile.Read(ref _viewportColumns) - 1;
    }

    private void MoveCursor(int row, int column, List<ExtractedText> output)
    {
        int viewportColumns = Volatile.Read(ref _viewportColumns);
        row = Math.Min(row, Volatile.Read(ref _viewportRows));
        bool scrollWrapContinuation = row < _cursorRow &&
            column >= viewportColumns - 1 &&
            _pendingLines.Count > 0 &&
            _previousLineReachedMargin;
        if (row > _cursorRow)
        {
            if (_line.Length > 0)
            {
                CompleteLine(output);
            }

            if (column == 0)
            {
                FlushPending(output, SourceBoundary.Line);
            }
        }
        else if (row < _cursorRow && !scrollWrapContinuation)
        {
            InvalidateCandidate();
        }

        _cursorRow = row;
        _cursorColumn = column;
        _cursorAtRightMargin = column >= viewportColumns - 1;
        if (_line.Length == 0)
        {
            _lineRow = row;
            _lineOriginColumn = column;
        }
    }

    private void ApplyEraseInLine(string sequence)
    {
        int mode = ParseParameters(sequence[..^1], defaultValue: 0)[0];
        if (mode == 2)
        {
            InvalidateCandidate();
            _lineRow = _cursorRow;
            _lineOriginColumn = _cursorColumn;
            return;
        }

        if (_lineRow != _cursorRow || _line.Length == 0)
        {
            return;
        }

        int relativeColumn = _cursorColumn - _lineOriginColumn;
        if (mode == 1)
        {
            int eraseThrough = Math.Min(_line.Length, Math.Max(0, relativeColumn + 1));
            for (int index = 0; index < eraseThrough; index++)
            {
                _line[index] = ' ';
            }
        }
        else if (relativeColumn <= 0)
        {
            _line.Clear();
            _lineOriginColumn = _cursorColumn;
        }
        else if (relativeColumn < _line.Length)
        {
            _line.Length = relativeColumn;
        }
    }

    private static int[] ParseParameters(string value, int defaultValue)
    {
        string normalized = value.TrimStart('?');
        if (normalized.Length == 0)
        {
            return [defaultValue];
        }

        return normalized.Split(';')
            .Select(part => int.TryParse(part, out int parsed) ? parsed : defaultValue)
            .ToArray();
    }

    private static int ParameterOrOne(string sequence) =>
        Math.Max(1, ParseParameters(sequence[..^1], defaultValue: 1)[0]);

    private void AdvanceLine()
    {
        _cursorRow = Math.Min(_cursorRow + 1, Volatile.Read(ref _viewportRows));
        _cursorColumn = 0;
        _cursorAtRightMargin = false;
        _lineRow = _cursorRow;
        _lineOriginColumn = 0;
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
        _lineRow = _cursorRow;
        _lineOriginColumn = _cursorColumn;
    }

    private void CompleteLine(List<ExtractedText> output)
    {
        if (_alternateBuffer)
        {
            _line.Clear();
            _lineRow = _cursorRow;
            _lineOriginColumn = _cursorColumn;
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
            _lineRow = _cursorRow;
            _lineOriginColumn = _cursorColumn;
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
        int displayWidth = _lineOriginColumn + GetDisplayWidth(rawLine);
        _previousLineReachedMargin = rawLine.Length > 0 && rawLine[^1] <= 0x7F &&
            displayWidth >= viewportColumns - 1 && displayWidth <= viewportColumns;
        _previousLineEndedWithWhitespace = rawLine.Length > 0 && char.IsWhiteSpace(rawLine[^1]);
        _currentLineContinuesPrevious = false;
        _currentLineStartedAtRightMargin = false;
        _lineRow = _cursorRow;
        _lineOriginColumn = _cursorColumn;
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
        string previous = _pendingLines[^1];
        int overlap = GetSuffixPrefixOverlap(previous, continuation);
        if (overlap > 0)
        {
            continuation = continuation[overlap..];
        }

        if (!startedAtRightMargin && _previousLineEndedWithWhitespace &&
            !previous.EndsWith(' ') && continuation.Length > 0 &&
            !char.IsWhiteSpace(continuation[0]))
        {
            _pendingLines[^1] += ' ';
        }

        _pendingLines[^1] += continuation;
    }

    private static int GetSuffixPrefixOverlap(string previous, string continuation)
    {
        int maximum = Math.Min(previous.Length, continuation.Length);
        for (int length = maximum; length > 0; length--)
        {
            if (previous.AsSpan(previous.Length - length, length)
                .SequenceEqual(continuation.AsSpan(0, length)))
            {
                return length;
            }
        }

        return 0;
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
