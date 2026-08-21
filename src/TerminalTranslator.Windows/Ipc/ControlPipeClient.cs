using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TerminalTranslator.Windows.Ipc;

public interface IControlPipeClient
{
    Task<ControlResultMessage> EnableAsync(
        string providerFingerprint,
        bool consent,
        CancellationToken cancellationToken);

    Task<ControlResultMessage> DisableAsync(CancellationToken cancellationToken);

    Task<StatusResultMessage> StatusAsync(CancellationToken cancellationToken);
}

public sealed class ControlPipeClient(
    string pipeName,
    string sessionId,
    string nonce,
    TimeSpan? requestTimeout = null) : IControlPipeClient
{
    private readonly TimeSpan _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(2);

    public Task<ControlResultMessage> EnableAsync(
        string providerFingerprint,
        bool consent,
        CancellationToken cancellationToken) =>
        SendControlAsync(
            new ControlRequestMessage(
                "enable", SessionProtocol.Version, sessionId, providerFingerprint, consent),
            "enable",
            cancellationToken);

    public Task<ControlResultMessage> DisableAsync(CancellationToken cancellationToken) =>
        SendControlAsync(
            new ControlRequestMessage("disable", SessionProtocol.Version, sessionId),
            "disable",
            cancellationToken);

    public async Task<StatusResultMessage> StatusAsync(CancellationToken cancellationToken)
    {
        await using NamedPipeClientStream pipe = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await WriteRequestAsync(
            pipe,
            new ControlRequestMessage("status", SessionProtocol.Version, sessionId),
            cancellationToken).ConfigureAwait(false);
        string line = await ReadWithDeadlineAsync(pipe, cancellationToken).ConfigureAwait(false);
        StatusResultMessage? response = JsonSerializer.Deserialize(
            line,
            SessionJsonContext.Default.StatusResultMessage);
        if (response is null || response.Type != "status-result" ||
            response.Protocol != SessionProtocol.Version)
        {
            throw new InvalidDataException("Invalid status response.");
        }

        return response;
    }

    private async Task<ControlResultMessage> SendControlAsync(
        ControlRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        await using NamedPipeClientStream pipe = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await WriteRequestAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        string line = await ReadWithDeadlineAsync(pipe, cancellationToken).ConfigureAwait(false);
        ControlResultMessage? response = JsonSerializer.Deserialize(
            line,
            SessionJsonContext.Default.ControlResultMessage);
        if (response is null || response.Type != "control-result" ||
            response.Protocol != SessionProtocol.Version ||
            !string.Equals(response.Operation, operation, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Invalid control response.");
        }

        return response;
    }

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        NamedPipeClientStream pipe = CurrentUserPipeFactory.CreateClient(pipeName, PipeDirection.InOut);
        using CancellationTokenSource deadline = CreateDeadline(cancellationToken);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            HelloMessage hello = new(
                "hello", SessionProtocol.Version, sessionId, nonce, "control");
            byte[] bytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(hello, SessionJsonContext.Default.HelloMessage) + "\n");
            await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            string line = await EventPipeServer.ReadBoundedLineAsync(pipe, deadline.Token).ConfigureAwait(false);
            HelloAckMessage? acknowledgement = JsonSerializer.Deserialize(
                line,
                SessionJsonContext.Default.HelloAckMessage);
            if (acknowledgement is null || acknowledgement.Type != "hello-ack" ||
                acknowledgement.Protocol != SessionProtocol.Version ||
                !string.Equals(acknowledgement.SessionId, sessionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Invalid control-pipe acknowledgement.");
            }

            return pipe;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException("The session host did not answer the control request.", exception);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task WriteRequestAsync(
        Stream pipe,
        ControlRequestMessage request,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(request, SessionJsonContext.Default.ControlRequestMessage);
        if (Encoding.UTF8.GetByteCount(json) > SessionProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC message exceeds the maximum size.");
        }

        using CancellationTokenSource deadline = CreateDeadline(cancellationToken);
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
        await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
    }

    private async Task<string> ReadWithDeadlineAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CreateDeadline(cancellationToken);
        try
        {
            return await EventPipeServer.ReadBoundedLineAsync(pipe, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The session host did not answer the control request.", exception);
        }
    }

    private CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        return deadline;
    }
}
