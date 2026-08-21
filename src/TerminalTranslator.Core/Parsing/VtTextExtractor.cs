using System.Text;
using TerminalTranslator.Core.Models;

namespace TerminalTranslator.Core.Parsing;

public sealed record ExtractedText(string Text, LayoutHints Layout, SourceBoundary Boundary);

public sealed class VtTextExtractor
{
    public const int MaximumCandidateBytes = 8 * 1024;

    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly List<string> _pendingLines = [];
    private bool _inEscape;
    private bool _inCsi;
    private bool _lastWasCarriageReturn;

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
            if (_inEscape)
            {
                if (!_inCsi)
                {
                    _inCsi = value == '[';
                    if (!_inCsi)
                    {
                        _inEscape = false;
                    }
                }
                else if (value is >= '@' and <= '~')
                {
                    _inEscape = false;
                    _inCsi = false;
                }

                continue;
            }

            if (value == '\u001b')
            {
                _inEscape = true;
                continue;
            }

            if (value == '\r')
            {
                CompleteLine(output);
                _lastWasCarriageReturn = true;
                continue;
            }

            if (value == '\n')
            {
                if (!_lastWasCarriageReturn)
                {
                    CompleteLine(output);
                }

                _lastWasCarriageReturn = false;
                continue;
            }

            _lastWasCarriageReturn = false;
            if (value == '\b')
            {
                if (_line.Length > 0)
                {
                    _line.Length--;
                }
            }
            else if (value == '\t')
            {
                _line.Append("    ");
            }
            else if (!char.IsControl(value))
            {
                _line.Append(value);
            }
        }

        if (_line.Length == 0)
        {
            FlushPending(output);
        }

        return output;
    }

    public IReadOnlyList<ExtractedText> Flush()
    {
        List<ExtractedText> output = [];
        if (_line.Length > 0)
        {
            CompleteLine(output);
        }

        FlushPending(output);
        return output;
    }

    private void CompleteLine(List<ExtractedText> output)
    {
        string line = _line.ToString().TrimEnd();
        _line.Clear();

        if (line.Length == 0)
        {
            FlushPending(output);
            return;
        }

        int indent = CountIndent(line);
        if (_pendingLines.Count > 0 && indent == 0 && !_pendingLines[^1].TrimEnd().EndsWith(':'))
        {
            FlushPending(output);
        }

        _pendingLines.Add(line);
    }

    private void FlushPending(List<ExtractedText> output)
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
                _pendingLines.Count == 1 ? SourceBoundary.Line : SourceBoundary.Block));
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
}
