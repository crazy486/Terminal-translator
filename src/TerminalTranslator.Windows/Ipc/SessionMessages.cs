using TerminalTranslator.Core.Models;

namespace TerminalTranslator.Windows.Ipc;

public static class SessionProtocol
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 32 * 1024;
}

public sealed record HelloMessage(string Type, int Protocol, string SessionId, string Nonce, string Role);

public sealed record HelloAckMessage(string Type, int Protocol, string SessionId, string State);

public sealed record TranslationEventMessage(
    string Type,
    int Protocol,
    string SessionId,
    long Generation,
    ulong Sequence,
    string SourceText,
    string TranslatedText,
    LayoutHints Layout);

public sealed record StateEventMessage(string Type, int Protocol, string State, string ProviderHost, string Model);

public sealed record StatusEventMessage(string Type, int Protocol, string Code, int? Count = null, int? ExitCode = null);

public sealed record ControlRequestMessage(
    string Type,
    int Protocol,
    string SessionId,
    string? ProviderFingerprint = null,
    bool? Consent = null);

public sealed record ControlResultMessage(
    string Type,
    int Protocol,
    string Operation,
    bool Ok,
    string State,
    long? Generation = null);

public sealed record StatusResultMessage(
    string Type,
    int Protocol,
    string State,
    string ProviderHost,
    string Model,
    int HighQueued,
    int NormalQueued,
    int PrivacySkipped,
    int OverloadDropped);
