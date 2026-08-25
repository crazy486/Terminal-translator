namespace TerminalTranslator.Core.Capture;

public sealed record RetentionRecord
{
    public RetentionRecord(string recordId, long sequence, long logicalBytes)
    {
        if (string.IsNullOrWhiteSpace(recordId) || sequence <= 0 || logicalBytes < 0)
        {
            throw new ArgumentException("Retention record facts are invalid.");
        }

        RecordId = recordId;
        Sequence = sequence;
        LogicalBytes = logicalBytes;
    }

    public string RecordId { get; }
    public long Sequence { get; }
    public long LogicalBytes { get; }
}

public sealed record CaptureRetentionPlan(
    bool CanFit,
    IReadOnlyList<RetentionRecord> RetainedRecords,
    IReadOnlyList<string> EvictedRecordIds,
    long RetainedBytes);

public static class CaptureRetentionPolicy
{
    public const long MaximumRetainedBytes = 10_180_000;

    public static CaptureRetentionPlan Plan(
        IReadOnlyList<RetentionRecord> existing,
        RetentionRecord candidate,
        Func<IReadOnlyList<RetentionRecord>, long> manifestSizer)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(manifestSizer);
        if (existing.Any(record => record.Sequence >= candidate.Sequence) ||
            existing.Select(record => record.RecordId).Append(candidate.RecordId).Distinct(StringComparer.Ordinal).Count() != existing.Count + 1)
        {
            throw new ArgumentException("Candidate must be uniquely newer than every retained record.", nameof(candidate));
        }

        List<RetentionRecord> proposed = existing.OrderBy(record => record.Sequence).Append(candidate).ToList();
        long candidateOnly = Measure([candidate], manifestSizer);
        if (candidateOnly > MaximumRetainedBytes)
        {
            return new CaptureRetentionPlan(false, existing.ToArray(), [], Measure(existing, manifestSizer));
        }

        List<string> evicted = [];
        while (Measure(proposed, manifestSizer) > MaximumRetainedBytes && proposed.Count > 1)
        {
            RetentionRecord oldest = proposed[0];
            proposed.RemoveAt(0);
            evicted.Add(oldest.RecordId);
        }

        long retainedBytes = Measure(proposed, manifestSizer);
        return new CaptureRetentionPlan(
            retainedBytes <= MaximumRetainedBytes,
            proposed,
            evicted,
            retainedBytes);
    }

    private static long Measure(
        IReadOnlyList<RetentionRecord> records,
        Func<IReadOnlyList<RetentionRecord>, long> manifestSizer)
    {
        long manifestBytes = manifestSizer(records);
        if (manifestBytes < 0)
        {
            throw new InvalidDataException("Manifest byte count cannot be negative.");
        }

        return records.Aggregate(manifestBytes, (total, record) => checked(total + record.LogicalBytes));
    }
}
