using System.Text;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Core.Assistance;

namespace TerminalTranslator.Cli.Tests.Unit;

[TestClass]
public sealed class DelayedSpinnerProviderActivityTests
{
    private static readonly TimeSpan TestDelay = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan TestInterval = TimeSpan.FromMilliseconds(10);

    [TestMethod]
    public async Task FastOperation_CompletesBeforeDelay_AndNeverRenders()
    {
        RecordingWriter output = new();
        DelayedSpinnerProviderActivity activity = Create(output);

        int result = await activity.RunAsync(_ => Task.FromResult(42), CancellationToken.None);

        Assert.AreEqual(42, result);
        Assert.AreEqual(string.Empty, output.Snapshot);
    }

    [TestMethod]
    public async Task SlowOperation_RendersAFrame_AndCleansItOnCompletion()
    {
        RecordingWriter output = new();
        DelayedSpinnerProviderActivity activity = Create(output);
        TaskCompletionSource<int> provider = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> running = activity.RunAsync(_ => provider.Task, CancellationToken.None);
        await WaitUntilAsync(() => output.Snapshot.Contains('/'));
        provider.SetResult(7);

        Assert.AreEqual(7, await running);
        StringAssert.Contains(output.Snapshot, "/");
        StringAssert.EndsWith(output.Snapshot, "\b \b");
    }

    [TestMethod]
    public async Task Completion_CleansSpinnerBeforeFinalResultIsRendered()
    {
        RecordingWriter output = new();
        TaskCompletionSource<bool> provider = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = LastCommand.Create(
            async (activity, cancellationToken) =>
            {
                await activity.RunAsync(_ => provider.Task, cancellationToken);
                return new LastAssistanceOutcome(AssistanceFailureKind.NoTranslatableEnglish);
            },
            output,
            writer => new DelayedSpinnerProviderActivity(writer, () => true, TestDelay, TestInterval));

        Task<int> running = command.Parse([]).InvokeAsync();
        await WaitUntilAsync(() => output.Snapshot.Contains('/'));
        provider.SetResult(true);

        Assert.AreEqual(0, await running);
        StringAssert.Contains(output.Snapshot, "\b \bNo translatable English content was found.");
    }

    [TestMethod]
    public async Task FailedOperation_StillStopsAndCleansSpinner()
    {
        RecordingWriter output = new();
        DelayedSpinnerProviderActivity activity = Create(output);
        TaskCompletionSource<int> provider = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> running = activity.RunAsync(_ => provider.Task, CancellationToken.None);
        await WaitUntilAsync(() => output.Snapshot.Contains('/'));
        provider.SetException(new InvalidOperationException("provider failed"));

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => running);
        Assert.AreEqual("provider failed", exception.Message);
        StringAssert.EndsWith(output.Snapshot, "\b \b");
    }

    [TestMethod]
    public async Task CanceledOperation_TerminatesSpinnerWithoutBackgroundWrites()
    {
        RecordingWriter output = new();
        DelayedSpinnerProviderActivity activity = Create(output);
        using CancellationTokenSource cancellation = new();

        Task<int> running = activity.RunAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            cancellation.Token);
        await WaitUntilAsync(() => output.Snapshot.Contains('/'));
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => running);
        string stoppedOutput = output.Snapshot;
        await Task.Delay(TestInterval + TestInterval);
        Assert.AreEqual(stoppedOutput, output.Snapshot);
        StringAssert.EndsWith(stoppedOutput, "\b \b");
    }

    [TestMethod]
    public async Task ProviderIgnoringCancellation_CommandCancellationStillStopsSpinnerImmediately()
    {
        RecordingWriter output = new();
        DelayedSpinnerProviderActivity activity = Create(output);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<int> provider = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> running = activity.RunAsync(_ => provider.Task, cancellation.Token);
        await WaitUntilAsync(() => output.Snapshot.Contains('/'));
        cancellation.Cancel();
        await WaitUntilAsync(() => output.Snapshot.EndsWith("\b \b", StringComparison.Ordinal));
        string stoppedOutput = output.Snapshot;

        await Task.Delay(TestInterval + TestInterval);
        Assert.AreEqual(stoppedOutput, output.Snapshot);
        Assert.IsFalse(running.IsCompleted);
        provider.SetResult(3);
        Assert.AreEqual(3, await running);
    }

    [TestMethod]
    public async Task NonInteractiveOutput_DoesNotRenderAnimationOrControlCharacters()
    {
        RecordingWriter output = new();
        DelayedSpinnerProviderActivity activity = new(
            output,
            () => false,
            TestDelay,
            TestInterval);

        await activity.RunAsync(
            async _ =>
            {
                await Task.Delay(TestDelay + TestDelay);
                return true;
            },
            CancellationToken.None);

        Assert.AreEqual(string.Empty, output.Snapshot);
    }

    [TestMethod]
    public async Task InjectedOutput_DefaultsToNonInteractiveAndPreservesFinalOutputContract()
    {
        RecordingWriter output = new();
        var command = LastCommand.Create(
            async (activity, cancellationToken) =>
            {
                await activity.RunAsync(
                    async _ =>
                    {
                        await Task.Delay(DelayedSpinnerProviderActivity.DefaultDelay + TestDelay);
                        return true;
                    },
                    cancellationToken);
                return new LastAssistanceOutcome(AssistanceFailureKind.NoTranslatableEnglish);
            },
            output);

        Assert.AreEqual(0, await command.Parse([]).InvokeAsync());
        Assert.AreEqual(
            $"No translatable English content was found.{Environment.NewLine}",
            output.Snapshot);
    }

    [TestMethod]
    public async Task SpinnerWriterFailure_DoesNotReplaceOperationResult()
    {
        DelayedSpinnerProviderActivity activity = new(
            new ThrowingWriter(),
            () => true,
            TimeSpan.Zero,
            TestInterval);

        int result = await activity.RunAsync(
            async _ =>
            {
                await Task.Delay(TestInterval + TestInterval);
                return 11;
            },
            CancellationToken.None);

        Assert.AreEqual(11, result);
    }

    private static DelayedSpinnerProviderActivity Create(RecordingWriter output) =>
        new(output, () => true, TestDelay, TestInterval);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset timeout = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= timeout)
                Assert.Fail("Timed out waiting for the spinner frame.");
            await Task.Delay(5);
        }
    }

    private sealed class RecordingWriter : TextWriter
    {
        private readonly object _sync = new();
        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Snapshot
        {
            get
            {
                lock (_sync) return _buffer.ToString();
            }
        }

        public override void Write(char value)
        {
            lock (_sync) _buffer.Append(value);
        }

        public override void Write(string? value)
        {
            lock (_sync) _buffer.Append(value);
        }
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => throw new IOException("console unavailable");

        public override void Write(string? value) => throw new IOException("console unavailable");
    }
}
