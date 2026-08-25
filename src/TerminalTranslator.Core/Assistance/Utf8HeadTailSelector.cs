using System.Text;

namespace TerminalTranslator.Core.Assistance;

public sealed record Utf8HeadTailSelection(
    string Head,
    string Tail,
    int HeadBytes,
    int TailBytes,
    long OriginalBytes)
{
    public string Combined => Head + Tail;
}

public static class Utf8HeadTailSelector
{
    public static Utf8HeadTailSelection Select(string text, int maximumPayloadBytes, bool preferNewline = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumPayloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        int original = Encoding.UTF8.GetByteCount(text);
        if (original <= maximumPayloadBytes)
            return new(text, string.Empty, original, 0, original);

        int headShare = maximumPayloadBytes / 2;
        int tailShare = maximumPayloadBytes - headShare;
        string head = TakeHead(text, headShare);
        string tail = TakeTail(text, tailShare);

        if (preferNewline)
        {
            int lastNewline = head.LastIndexOf('\n');
            if (lastNewline >= 0) head = head[..(lastNewline + 1)];
            int firstNewline = tail.IndexOf('\n');
            if (firstNewline >= 0 && firstNewline + 1 < tail.Length) tail = tail[(firstNewline + 1)..];
        }

        return new(head, tail, Encoding.UTF8.GetByteCount(head), Encoding.UTF8.GetByteCount(tail), original);
    }

    private static string TakeHead(string text, int byteLimit)
    {
        int used = 0;
        int chars = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int bytes = rune.Utf8SequenceLength;
            if (used + bytes > byteLimit) break;
            used += bytes;
            chars += rune.Utf16SequenceLength;
        }
        return text[..chars];
    }

    private static string TakeTail(string text, int byteLimit)
    {
        int used = 0;
        int start = text.Length;
        for (int index = text.Length; index > 0;)
        {
            int runeStart = index - 1;
            if (char.IsLowSurrogate(text[runeStart]) && runeStart > 0 && char.IsHighSurrogate(text[runeStart - 1])) runeStart--;
            Rune rune = Rune.GetRuneAt(text, runeStart);
            if (used + rune.Utf8SequenceLength > byteLimit) break;
            used += rune.Utf8SequenceLength;
            start = runeStart;
            index = runeStart;
        }
        return text[start..];
    }
}
