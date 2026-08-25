using System.Text;
using TerminalTranslator.Core.Assistance;

namespace TerminalTranslator.Core.Capture;

public sealed record LocalRetentionResult(bool Supported, CapturedCommand? Command, long RetainedLogicalBytes);

public static class OversizedCommandRetention
{
    public static LocalRetentionResult Create(
        CapturedCommand source,
        long metadataBytes,
        long indexBytes,
        long manifestBytes,
        long maximumBytes = CaptureRetentionPolicy.MaximumRetainedBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (metadataBytes < 0 || indexBytes < 0 || manifestBytes < 0 || maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(metadataBytes));
        long essential = checked(metadataBytes + indexBytes + manifestBytes);
        if (essential >= maximumBytes || maximumBytes - essential > int.MaxValue)
            return new(false, null, essential);

        int originalBytes = Encoding.UTF8.GetByteCount(source.Output);
        long completeBytes = checked(essential + originalBytes);
        if (completeBytes <= maximumBytes)
            return new(true, source, completeBytes);

        int allowance = checked((int)(maximumBytes - essential));
        Utf8HeadTailSelection selection = Utf8HeadTailSelector.Select(source.Output, allowance);
        CapturedCommand truncated = new(
            source.Session,
            source.Sequence,
            source.CommandText,
            selection.Combined,
            source.Boundary,
            LocalCaptureCompleteness.LocalHeadTail,
            source.OriginalOutputBytes > 0 ? source.OriginalOutputBytes : originalBytes,
            source.IsContextualAssistanceCommand,
            source.HistoryId);
        long retained = checked(essential + selection.HeadBytes + selection.TailBytes);
        return retained <= maximumBytes ? new(true, truncated, retained) : new(false, null, retained);
    }
}
