namespace TerminalTranslator.Windows.Console;

public interface INonBlockingAnalysisSink
{
    bool IsEnabled { get; }

    bool IsObserving => IsEnabled;

    bool TryOffer(ReadOnlyMemory<byte> bytes);

    bool TryObserve(ReadOnlyMemory<byte> bytes) => TryOffer(bytes);
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

            // The program pane is the primary path. The production sink advertises
            // IsObserving=false while disabled (FR-010); when enabled, its side copy
            // remains a non-blocking offer and can never delay this raw-output path.
            if (analysisSink?.IsObserving == true)
            {
                try
                {
                    _ = analysisSink.TryObserve(buffer.AsMemory(0, count).ToArray());
                }
                catch
                {
                    // Translation is an optional side path. A failed offer must never stop the
                    // ConPTY drain or alter bytes already forwarded to the program pane.
                }
            }
        }
    }
}
