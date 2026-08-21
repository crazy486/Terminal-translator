namespace TerminalTranslator.Core.Sessions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    TimeSpan MonotonicNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemClock : IClock
{
    private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeSpan MonotonicNow => System.Diagnostics.Stopwatch.GetElapsedTime(_started);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
