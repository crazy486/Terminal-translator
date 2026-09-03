using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Windows.Capture;

public enum FinalizationOutcome
{
    Published,
    RejectedUnreliable,
    RequiresLocalReduction,
    Failed,
}

public sealed record CapturedCommandCandidate(
    string RecordId,
    long Sequence,
    byte[] Content,
    byte[] Metadata,
    bool IsReliable,
    CapturedCommand? Command = null);

public sealed record FinalizationResult(
    FinalizationOutcome Outcome,
    bool CommitOccurred,
    IReadOnlyList<string> ResiduePaths,
    RetainedGeneration? Generation);

public sealed class CapturedCommandFinalizer(
    RetainedCaptureStore store,
    FinalizationResidueManager? residueManager = null)
{
    public async Task<FinalizationResult> FinalizeAsync(
        CapturedCommandCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (residueManager is not null && !residueManager.CanStartCapture)
        {
            return new FinalizationResult(FinalizationOutcome.Failed, false, [], null);
        }

        if (!candidate.IsReliable || candidate.Sequence <= 0)
        {
            return new FinalizationResult(FinalizationOutcome.RejectedUnreliable, false, [], null);
        }

        try
        {
            RetainedGeneration? current = await store.LoadCommittedAsync(cancellationToken).ConfigureAwait(false);
            List<RetainedRecordWrite> writes = [];
            if (current is not null)
            {
                foreach (RetainedRecordDescriptor descriptor in current.Records.OrderBy(record => record.Sequence))
                {
                    writes.Add(new RetainedRecordWrite(
                        descriptor.RecordId,
                        descriptor.Sequence,
                        await store.ReadContentAsync(descriptor, cancellationToken).ConfigureAwait(false),
                        await store.ReadMetadataAsync(descriptor, cancellationToken).ConfigureAwait(false)));
                }
            }

            if (writes.Count > 0 && writes[^1].Sequence >= candidate.Sequence)
            {
                return new FinalizationResult(FinalizationOutcome.RejectedUnreliable, false, [], null);
            }

            RetainedRecordWrite candidateWrite = new(candidate.RecordId, candidate.Sequence, candidate.Content, candidate.Metadata);
            Dictionary<string, RetainedRecordWrite> byId = writes.Append(candidateWrite)
                .ToDictionary(record => record.RecordId, StringComparer.Ordinal);
            List<RetentionRecord> existing = writes
                .Select(record => new RetentionRecord(record.RecordId, record.Sequence, record.Content.LongLength + record.Metadata.LongLength))
                .ToList();
            RetentionRecord candidateSize = new(
                candidate.RecordId,
                candidate.Sequence,
                candidate.Content.LongLength + candidate.Metadata.LongLength);
            long nextGeneration = checked((current?.Generation ?? 0) + 1);
            CaptureRetentionPlan plan = CaptureRetentionPolicy.Plan(
                existing,
                candidateSize,
                selected =>
                {
                    RetainedRecordWrite[] selectedWrites = selected.Select(record => byId[record.RecordId]).ToArray();
                    long dataBytes = selected.Sum(record => record.LogicalBytes);
                    return checked(store.EstimateRetainedBytes(selectedWrites, nextGeneration) - dataBytes);
                });
            if (!plan.CanFit && candidate.Command is not null)
            {
                candidateWrite = CreateLocallyReducedWrite(candidate, store, nextGeneration);
                byId[candidate.RecordId] = candidateWrite;
                candidateSize = new RetentionRecord(
                    candidate.RecordId,
                    candidate.Sequence,
                    candidateWrite.Content.LongLength + candidateWrite.Metadata.LongLength);
                plan = CaptureRetentionPolicy.Plan(
                    existing,
                    candidateSize,
                    selected =>
                    {
                        RetainedRecordWrite[] selectedWrites = selected.Select(record => byId[record.RecordId]).ToArray();
                        long dataBytes = selected.Sum(record => record.LogicalBytes);
                        return checked(store.EstimateRetainedBytes(selectedWrites, nextGeneration) - dataBytes);
                    });
            }

            if (!plan.CanFit)
            {
                return new FinalizationResult(FinalizationOutcome.RequiresLocalReduction, false, [], current);
            }

            RetainedRecordWrite[] proposed = plan.RetainedRecords.Select(record => byId[record.RecordId]).ToArray();
            RetainedGeneration published = await store.PublishAsync(proposed, cancellationToken).ConfigureAwait(false);
            return new FinalizationResult(FinalizationOutcome.Published, true, [], published);
        }
        catch (RetainedPublicationException exception)
        {
            CaptureCompletionDiagnostics.Write("retention-failure", Guid.Empty, candidate.Sequence, new
            {
                failureType = exception.GetType().Name,
                innerFailureType = exception.InnerException?.GetType().Name,
                hResult = exception.HResult,
                commitOccurred = exception.CommitOccurred,
                residueCount = exception.ResiduePaths.Count,
            });
            if (residueManager is not null && exception.ResiduePaths.Count > 0)
            {
                try
                {
                    _ = await residueManager.TryRegisterAsync(exception.ResiduePaths, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The finalization result remains failed and capture must stay unavailable.
                }
            }

            return new FinalizationResult(FinalizationOutcome.Failed, exception.CommitOccurred, exception.ResiduePaths, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            CaptureCompletionDiagnostics.Write("retention-failure", Guid.Empty, candidate.Sequence, new
            {
                failureType = exception.GetType().Name,
                hResult = exception.HResult,
                commitOccurred = false,
                residueCount = 0,
            });
            return new FinalizationResult(FinalizationOutcome.Failed, false, [], null);
        }
    }

    private static RetainedRecordWrite CreateLocallyReducedWrite(
        CapturedCommandCandidate candidate,
        RetainedCaptureStore store,
        long generation)
    {
        CapturedCommand source = candidate.Command!;
        if (source.Sequence != candidate.Sequence ||
            !candidate.Content.AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(source.Output)))
        {
            throw new InvalidDataException("Finalization candidate does not match its trusted command model.");
        }

        CapturedCommand metadataShape = new(
            source.Session,
            source.Sequence,
            source.CommandText,
            string.Empty,
            source.Boundary,
            LocalCaptureCompleteness.LocalHeadTail,
            source.OriginalOutputBytes > 0
                ? source.OriginalOutputBytes
                : System.Text.Encoding.UTF8.GetByteCount(source.Output),
            source.IsContextualAssistanceCommand,
            source.HistoryId);
        byte[] metadata = RetainedCommandRecordCodec.SerializeMetadata(metadataShape);
        RetainedRecordWrite empty = new(candidate.RecordId, candidate.Sequence, [], metadata);
        long nonContentBytes = checked(store.EstimateRetainedBytes([empty], generation) - metadata.LongLength);
        long effectiveMaximum = CaptureRetentionPolicy.MaximumRetainedBytes;

        for (int attempt = 0; attempt < 16; attempt++)
        {
            LocalRetentionResult reduced = OversizedCommandRetention.Create(
                source,
                metadata.LongLength,
                indexBytes: 0,
                manifestBytes: nonContentBytes,
                maximumBytes: effectiveMaximum);
            if (!reduced.Supported || reduced.Command is null)
                throw new InvalidDataException("Essential retained command metadata cannot fit within the hard limit.");

            metadata = RetainedCommandRecordCodec.SerializeMetadata(reduced.Command);
            RetainedRecordWrite write = new(
                candidate.RecordId,
                candidate.Sequence,
                System.Text.Encoding.UTF8.GetBytes(reduced.Command.Output),
                metadata);
            long measured = store.EstimateRetainedBytes([write], generation);
            if (measured <= CaptureRetentionPolicy.MaximumRetainedBytes)
                return write;

            effectiveMaximum = checked(effectiveMaximum - (measured - CaptureRetentionPolicy.MaximumRetainedBytes));
        }

        throw new InvalidDataException("Locally reduced retained command accounting did not converge.");
    }
}
