using TerminalTranslator.Cli.Configuration;

namespace TerminalTranslator.Cli.Commands;

public sealed class ConsentPrompt(TextReader input, TextWriter output)
{
    public async Task<bool> ConfirmAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await output.WriteLineAsync($"External destination: {settings.Endpoint.Host}; model: {settings.Model}.").ConfigureAwait(false);
        await output.WriteLineAsync("Scope: eligible, non-sensitive English segments from this session only.").ConfigureAwait(false);
        await output.WriteLineAsync("Retention: terminal source and translations are not persisted.").ConfigureAwait(false);
        await output.WriteLineAsync("Suspected secrets cause the entire segment to be skipped.").ConfigureAwait(false);
        await output.WriteAsync("Enable translation? [y/N] ").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return response?.Trim() is string value &&
            (value.Equals("y", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
