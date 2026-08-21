using TerminalTranslator.Core.Models;

namespace TerminalTranslator.Core.Sessions;

public sealed class SessionTeardown(TranslationSession session, IClock clock)
{
    public async Task ExecuteAsync(
        string reason,
        int? exitCode,
        bool abnormal,
        Func<CancellationToken, Task> drainRawOutput,
        Func<StatusEvent, CancellationToken, ValueTask>? statusSink,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(drainRawOutput);

        session.BeginStopping();
        try
        {
            await drainRawOutput(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            session.Fault();
            throw;
        }

        if (abnormal)
        {
            session.Fault();
        }
        else
        {
            session.End();
        }

        if (statusSink is not null)
        {
            StatusEvent ended = new(
                session.SessionId,
                StatusKind.SessionEnded,
                reason,
                exitCode,
                clock.UtcNow);
            try
            {
                await statusSink(ended, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }
}
