namespace TerminalTranslator.Windows.Console;

public sealed class ConsoleInputRelay(Stream programInput, Stream pseudoConsoleInput)
{
    public async Task CopyAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await programInput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            await pseudoConsoleInput.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            await pseudoConsoleInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
