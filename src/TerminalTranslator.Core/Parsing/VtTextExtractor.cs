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
    private ParserState _state;
    private ParserState _controlStringState;
    private bool _pendingCarriageReturn;
    private bool _alternateBuffer;

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

        if (_state == ParserState.Normal && !_pendingCarriageReturn && _line.Length == 0)
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
            _pendingCarriageReturn = false;
            if (value == '\n')
            {
                CompleteLine(output);
                return;
            }

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
    }

    private void CompleteLine(List<ExtractedText> output)
    {
        if (_alternateBuffer)
        {
            _line.Clear();
            return;
        }

        string line = _line.ToString().TrimEnd();
        _line.Clear();
        if (line.Length == 0)
        {
            FlushPending(output, SourceBoundary.Line);
            return;
        }

        int indent = CountIndent(line);
        if (_pendingLines.Count > 0 && indent == 0 && !_pendingLines[^1].TrimEnd().EndsWith(':'))
        {
            FlushPending(output, SourceBoundary.Line);
        }

        _pendingLines.Add(line);
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

    private enum ParserState { Normal, Escape, Csi, Osc, Dcs, ControlStringEscape }
}
