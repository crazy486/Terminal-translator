namespace TerminalTranslator.Windows.Ipc;

public static class SessionPipeNames
{
    private const int NoncePrefixLength = 12;

    public static string Event(string sessionId, string nonce) =>
        $"tt-{Normalize(sessionId, nameof(sessionId))}-{NoncePrefix(nonce)}-events";

    public static string Control(string sessionId, string nonce) =>
        $"tt-{Normalize(sessionId, nameof(sessionId))}-{NoncePrefix(nonce)}-control";

    private static string NoncePrefix(string nonce)
    {
        string normalized = Normalize(nonce, nameof(nonce));
        if (normalized.Length < NoncePrefixLength)
        {
            throw new ArgumentException($"Nonce must contain at least {NoncePrefixLength} characters.", nameof(nonce));
        }

        return normalized[..NoncePrefixLength];
    }

    private static string Normalize(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
