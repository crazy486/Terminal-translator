using System.Text.RegularExpressions;

namespace TerminalTranslator.Windows.Capture;

/// <summary>
/// Extracts a completed command interval from the framework envelope written by Windows
/// PowerShell 5.1 transcription. Envelope recognition is structural: the opening/header and
/// closing/footer blocks are bounded by the transcript's matching separator lines. Localized
/// labels and timestamps are deliberately opaque.
/// </summary>
internal static class PowerShellTranscriptEnvelopeParser
{
    public static bool TryExtractOrderedOutput(
        string transcript,
        string commandText,
        bool hasNativeFileRedirection,
        out string output,
        out CaptureBoundaryFailureDetail failureDetail)
    {
        output = string.Empty;
        failureDetail = CaptureBoundaryFailureDetail.TranscriptEnvelopeMismatch;
        if (string.IsNullOrEmpty(transcript) || string.IsNullOrWhiteSpace(commandText))
            return false;

        IReadOnlyList<TranscriptLine> lines = ReadLines(transcript);
        int openingSeparator = FindFirstNonEmpty(lines, transcript);
        int closingSeparator = FindLastNonEmpty(lines, transcript);
        if (openingSeparator < 0 || closingSeparator <= openingSeparator ||
            !TryGetSeparator(lines[openingSeparator], transcript, out string separator) ||
            !LineEquals(lines[closingSeparator], transcript, separator))
        {
            return false;
        }

        int headerEnd = FindMatchingSeparator(lines, transcript, separator, openingSeparator + 1, closingSeparator);
        int footerStart = FindMatchingSeparatorReverse(lines, transcript, separator, closingSeparator - 1, headerEnd);
        if (headerEnd <= openingSeparator || footerStart <= headerEnd ||
            CountNonEmpty(lines, transcript, openingSeparator + 1, headerEnd) == 0 ||
            CountNonEmpty(lines, transcript, footerStart + 1, closingSeparator) < 2)
        {
            return false;
        }

        int bodyStart = lines[headerEnd].End;
        int bodyEnd = lines[footerStart].Start;
        int commandStart = FindSubmittedCommandEcho(transcript, commandText, bodyStart, bodyEnd);
        if (commandStart < 0)
        {
            failureDetail = CaptureBoundaryFailureDetail.CommandIdentityMismatch;
            return false;
        }

        int afterCommand = commandStart + commandText.Length;
        if (afterCommand > bodyEnd)
        {
            failureDetail = CaptureBoundaryFailureDetail.CommandIdentityMismatch;
            return false;
        }

        output = transcript[afterCommand..bodyEnd].TrimStart('\r', '\n').TrimEnd('\r', '\n');
        if (hasNativeFileRedirection)
        {
            // AlwaysCaptureApplicationIO routes native stdout through PowerShell's joined reader
            // path. For a native file redirection, Windows PowerShell 5.1 transcribes its own
            // Out-File parameter-binding trace even though the user bytes correctly went only to
            // the file. It is framework plumbing, not command output.
            output = Regex.Replace(
                output,
                @"(?m)^(?:PS>|>> )ParameterBinding\(Out-File\):[^\r\n]*(?:\r?\n|$)",
                string.Empty,
                RegexOptions.CultureInvariant).TrimEnd('\r', '\n');
        }
        failureDetail = CaptureBoundaryFailureDetail.None;
        return true;
    }

    private static int FindSubmittedCommandEcho(
        string transcript,
        string commandText,
        int bodyStart,
        int bodyEnd)
    {
        // A transcript interval opens before prompt presentation. Its body therefore begins in
        // AwaitingSubmittedEcho state: prompt presentation may precede the echo, but the submitted
        // command completes a console-input line before command output begins. Once that first
        // line-aligned identity is observed, every later occurrence belongs to output (for example
        // PowerShell ErrorRecord source reproduction) and cannot make the echo ambiguous.
        int searchStart = bodyStart;
        while (searchStart < bodyEnd)
        {
            int candidate = transcript.IndexOf(
                commandText,
                searchStart,
                bodyEnd - searchStart,
                StringComparison.Ordinal);
            if (candidate < 0) return -1;

            int afterCandidate = candidate + commandText.Length;
            if (afterCandidate == bodyEnd || transcript[afterCandidate] is '\r' or '\n')
                return candidate;

            searchStart = candidate + 1;
        }

        return -1;
    }

    private static IReadOnlyList<TranscriptLine> ReadLines(string value)
    {
        List<TranscriptLine> lines = [];
        int start = 0;
        while (start < value.Length)
        {
            int contentEnd = start;
            while (contentEnd < value.Length && value[contentEnd] is not '\r' and not '\n')
                contentEnd++;

            int end = contentEnd;
            if (end < value.Length && value[end] == '\r') end++;
            if (end < value.Length && value[end] == '\n') end++;
            lines.Add(new TranscriptLine(start, contentEnd, end));
            start = end;
        }

        return lines;
    }

    private static int FindFirstNonEmpty(IReadOnlyList<TranscriptLine> lines, string value)
    {
        for (int index = 0; index < lines.Count; index++)
        {
            if (!IsEmpty(lines[index], value)) return index;
        }

        return -1;
    }

    private static int FindLastNonEmpty(IReadOnlyList<TranscriptLine> lines, string value)
    {
        for (int index = lines.Count - 1; index >= 0; index--)
        {
            if (!IsEmpty(lines[index], value)) return index;
        }

        return -1;
    }

    private static int FindMatchingSeparator(
        IReadOnlyList<TranscriptLine> lines,
        string value,
        string separator,
        int start,
        int endExclusive)
    {
        for (int index = start; index < endExclusive; index++)
        {
            if (LineEquals(lines[index], value, separator)) return index;
        }

        return -1;
    }

    private static int FindMatchingSeparatorReverse(
        IReadOnlyList<TranscriptLine> lines,
        string value,
        string separator,
        int start,
        int endExclusive)
    {
        for (int index = start; index > endExclusive; index--)
        {
            if (LineEquals(lines[index], value, separator)) return index;
        }

        return -1;
    }

    private static int CountNonEmpty(
        IReadOnlyList<TranscriptLine> lines,
        string value,
        int start,
        int endExclusive)
    {
        int count = 0;
        for (int index = start; index < endExclusive; index++)
        {
            if (!IsEmpty(lines[index], value)) count++;
        }

        return count;
    }

    private static bool TryGetSeparator(TranscriptLine line, string value, out string separator)
    {
        ReadOnlySpan<char> content = value.AsSpan(line.Start, line.ContentEnd - line.Start);
        if (content.Length < 8 || content.IndexOfAnyExcept('*') >= 0)
        {
            separator = string.Empty;
            return false;
        }

        separator = content.ToString();
        return true;
    }

    private static bool LineEquals(TranscriptLine line, string value, string expected) =>
        value.AsSpan(line.Start, line.ContentEnd - line.Start).SequenceEqual(expected);

    private static bool IsEmpty(TranscriptLine line, string value) =>
        value.AsSpan(line.Start, line.ContentEnd - line.Start).Trim().IsEmpty;

    private readonly record struct TranscriptLine(int Start, int ContentEnd, int End);
}
