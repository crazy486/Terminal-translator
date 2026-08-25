using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Cli.Commands;

internal sealed class ProductionPreviousCommandRetriever(
    CapturePreferenceStore preferenceStore,
    string captureRoot,
    Func<string, string?> environmentReader,
    Func<(bool Success, CaptureOwnerIdentity? Owner)> directOwnerReader)
{
    public async Task<PreviousCommandResult> RetrieveAsync(CancellationToken cancellationToken)
    {
        CapturePreference preference;
        try
        {
            preference = await preferenceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return PreviousCommandResult.CaptureUnavailable();
        }

        if (preference == CapturePreference.Disabled)
            return PreviousCommandResult.CaptureDisabled();

        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        string? sessionText = environmentReader(CaptureSessionIdentity.SessionEnvironmentVariable);
        string? nonceText = environmentReader(CaptureSessionIdentity.NonceEnvironmentVariable);
        if (sessionText is not null) environment[CaptureSessionIdentity.SessionEnvironmentVariable] = sessionText;
        if (nonceText is not null) environment[CaptureSessionIdentity.NonceEnvironmentVariable] = nonceText;
        if (!CaptureSessionIdentity.TryReadEnvironment(environment, out Guid sessionId, out string? nonce))
            return PreviousCommandResult.CaptureUnavailable();

        (bool ownerSuccess, CaptureOwnerIdentity? owner) = directOwnerReader();
        if (!ownerSuccess || owner is null)
            return PreviousCommandResult.UnreliableOrCorrupt();

        string sessionDirectory = Path.Combine(captureRoot, sessionId.ToString("N"));
        try
        {
            CaptureOwnerManifest manifest = await CaptureSessionBootstrap.ReadOwnerManifestAsync(
                sessionDirectory, cancellationToken).ConfigureAwait(false);
            if (!CaptureSessionIdentity.Validate(
                    manifest.Proof,
                    sessionId,
                    nonce!,
                    owner,
                    CaptureSessionBootstrap.CurrentIntegrationVersion))
                return PreviousCommandResult.UnreliableOrCorrupt();

            if (manifest.HealthState != CaptureHealthState.Healthy)
                return PreviousCommandResult.CaptureUnavailable();

            RetainedCaptureStore store = new(sessionDirectory);
            RetainedGeneration? generation = await store.LoadCommittedAsync(cancellationToken).ConfigureAwait(false);
            List<CapturedCommand> records = [];
            if (generation is not null)
            {
                foreach (RetainedRecordDescriptor descriptor in generation.Records.OrderBy(record => record.Sequence))
                {
                    CapturedCommand record = RetainedCommandRecordCodec.Deserialize(
                        await store.ReadContentAsync(descriptor, cancellationToken).ConfigureAwait(false),
                        await store.ReadMetadataAsync(descriptor, cancellationToken).ConfigureAwait(false));
                    if (record.Sequence != descriptor.Sequence)
                        return PreviousCommandResult.UnreliableOrCorrupt();
                    records.Add(record);
                }
            }

            return PreviousCommandRetriever.Retrieve(new PreviousCommandRetrievalState(
                preference,
                manifest.HealthState,
                new CaptureSessionId(sessionId, nonce!),
                records,
                IsStoreReliable: true));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return PreviousCommandResult.UnreliableOrCorrupt();
        }
    }
}
