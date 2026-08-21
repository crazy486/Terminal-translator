using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Core.Tests.TestDoubles;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);

    public TimeSpan MonotonicNow { get; private set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Advance(delay);
        return Task.CompletedTask;
    }

    public void Advance(TimeSpan duration)
    {
        MonotonicNow += duration;
        UtcNow += duration;
    }
}

public sealed class FakeTranslationProvider(Func<TranslationRequest, CancellationToken, Task<TranslationResult>> handler)
    : ITranslationProvider
{
    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) =>
        handler(request, cancellationToken);
}

public sealed class TranslationProviderSpy : ITranslationProvider
{
    public List<TranslationRequest> Requests { get; } = [];

    public TranslationResult Result { get; set; } = new("翻译");

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}

public sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
