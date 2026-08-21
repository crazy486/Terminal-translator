using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Windows.Ipc;

public sealed class EventPipeServer : ITranslationEventSink, IAsyncDisposable
{
    private static readonly TimeSpan WriteDeadline = TimeSpan.FromMilliseconds(250);
    private readonly string _pipeName;
    private readonly string _sessionId;
    private readonly string _nonce;
    private readonly string _initialState;
    private readonly object _connectionGate = new();
    private readonly SemaphoreSlim _acceptLock = new(1, 1);
    private readonly Channel<OutboundMessage> _outbound;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writer;
    private NamedPipeServerStream? _pipe;
    private TaskCompletionSource? _disconnected;
    private long _connectionId;
    private bool _ready;
    private bool _disposed;

    public EventPipeServer(
        string pipeName,
        string sessionId,
        string nonce,
        string initialState = "disabled")
    {
        _pipeName = pipeName;
        _sessionId = sessionId;
        _nonce = nonce;
        _initialState = initialState;
        _outbound = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
        _writer = RunWriterAsync(_shutdown.Token);
    }

    public bool IsConnected
    {
        get
        {
            lock (_connectionGate)
            {
                return _ready && _pipe?.IsConnected == true;
            }
        }
    }

    public async Task WaitForClientAsync(CancellationToken cancellationToken)
    {
        await _acceptLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AcceptClientAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _acceptLock.Release();
        }
    }

    private async Task AcceptClientAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream? previous;
        lock (_connectionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _pipe;
            _pipe = null;
            _ready = false;
            _disconnected?.TrySetResult();
            _disconnected = null;
        }

        previous?.Dispose();
        NamedPipeServerStream candidate = CurrentUserPipeFactory.CreateServer(
            _pipeName,
            PipeDirection.InOut,
            maxInstances: 1);
        long connectionId;
        lock (_connectionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pipe = candidate;
            _disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connectionId = ++_connectionId;
        }

        try
        {
            await candidate.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            string line = await ReadBoundedLineAsync(candidate, cancellationToken).ConfigureAwait(false);
            HelloMessage? hello = JsonSerializer.Deserialize(line, SessionJsonContext.Default.HelloMessage);
            if (!SessionHandshakeValidator.IsValid(hello, _sessionId, _nonce, ["companion"]))
            {
                throw new InvalidDataException("Invalid event-pipe handshake.");
            }

            HelloAckMessage acknowledgement = new(
                "hello-ack",
                SessionProtocol.Version,
                _sessionId,
                _initialState);
            await WriteDirectAsync(
                candidate,
                JsonSerializer.Serialize(acknowledgement, SessionJsonContext.Default.HelloAckMessage),
                cancellationToken).ConfigureAwait(false);
            lock (_connectionGate)
            {
                if (_connectionId == connectionId && ReferenceEquals(_pipe, candidate))
                {
                    _ready = true;
                }
            }

            _ = MonitorDisconnectAsync(candidate, connectionId);
        }
        catch
        {
            RemoveConnection(candidate, connectionId);
            throw;
        }
    }

    public Task WaitForDisconnectAsync(CancellationToken cancellationToken)
    {
        Task disconnected;
        lock (_connectionGate)
        {
            disconnected = _disconnected?.Task ?? Task.CompletedTask;
        }

        return disconnected.WaitAsync(cancellationToken);
    }

    public ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            TranslationEventMessage message = new(
                "translation",
                SessionProtocol.Version,
                item.SessionId.ToString("N"),
                item.Generation,
                item.SegmentSequence,
                item.SourceText,
                item.TranslatedText,
                item.Layout);
            TryQueue(JsonSerializer.Serialize(message, SessionJsonContext.Default.TranslationEventMessage));
        }

        return ValueTask.CompletedTask;
    }

    public Task PublishAsync(StatusEventMessage message, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            TryQueue(JsonSerializer.Serialize(message, SessionJsonContext.Default.StatusEventMessage));
        }

        return Task.CompletedTask;
    }

    public Task PublishAsync(StateEventMessage message, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            TryQueue(JsonSerializer.Serialize(message, SessionJsonContext.Default.StateEventMessage));
        }

        return Task.CompletedTask;
    }

    private void TryQueue(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > SessionProtocol.MaximumMessageBytes)
        {
            return;
        }

        NamedPipeServerStream pipe;
        long connectionId;
        lock (_connectionGate)
        {
            if (!_ready || _pipe?.IsConnected != true || _disposed)
            {
                return;
            }

            pipe = _pipe;
            connectionId = _connectionId;
        }

        _outbound.Writer.TryWrite(new OutboundMessage(pipe, connectionId, json));
    }

    private async Task RunWriterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (OutboundMessage message in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!IsCurrent(message))
                {
                    continue;
                }

                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(WriteDeadline);
                try
                {
                    await WriteDirectAsync(message.Pipe, message.Json, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    RemoveConnection(message.Pipe, message.ConnectionId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool IsCurrent(OutboundMessage message)
    {
        lock (_connectionGate)
        {
            return _ready &&
                _connectionId == message.ConnectionId &&
                ReferenceEquals(_pipe, message.Pipe);
        }
    }

    private void RemoveConnection(NamedPipeServerStream pipe, long connectionId)
    {
        bool dispose;
        TaskCompletionSource? disconnected = null;
        lock (_connectionGate)
        {
            dispose = _connectionId == connectionId && ReferenceEquals(_pipe, pipe);
            if (dispose)
            {
                _ready = false;
                _pipe = null;
                disconnected = _disconnected;
                _disconnected = null;
            }
        }

        if (dispose)
        {
            pipe.Dispose();
            disconnected?.TrySetResult();
        }
    }

    private async Task MonitorDisconnectAsync(NamedPipeServerStream pipe, long connectionId)
    {
        byte[] probe = new byte[1];
        try
        {
            while (await pipe.ReadAsync(probe, _shutdown.Token).ConfigureAwait(false) != 0)
            {
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            RemoveConnection(pipe, connectionId);
        }
    }

    private static async Task WriteDirectAsync(
        Stream pipe,
        string json,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ReadBoundedLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        List<byte> bytes = [];
        byte[] single = new byte[1];
        while (bytes.Count <= SessionProtocol.MaximumMessageBytes)
        {
            int read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            if (single[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(bytes.ToArray());
            }

            bytes.Add(single[0]);
        }

        throw new InvalidDataException("IPC message exceeds the maximum size.");
    }

    public async ValueTask DisposeAsync()
    {
        NamedPipeServerStream? pipe;
        lock (_connectionGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ready = false;
            pipe = _pipe;
            _pipe = null;
            _disconnected?.TrySetResult();
            _disconnected = null;
        }

        _outbound.Writer.TryComplete();
        _shutdown.Cancel();
        pipe?.Dispose();
        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
        _acceptLock.Dispose();
    }

    private sealed record OutboundMessage(
        NamedPipeServerStream Pipe,
        long ConnectionId,
        string Json);
}
