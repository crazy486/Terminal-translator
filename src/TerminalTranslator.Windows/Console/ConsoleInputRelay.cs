namespace TerminalTranslator.Windows.Console;

public sealed class ConsoleInputRelay(
    Stream programInput,
    Stream pseudoConsoleInput,
    Action<ReadOnlyMemory<byte>>? inputObserver = null)
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

            try
            {
                inputObserver?.Invoke(buffer.AsMemory(0, count));
            }
            catch
            {
                // Input forwarding is the primary path. Analysis ownership tracking
                // is optional and must never prevent bytes from reaching the ConPTY.
            }

            await pseudoConsoleInput.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            await pseudoConsoleInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
