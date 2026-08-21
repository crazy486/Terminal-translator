namespace TerminalTranslator.Windows.Ipc;

public static class SessionHandshakeValidator
{
    public static bool IsValid(
        HelloMessage? hello,
        string expectedSessionId,
        string expectedNonce,
        IReadOnlyCollection<string> allowedRoles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedNonce);
        ArgumentNullException.ThrowIfNull(allowedRoles);
        return hello is not null &&
            string.Equals(hello.Type, "hello", StringComparison.Ordinal) &&
            hello.Protocol == SessionProtocol.Version &&
            string.Equals(hello.SessionId, expectedSessionId, StringComparison.Ordinal) &&
            string.Equals(hello.Nonce, expectedNonce, StringComparison.Ordinal) &&
            allowedRoles.Contains(hello.Role, StringComparer.Ordinal);
    }
}
