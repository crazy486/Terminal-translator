using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Windows.Console;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class FailureIsolationTests
{
    private static readonly byte[] ShellInput = Encoding.UTF8.GetBytes("Write-Output 'still responsive'\r\n");

    [TestMethod]
    public async Task SlowProvider_DoesNotBackpressureRawOutputOrShellInput()
    {
        ManualClock clock = new();
        DeferredProvider provider = new();
        RecordingEventSink eventSink = new();
        TranslationCoordinator coordinator = new(provider, eventSink, clock, TimeSpan.FromSeconds(2));
        TranslationStartingAnalysisSink analysis = new(coordinator, clock);
        byte[] rawOutput = Encoding.UTF8.GetBytes("first raw line\r\nsecond raw line\r\n");

        ShellRelayResult shell = await RelayShellAsync(rawOutput, analysis);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(rawOutput, shell.ProgramOutput);
        CollectionAssert.AreEqual(ShellInput, shell.ChildInput);
        Assert.IsFalse(analysis.TranslationTask!.IsCompleted, "The fake provider should still be blocked.");
        Assert.IsTrue(shell.OutputCompleted, "Raw output must complete without waiting for translation.");

        provider.Complete(new TranslationResult("慢速翻译完成。"));
        Assert.IsNotNull(await analysis.TranslationTask.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task CompanionDisconnect_DoesNotBlockProgramOutputInputOrSession()
    {
        PipeIdentity identity = PipeIdentity.Create();
        await using EventPipeServer server = new(identity.PipeName, identity.SessionId, identity.Nonce);
        Task accept = server.WaitForClientAsync(CancellationToken.None);
        EventPipeClient client = await EventPipeClient.ConnectAsync(
            identity.PipeName, identity.SessionId, identity.Nonce, TimeSpan.FromSeconds(1));
        await accept.WaitAsync(TimeSpan.FromSeconds(1));
        await client.DisposeAsync();

        await server.PublishAsync(CreateTranslation(identity.SessionGuid, 1), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        ShellRelayResult shell = await RelayShellAsync(Encoding.UTF8.GetBytes("output after disconnect\r\n"));

        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("output after disconnect\r\n"), shell.ProgramOutput);
        CollectionAssert.AreEqual(ShellInput, shell.ChildInput);
        Assert.IsTrue(shell.OutputCompleted);
    }

    [TestMethod]
    public async Task CompanionReconnect_AcceptsFutureEventsWithoutReplayingLostHistoryOrChangingShell()
    {
        PipeIdentity identity = PipeIdentity.Create();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        await using EventPipeServer server = new(identity.PipeName, identity.SessionId, identity.Nonce);

        Task firstAccept = server.WaitForClientAsync(timeout.Token);
        EventPipeClient firstClient = await EventPipeClient.ConnectAsync(
            identity.PipeName, identity.SessionId, identity.Nonce, TimeSpan.FromSeconds(1), timeout.Token);
        await firstAccept;
        await server.PublishAsync(CreateTranslation(identity.SessionGuid, 1), timeout.Token);
        using (JsonDocument firstEvent = (await firstClient.ReadEventAsync(timeout.Token))!)
        {
            Assert.AreEqual(1UL, firstEvent.RootElement.GetProperty("sequence").GetUInt64());
        }

        await firstClient.DisposeAsync();
        await server.PublishAsync(CreateTranslation(identity.SessionGuid, 2), timeout.Token);

        Task secondAccept = server.WaitForClientAsync(timeout.Token);
        EventPipeClient? secondClient = null;
        try
        {
            secondClient = await EventPipeClient.ConnectAsync(
                identity.PipeName, identity.SessionId, identity.Nonce, TimeSpan.FromSeconds(1), timeout.Token);
            await secondAccept;
            await server.PublishAsync(CreateTranslation(identity.SessionGuid, 3), timeout.Token);
            using JsonDocument nextEvent = (await secondClient.ReadEventAsync(timeout.Token))!;
            Assert.AreEqual(3UL, nextEvent.RootElement.GetProperty("sequence").GetUInt64());

            ShellRelayResult shell = await RelayShellAsync(Encoding.UTF8.GetBytes("output after reconnect\r\n"));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("output after reconnect\r\n"), shell.ProgramOutput);
            CollectionAssert.AreEqual(ShellInput, shell.ChildInput);
        }
        finally
        {
            if (secondClient is not null)
            {
                await secondClient.DisposeAsync();
            }

            try
            {
                await secondAccept;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
            {
            }
        }
    }

    [TestMethod]
    public async Task ProviderIgnoringCancellation_DisableDetachesOldWorkAndSuppressesLateResult()
    {
        ManualClock clock = new();
        using TranslationSession session = CreateEnabledSession(clock);
        DeferredProvider provider = new();
        RecordingEventSink eventSink = new();
        TranslationCoordinator coordinator = new(
            provider,
            eventSink,
            clock,
            TimeSpan.FromSeconds(2),
            session: session,
            providerFingerprint: "provider-a");
        Task<TranslationItem?> oldWork = coordinator.TranslateAsync(
            CreateSegment(session, 1, "The old translation must be discarded."),
            CancellationToken.None);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        ShellRelayResult shell = await RelayShellAsync(Encoding.UTF8.GetBytes("shell remains responsive\r\n"));
        Assert.IsTrue(session.Disable());
        await Task.Yield();
        bool detachedAfterDisable = oldWork.IsCompleted;
        Assert.IsTrue(session.Enable("provider-a", consent: true));

        provider.Complete(new TranslationResult("不得显示的旧翻译。"));
        TranslationItem? late = await oldWork.WaitAsync(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("shell remains responsive\r\n"), shell.ProgramOutput);
        CollectionAssert.AreEqual(ShellInput, shell.ChildInput);
        Assert.IsNull(late);
        Assert.AreEqual(0, eventSink.Items.Count);
        Assert.IsTrue(
            detachedAfterDisable,
            "Ignoring provider cancellation must not keep old-generation worker completion pending after disable.");
    }

    [TestMethod]
    public async Task ActiveOutputShutdown_DrainsRawBytesWithoutWaitingForIgnoredTranslation()
    {
        ManualClock clock = new();
        using TranslationSession session = CreateEnabledSession(clock);
        DeferredProvider provider = new();
        RecordingEventSink eventSink = new();
        TranslationCoordinator coordinator = new(
            provider,
            eventSink,
            clock,
            TimeSpan.FromSeconds(2),
            session: session,
            providerFingerprint: "provider-a");
        Task<TranslationItem?> ignoredTranslation = coordinator.TranslateAsync(
            CreateSegment(session, 1, "Translation is still active during shutdown."),
            CancellationToken.None);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await using ControlledChunkStream childOutput = new();
        await using SignalingWriteStream programOutput = new();
        Task rawDrain = new ConsoleOutputRelay(childOutput, programOutput).CopyAsync(CancellationToken.None);
        childOutput.Enqueue(Encoding.UTF8.GetBytes("frame before shutdown\r\n"));
        await programOutput.WaitForWriteAsync(TimeSpan.FromSeconds(1));

        SessionTeardown teardown = new(session, clock);
        Task shutdown = teardown.ExecuteAsync(
            "shell-exit",
            23,
            abnormal: false,
            _ => rawDrain,
            null,
            CancellationToken.None);
        childOutput.Enqueue(Encoding.UTF8.GetBytes("final frame during shutdown\r\n"));
        childOutput.Complete();

        await shutdown.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(SessionState.Ended, session.State);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("frame before shutdown\r\nfinal frame during shutdown\r\n"),
            programOutput.ToArray());
        Assert.IsFalse(ignoredTranslation.IsCompleted, "The provider intentionally still ignores cancellation.");

        await AssertInputUnchangedAsync();
        provider.Complete(new TranslationResult("过期翻译。"));
        Assert.IsNull(await ignoredTranslation.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(0, eventSink.Items.Count);
    }

    private static async Task<ShellRelayResult> RelayShellAsync(
        byte[] rawOutput,
        INonBlockingAnalysisSink? analysisSink = null)
    {
        await using MemoryStream programOutput = new();
        await new ConsoleOutputRelay(new MemoryStream(rawOutput), programOutput, analysisSink)
            .CopyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        byte[] childInput = await RelayInputAsync();
        return new ShellRelayResult(programOutput.ToArray(), childInput, OutputCompleted: true);
    }

    private static async Task AssertInputUnchangedAsync()
    {
        CollectionAssert.AreEqual(ShellInput, await RelayInputAsync());
    }

    private static async Task<byte[]> RelayInputAsync()
    {
        await using MemoryStream childInput = new();
        await new ConsoleInputRelay(new MemoryStream(ShellInput), childInput)
            .CopyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        return childInput.ToArray();
    }

    private static TranslationSession CreateEnabledSession(IClock clock)
    {
        TranslationSession session = new(Guid.NewGuid(), clock);
        session.Start();
        session.Enable("provider-a", consent: true);
        return session;
    }

    private static OutputSegment CreateSegment(
        TranslationSession session,
        ulong sequence,
        string text) => new(
            session.SessionId,
            session.Generation,
            sequence,
            text,
            new LayoutHints(1, [0]),
            SourceBoundary.Line,
            TranslationPriority.Normal,
            null,
            TimeSpan.Zero);

    private static TranslationItem CreateTranslation(Guid sessionId, ulong sequence) => new(
        sessionId,
        1,
        sequence,
        $"Source {sequence}",
        $"Translation {sequence}",
        new LayoutHints(1, [0]),
        null,
        TimeSpan.Zero);

    private sealed record ShellRelayResult(byte[] ProgramOutput, byte[] ChildInput, bool OutputCompleted);

    private sealed record PipeIdentity(string PipeName, string SessionId, string Nonce, Guid SessionGuid)
    {
        public static PipeIdentity Create()
        {
            Guid sessionGuid = Guid.NewGuid();
            string sessionId = sessionGuid.ToString("N");
            string nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray()) +
                Convert.ToHexString(Guid.NewGuid().ToByteArray());
            return new PipeIdentity(SessionPipeNames.Event(sessionId, nonce), sessionId, nonce, sessionGuid);
        }
    }

    private sealed class ManualClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);

        public TimeSpan MonotonicNow { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MonotonicNow += delay;
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class DeferredProvider : ITranslationProvider
    {
        private readonly TaskCompletionSource<TranslationResult> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return _result.Task;
        }

        public void Complete(TranslationResult result) => _result.TrySetResult(result);
    }

    private sealed class RecordingEventSink : ITranslationEventSink
    {
        public List<TranslationItem> Items { get; } = [];

        public ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken)
        {
            Items.Add(item);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TranslationStartingAnalysisSink(
        TranslationCoordinator coordinator,
        IClock clock) : INonBlockingAnalysisSink
    {
        public bool IsEnabled => true;

        public Task<TranslationItem?>? TranslationTask { get; private set; }

        public bool TryOffer(ReadOnlyMemory<byte> bytes)
        {
            OutputSegment segment = new(
                Guid.NewGuid(),
                1,
                1,
                Encoding.UTF8.GetString(bytes.Span),
                new LayoutHints(1, [0]),
                SourceBoundary.Line,
                TranslationPriority.Normal,
                null,
                clock.MonotonicNow);
            TranslationTask = coordinator.TranslateAsync(segment, CancellationToken.None);
            return true;
        }
    }

    private sealed class ControlledChunkStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Enqueue(byte[] bytes) => _chunks.Writer.TryWrite(bytes);

        public void Complete() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset == _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }

                if (_chunks.Reader.TryRead(out byte[]? next))
                {
                    _current = next;
                    _offset = 0;
                }
            }

            int count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SignalingWriteStream : Stream
    {
        private readonly MemoryStream _bytes = new();
        private readonly SemaphoreSlim _writes = new(0);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytes.Length;
        public override long Position { get => _bytes.Position; set => throw new NotSupportedException(); }

        public byte[] ToArray() => _bytes.ToArray();

        public async Task WaitForWriteAsync(TimeSpan timeout) =>
            Assert.IsTrue(await _writes.WaitAsync(timeout), "Timed out waiting for raw program output.");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _bytes.Write(buffer.Span);
            _writes.Release();
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _writes.Dispose();
                _bytes.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
