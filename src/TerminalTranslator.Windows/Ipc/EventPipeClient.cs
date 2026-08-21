using System.IO.Pipes;
using System.Text.Json;

namespace TerminalTranslator.Windows.Ipc;

public sealed class EventPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;

    private EventPipeClient(NamedPipeClientStream pipe) => _pipe = pipe;

    public static async Task<EventPipeClient> ConnectAsync(
        string pipeName,
        string sessionId,
        string nonce,
        int maximumAttempts = 20,
        TimeSpan? retryDelay = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan delay = retryDelay ?? TimeSpan.FromMilliseconds(100);
        Exception? lastError = null;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync((int)Math.Max(1, delay.TotalMilliseconds), cancellationToken).ConfigureAwait(false);
                HelloMessage hello = new("hello", SessionProtocol.Version, sessionId, nonce, "companion");
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(hello, SessionJsonContext.Default.HelloMessage) + "\n");
                await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

                string line = await EventPipeServer.ReadBoundedLineAsync(pipe, cancellationToken).ConfigureAwait(false);
                HelloAckMessage? acknowledgement = JsonSerializer.Deserialize(line, SessionJsonContext.Default.HelloAckMessage);
                if (acknowledgement is null ||
                    acknowledgement.Type != "hello-ack" ||
                    acknowledgement.Protocol != SessionProtocol.Version ||
                    acknowledgement.SessionId != sessionId)
                {
                    throw new InvalidDataException("Invalid event-pipe acknowledgement.");
                }

                return new EventPipeClient(pipe);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                lastError = exception;
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (attempt < maximumAttempts)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new IOException("Unable to connect to the event pipe.", lastError);
    }

    public async Task<JsonDocument?> ReadEventAsync(CancellationToken cancellationToken)
    {
        try
        {
            string line = await EventPipeServer.ReadBoundedLineAsync(_pipe, cancellationToken).ConfigureAwait(false);
            return JsonDocument.Parse(line);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}
