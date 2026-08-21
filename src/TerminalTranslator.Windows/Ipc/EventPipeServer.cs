using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Windows.Ipc;

public sealed class EventPipeServer(
    string pipeName,
    string sessionId,
    string nonce,
    string initialState = "disabled") : ITranslationEventSink, IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe = new(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool IsConnected => _pipe.IsConnected;

    public async Task WaitForClientAsync(CancellationToken cancellationToken)
    {
        await _pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        string line = await ReadBoundedLineAsync(_pipe, cancellationToken).ConfigureAwait(false);
        HelloMessage? hello = JsonSerializer.Deserialize(line, SessionJsonContext.Default.HelloMessage);
        if (hello is null ||
            hello.Type != "hello" ||
            hello.Protocol != SessionProtocol.Version ||
            hello.SessionId != sessionId ||
            hello.Nonce != nonce ||
            hello.Role != "companion")
        {
            _pipe.Disconnect();
            throw new InvalidDataException("Invalid event-pipe handshake.");
        }

        HelloAckMessage acknowledgement = new("hello-ack", SessionProtocol.Version, sessionId, initialState);
        await WriteLineAsync(
            JsonSerializer.Serialize(acknowledgement, SessionJsonContext.Default.HelloAckMessage),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken)
    {
        if (!_pipe.IsConnected)
        {
            return;
        }

        TranslationEventMessage message = new(
            "translation",
            SessionProtocol.Version,
            item.SessionId.ToString("N"),
            item.Generation,
            item.SegmentSequence,
            item.SourceText,
            item.TranslatedText,
            item.Layout);
        await WriteLineAsync(
            JsonSerializer.Serialize(message, SessionJsonContext.Default.TranslationEventMessage),
            cancellationToken).ConfigureAwait(false);
    }

    public Task PublishAsync(StatusEventMessage message, CancellationToken cancellationToken) =>
        WriteLineAsync(JsonSerializer.Serialize(message, SessionJsonContext.Default.StatusEventMessage), cancellationToken);

    public Task PublishAsync(StateEventMessage message, CancellationToken cancellationToken) =>
        WriteLineAsync(JsonSerializer.Serialize(message, SessionJsonContext.Default.StateEventMessage), cancellationToken);

    private async Task WriteLineAsync(string json, CancellationToken cancellationToken)
    {
        if (!_pipe.IsConnected || Encoding.UTF8.GetByteCount(json) > SessionProtocol.MaximumMessageBytes)
        {
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        finally
        {
            _writeLock.Release();
        }
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
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
