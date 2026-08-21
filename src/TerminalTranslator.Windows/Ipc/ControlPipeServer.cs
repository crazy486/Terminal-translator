using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TerminalTranslator.Windows.Ipc;

public interface IControlPipeRequestHandler
{
    string CurrentState { get; }

    Task<ControlResultMessage> EnableAsync(
        string providerFingerprint,
        bool consent,
        CancellationToken cancellationToken);

    Task<ControlResultMessage> DisableAsync(CancellationToken cancellationToken);

    Task<StatusResultMessage> StatusAsync(CancellationToken cancellationToken);
}

public sealed class ControlPipeServer(
    string pipeName,
    string sessionId,
    string nonce,
    IControlPipeRequestHandler handler) : IAsyncDisposable
{
    private NamedPipeServerStream? _activePipe;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using NamedPipeServerStream pipe = CurrentUserPipeFactory.CreateServer(
                pipeName,
                PipeDirection.InOut,
                maxInstances: 1);
            _activePipe = pipe;
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleClientAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException or
                InvalidDataException or JsonException)
            {
                // Reject only this connection. Never echo malformed or unauthorized input.
            }
            finally
            {
                _activePipe = null;
            }
        }
    }

    private async Task HandleClientAsync(Stream pipe, CancellationToken cancellationToken)
    {
        string helloLine = await EventPipeServer.ReadBoundedLineAsync(pipe, cancellationToken).ConfigureAwait(false);
        HelloMessage? hello = JsonSerializer.Deserialize(helloLine, SessionJsonContext.Default.HelloMessage);
        if (!SessionHandshakeValidator.IsValid(hello, sessionId, nonce, ["control"]))
        {
            throw new InvalidDataException("Invalid control-pipe handshake.");
        }

        await WriteAsync(
            pipe,
            JsonSerializer.Serialize(
                new HelloAckMessage("hello-ack", SessionProtocol.Version, sessionId, handler.CurrentState),
                SessionJsonContext.Default.HelloAckMessage),
            cancellationToken).ConfigureAwait(false);

        string requestLine = await EventPipeServer.ReadBoundedLineAsync(pipe, cancellationToken).ConfigureAwait(false);
        ControlRequestMessage? request = JsonSerializer.Deserialize(
            requestLine,
            SessionJsonContext.Default.ControlRequestMessage);
        if (request is null || request.Protocol != SessionProtocol.Version ||
            !string.Equals(request.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Invalid control request.");
        }

        switch (request.Type)
        {
            case "enable" when !string.IsNullOrWhiteSpace(request.ProviderFingerprint) && request.Consent is not null:
                ControlResultMessage enable = await handler.EnableAsync(
                    request.ProviderFingerprint,
                    request.Consent.Value,
                    cancellationToken).ConfigureAwait(false);
                await WriteAsync(
                    pipe,
                    JsonSerializer.Serialize(enable, SessionJsonContext.Default.ControlResultMessage),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "disable":
                ControlResultMessage disable = await handler.DisableAsync(cancellationToken).ConfigureAwait(false);
                await WriteAsync(
                    pipe,
                    JsonSerializer.Serialize(disable, SessionJsonContext.Default.ControlResultMessage),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "status":
                StatusResultMessage status = await handler.StatusAsync(cancellationToken).ConfigureAwait(false);
                await WriteAsync(
                    pipe,
                    JsonSerializer.Serialize(status, SessionJsonContext.Default.StatusResultMessage),
                    cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException("Unknown control operation.");
        }
    }

    private static async Task WriteAsync(
        Stream pipe,
        string json,
        CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(json) > SessionProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC message exceeds the maximum size.");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _activePipe?.Dispose();
        return ValueTask.CompletedTask;
    }
}
