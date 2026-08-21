using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TerminalTranslator.Windows.Ipc;

public sealed class MinimalControlPipeServer(
    string pipeName,
    string sessionId,
    string providerFingerprint,
    Func<CancellationToken, Task> enable) : IAsyncDisposable
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
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                if (pipe.IsConnected)
                {
                    await WriteResultAsync(pipe, new ControlResultMessage(
                        "control-result", SessionProtocol.Version, "enable", false, "disabled"),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _activePipe = null;
            }
        }
    }

    private async Task HandleClientAsync(Stream pipe, CancellationToken cancellationToken)
    {
        string line = await EventPipeServer.ReadBoundedLineAsync(pipe, cancellationToken).ConfigureAwait(false);
        ControlRequestMessage? request = JsonSerializer.Deserialize(line, SessionJsonContext.Default.ControlRequestMessage);
        bool accepted = request is
        {
            Type: "enable",
            Protocol: SessionProtocol.Version,
            Consent: true,
        } &&
            request.SessionId == sessionId &&
            string.Equals(request.ProviderFingerprint, providerFingerprint, StringComparison.Ordinal);

        if (accepted)
        {
            await enable(cancellationToken).ConfigureAwait(false);
        }

        await WriteResultAsync(pipe, new ControlResultMessage(
            "control-result",
            SessionProtocol.Version,
            "enable",
            accepted,
            accepted ? "enabled" : "disabled",
            accepted ? 1 : null), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResultAsync(
        Stream pipe,
        ControlResultMessage response,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(response, SessionJsonContext.Default.ControlResultMessage) + "\n");
        await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _activePipe?.Dispose();
        return ValueTask.CompletedTask;
    }
}
