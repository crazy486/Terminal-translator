using System.IO.Pipes;
using System.Text.Json;

namespace TerminalTranslator.Windows.Ipc;

public sealed class EventPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;

    private EventPipeClient(NamedPipeClientStream pipe, string initialState)
    {
        _pipe = pipe;
        InitialState = initialState;
    }

    public string InitialState { get; }

    public static async Task<EventPipeClient> ConnectAsync(
        string pipeName,
        string sessionId,
        string nonce,
        TimeSpan? connectionTimeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan timeout = connectionTimeout ?? TimeSpan.FromSeconds(5);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectionTimeout));
        }

        NamedPipeClientStream pipe = CurrentUserPipeFactory.CreateClient(pipeName, PipeDirection.InOut);
        using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(timeout);
        try
        {
            await pipe.ConnectAsync(startup.Token).ConfigureAwait(false);
            HelloMessage hello = new("hello", SessionProtocol.Version, sessionId, nonce, "companion");
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(hello, SessionJsonContext.Default.HelloMessage) + "\n");
            await pipe.WriteAsync(bytes, startup.Token).ConfigureAwait(false);
            await pipe.FlushAsync(startup.Token).ConfigureAwait(false);

            string line = await EventPipeServer.ReadBoundedLineAsync(pipe, startup.Token).ConfigureAwait(false);
            HelloAckMessage? acknowledgement = JsonSerializer.Deserialize(line, SessionJsonContext.Default.HelloAckMessage);
            if (acknowledgement is null ||
                acknowledgement.Type != "hello-ack" ||
                acknowledgement.Protocol != SessionProtocol.Version ||
                acknowledgement.SessionId != sessionId)
            {
                throw new InvalidDataException("Invalid event-pipe acknowledgement.");
            }

            return new EventPipeClient(pipe, acknowledgement.State);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new IOException(
                "Unable to connect to the event pipe before the host startup deadline.",
                new TimeoutException("The host did not create and acknowledge the event pipe in time.", exception));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new IOException("Unable to connect to the event pipe.", exception);
        }
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
