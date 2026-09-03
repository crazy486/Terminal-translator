using System.Text.Json;

namespace TerminalTranslator.Windows.Capture;

internal static class CaptureCompletionDiagnostics
{
    internal const string EnabledEnvironmentVariable = "TT_CAPTURE_COMPLETION_DIAGNOSTICS";
    internal const string PathEnvironmentVariable = "TT_CAPTURE_COMPLETION_DIAGNOSTIC_PATH";

    private static readonly object Gate = new();

    internal static void Write(string stage, Guid sessionId, long sequence, object structuralState)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledEnvironmentVariable), "1", StringComparison.Ordinal))
            return;

        string? path = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            string line = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTime.UtcNow,
                processId = Environment.ProcessId,
                sessionId,
                sequence,
                stage,
                structuralState,
            });
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Test-only structural diagnostics must never affect capture availability.
        }
    }
}
