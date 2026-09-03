using System.Text;
using System.Text.Json;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Windows.Capture;

public sealed record RetainedCommandMetadata(
    int Version,
    Guid SessionId,
    string SessionNonce,
    long Sequence,
    long HistoryId,
    string CommandText,
    Guid BoundarySessionId,
    string BoundarySessionNonce,
    long BoundarySequence,
    string BoundaryCommandText,
    bool BoundaryReliable,
    bool? PowerShellSucceeded,
    int? NativeExitCode,
    bool WasInterrupted,
    LocalCaptureCompleteness LocalCompleteness,
    long OriginalOutputBytes,
    bool IsContextualAssistanceCommand);

public static class RetainedCommandRecordCodec
{
    public const int CurrentVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] SerializeMetadata(CapturedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return JsonSerializer.SerializeToUtf8Bytes(new RetainedCommandMetadata(
            CurrentVersion,
            command.Session.Value,
            command.Session.Nonce,
            command.Sequence,
            command.HistoryId,
            command.CommandText,
            command.Boundary.Session.Value,
            command.Boundary.Session.Nonce,
            command.Boundary.Sequence,
            command.Boundary.CommandText,
            command.Boundary.IsReliable,
            command.Boundary.PowerShellSucceeded,
            command.Boundary.NativeExitCode,
            command.Boundary.WasInterrupted,
            command.LocalCompleteness,
            command.OriginalOutputBytes,
            command.IsContextualAssistanceCommand), JsonOptions);
    }

    public static CapturedCommand Deserialize(byte[] content, byte[] metadata)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);
        RetainedCommandMetadata persisted;
        try
        {
            persisted = JsonSerializer.Deserialize<RetainedCommandMetadata>(metadata, JsonOptions) ??
                throw new InvalidDataException("Retained command metadata is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Retained command metadata is corrupt.", exception);
        }

        if (persisted.Version != CurrentVersion || persisted.Sequence <= 0 ||
            persisted.SessionId == Guid.Empty || persisted.BoundarySessionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(persisted.SessionNonce) ||
            string.IsNullOrWhiteSpace(persisted.BoundarySessionNonce) ||
            string.IsNullOrWhiteSpace(persisted.CommandText) ||
            string.IsNullOrWhiteSpace(persisted.BoundaryCommandText) ||
            persisted.OriginalOutputBytes < 0 || persisted.HistoryId < 0)
        {
            throw new InvalidDataException("Retained command metadata is invalid.");
        }

        string output;
        try
        {
            output = new UTF8Encoding(false, true).GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Retained command content is not valid UTF-8.", exception);
        }

        CaptureSessionId session = new(persisted.SessionId, persisted.SessionNonce);
        CaptureSessionId boundarySession = new(persisted.BoundarySessionId, persisted.BoundarySessionNonce);
        CommandBoundary boundary = new(
            boundarySession,
            persisted.BoundarySequence,
            persisted.BoundaryCommandText,
            persisted.BoundaryReliable,
            persisted.NativeExitCode,
            persisted.WasInterrupted,
            persisted.PowerShellSucceeded);
        try
        {
            return new CapturedCommand(
                session,
                persisted.Sequence,
                persisted.CommandText,
                output,
                boundary,
                persisted.LocalCompleteness,
                persisted.OriginalOutputBytes,
                persisted.IsContextualAssistanceCommand,
                persisted.HistoryId);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Retained command identity or boundary is inconsistent.", exception);
        }
    }
}
