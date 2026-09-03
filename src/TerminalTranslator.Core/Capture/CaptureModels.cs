namespace TerminalTranslator.Core.Capture;

public enum LocalCaptureCompleteness
{
    Complete = 0,
    LocalHeadTail = 1,
}

public enum AiInputCompleteness
{
    Complete = 0,
    AiHeadTail = 2,
}

public enum CapturePreference
{
    Disabled = 0,
    Enabled = 1,
}

public enum CaptureHealthState
{
    Starting,
    Healthy,
    Unavailable,
    Recovering,
    Closing,
}

public enum PreviousCommandResultKind
{
    Success,
    NoPreviousCommand,
    NoOutput,
    CaptureDisabled,
    CaptureUnavailable,
    UnreliableOrCorrupt,
}

public sealed record CaptureSessionId
{
    public CaptureSessionId(Guid value, string nonce)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Session identity cannot be empty.", nameof(value));
        }

        if (string.IsNullOrWhiteSpace(nonce))
        {
            throw new ArgumentException("Session nonce cannot be empty.", nameof(nonce));
        }

        Value = value;
        Nonce = nonce;
    }

    public Guid Value { get; }

    public string Nonce { get; }
}

public sealed record CommandBoundary
{
    public CommandBoundary(
        CaptureSessionId session,
        long sequence,
        string commandText,
        bool isReliable,
        int? nativeExitCode,
        bool wasInterrupted,
        bool? powerShellSucceeded = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (string.IsNullOrWhiteSpace(commandText))
        {
            throw new ArgumentException("Command text is required.", nameof(commandText));
        }

        Session = session;
        Sequence = sequence;
        CommandText = commandText;
        IsReliable = isReliable;
        NativeExitCode = nativeExitCode;
        WasInterrupted = wasInterrupted;
        PowerShellSucceeded = powerShellSucceeded;
    }

    public CaptureSessionId Session { get; }
    public long Sequence { get; }
    public string CommandText { get; }
    public bool IsReliable { get; }
    public bool? PowerShellSucceeded { get; }
    public int? NativeExitCode { get; }
    public int? ExitCode => NativeExitCode;
    public bool WasInterrupted { get; }
}

public sealed record CapturedCommand
{
    public CapturedCommand(
        CaptureSessionId session,
        long sequence,
        string commandText,
        string output,
        CommandBoundary boundary,
        LocalCaptureCompleteness localCompleteness,
        long originalOutputBytes,
        bool isContextualAssistanceCommand,
        long historyId = 0)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(output);
        if (!boundary.IsReliable || boundary.Session != session || boundary.Sequence != sequence ||
            !string.Equals(boundary.CommandText, commandText, StringComparison.Ordinal))
        {
            throw new ArgumentException("Captured command must have a matching reliable boundary.", nameof(boundary));
        }

        if (originalOutputBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(originalOutputBytes));
        }
        if (historyId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(historyId));
        }

        Session = session;
        Sequence = sequence;
        CommandText = commandText;
        Output = output;
        Boundary = boundary;
        LocalCompleteness = localCompleteness;
        OriginalOutputBytes = originalOutputBytes;
        IsContextualAssistanceCommand = isContextualAssistanceCommand;
        HistoryId = historyId;
    }

    public CaptureSessionId Session { get; }
    public long Sequence { get; }
    public string CommandText { get; }
    public string Output { get; }
    public CommandBoundary Boundary { get; }
    public LocalCaptureCompleteness LocalCompleteness { get; }
    public long OriginalOutputBytes { get; }
    public bool IsContextualAssistanceCommand { get; }
    public long HistoryId { get; }
}

public sealed record PreviousCommandSnapshot(
    CaptureSessionId Session,
    long Sequence,
    string CommandText,
    string Output,
    int? NativeExitCode,
    bool WasInterrupted,
    LocalCaptureCompleteness LocalCompleteness,
    long OriginalOutputBytes)
{
    public bool? PowerShellSucceeded { get; init; }
    public int? ExitCode => NativeExitCode;
}

public sealed record PreviousCommandResult
{
    private PreviousCommandResult(PreviousCommandResultKind kind, PreviousCommandSnapshot? snapshot)
    {
        Kind = kind;
        Snapshot = snapshot;
    }

    public PreviousCommandResultKind Kind { get; }

    public PreviousCommandSnapshot? Snapshot { get; }

    public static PreviousCommandResult Success(PreviousCommandSnapshot snapshot) =>
        new(PreviousCommandResultKind.Success, snapshot ?? throw new ArgumentNullException(nameof(snapshot)));

    public static PreviousCommandResult NoPreviousCommand() => new(PreviousCommandResultKind.NoPreviousCommand, null);
    public static PreviousCommandResult NoOutput() => new(PreviousCommandResultKind.NoOutput, null);
    public static PreviousCommandResult CaptureDisabled() => new(PreviousCommandResultKind.CaptureDisabled, null);
    public static PreviousCommandResult CaptureUnavailable() => new(PreviousCommandResultKind.CaptureUnavailable, null);
    public static PreviousCommandResult UnreliableOrCorrupt() => new(PreviousCommandResultKind.UnreliableOrCorrupt, null);
}
