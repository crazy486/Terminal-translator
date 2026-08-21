namespace TerminalTranslator.Windows.Console;

public interface INonBlockingAnalysisSink
{
    bool IsEnabled { get; }

    bool TryOffer(ReadOnlyMemory<byte> bytes);
}

public sealed class ConsoleOutputRelay(
    Stream pseudoConsoleOutput,
    Stream programOutput,
    INonBlockingAnalysisSink? analysisSink = null)
{
    public async Task CopyAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int count = await pseudoConsoleOutput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            await programOutput.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            await programOutput.FlushAsync(cancellationToken).ConfigureAwait(false);

            // The program pane is the primary path. Allocate and offer a side copy only while
            // translation is enabled, and never wait for the analysis consumer.
            if (analysisSink?.IsEnabled == true)
            {
                _ = analysisSink.TryOffer(buffer.AsMemory(0, count).ToArray());
            }
        }
    }
}
