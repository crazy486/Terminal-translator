namespace TerminalTranslator.Windows.ConPty;

public sealed class PseudoConsoleResizeMonitor(
    ConPtySession session,
    Func<(short Columns, short Rows)>? sizeReader = null,
    TimeSpan? interval = null)
{
    private readonly Func<(short Columns, short Rows)> _sizeReader = sizeReader ?? ReadConsoleSize;
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromMilliseconds(100);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        (short Columns, short Rows) previous = default;
        while (!cancellationToken.IsCancellationRequested)
        {
            (short Columns, short Rows) current = _sizeReader();
            if (current.Columns > 0 && current.Rows > 0 && current != previous)
            {
                session.Resize(current.Columns, current.Rows);
                previous = current;
            }

            await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static (short Columns, short Rows) ReadConsoleSize()
    {
        try
        {
            return ((short)Math.Clamp(global::System.Console.WindowWidth, 1, short.MaxValue),
                (short)Math.Clamp(global::System.Console.WindowHeight, 1, short.MaxValue));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return (120, 30);
        }
    }
}
