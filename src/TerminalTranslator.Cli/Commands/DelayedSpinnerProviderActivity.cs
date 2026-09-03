namespace TerminalTranslator.Cli.Commands;

internal sealed class DelayedSpinnerProviderActivity : IProviderActivity
{
    internal static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    private static readonly char[] Frames = ['/', '-', '\\', '|'];

    private readonly TextWriter _output;
    private readonly Func<bool> _isInteractive;
    private readonly TimeSpan _delay;
    private readonly TimeSpan _interval;

    internal DelayedSpinnerProviderActivity(
        TextWriter output,
        Func<bool> isInteractive,
        TimeSpan? delay = null,
        TimeSpan? interval = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _isInteractive = isInteractive ?? throw new ArgumentNullException(nameof(isInteractive));
        _delay = delay ?? DefaultDelay;
        _interval = interval ?? DefaultInterval;
        if (_delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!IsInteractive())
            return await operation(cancellationToken).ConfigureAwait(false);

        using CancellationTokenSource spinnerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task spinnerTask = AnimateAsync(spinnerCancellation.Token);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            spinnerCancellation.Cancel();
            try
            {
                await spinnerTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Presentation failures must never replace the provider result or failure.
            }
        }
    }

    private bool IsInteractive()
    {
        try
        {
            return _isInteractive();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task AnimateAsync(CancellationToken cancellationToken)
    {
        bool rendered = false;
        try
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            int index = 0;
            while (true)
            {
                if (rendered) _output.Write('\b');
                _output.Write(Frames[index]);
                rendered = true;
                _output.Flush();
                index = (index + 1) % Frames.Length;
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // A detached/closed console disables the animation without affecting the request.
        }
        finally
        {
            if (rendered)
            {
                try
                {
                    _output.Write("\b \b");
                    _output.Flush();
                }
                catch (Exception)
                {
                    // The output may disappear during shutdown; cleanup remains best-effort.
                }
            }
        }
    }
}
