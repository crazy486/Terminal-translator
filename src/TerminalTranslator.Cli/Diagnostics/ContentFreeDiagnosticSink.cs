using System.Globalization;
using System.Text.RegularExpressions;
using TerminalTranslator.Core.Sessions;

namespace TerminalTranslator.Cli.Diagnostics;

public sealed partial class ContentFreeDiagnosticSink(TextWriter writer) : IDiagnosticSink
{
    public void Record(string code, int count = 1, TimeSpan? duration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (!SafeCodeRegex().IsMatch(code))
        {
            throw new ArgumentException("Diagnostic code must be a content-free identifier.", nameof(code));
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        string durationField = duration is null
            ? string.Empty
            : $" duration-ms={duration.Value.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}";
        writer.WriteLine($"diagnostic: {code} count={count}{durationField}");
    }

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCodeRegex();
}
