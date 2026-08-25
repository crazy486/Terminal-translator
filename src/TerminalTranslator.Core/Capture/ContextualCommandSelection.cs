namespace TerminalTranslator.Core.Capture;

public static class ContextualCommandSelection
{
    public static bool IsContextual(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText)) return false;
        string normalized = string.Join(' ', commandText.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Equals("tt last", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("tt ask last", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("tt ask last ", StringComparison.OrdinalIgnoreCase);
    }

    public static CapturedCommand? SelectStrictTarget(IReadOnlyList<CapturedCommand> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        for (int index = records.Count - 1; index >= 0; index--)
        {
            CapturedCommand record = records[index];
            if (record.IsContextualAssistanceCommand || IsContextual(record.CommandText)) continue;
            return record;
        }
        return null;
    }
}
