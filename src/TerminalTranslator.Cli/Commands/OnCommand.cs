using System.CommandLine;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Commands;

public interface IEnableRequestSender
{
    Task<bool> SendAsync(ControlRequestMessage request, CancellationToken cancellationToken);
}

public sealed class MinimalEnableRequestSender(string pipeName) : IEnableRequestSender
{
    public async Task<bool> SendAsync(ControlRequestMessage request, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(1000, deadline.Token).ConfigureAwait(false);
            byte[] bytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(request, SessionJsonContext.Default.ControlRequestMessage) + "\n");
            await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            string responseLine = await EventPipeServer.ReadBoundedLineAsync(pipe, deadline.Token).ConfigureAwait(false);
            ControlResultMessage? response = JsonSerializer.Deserialize(
                responseLine,
                SessionJsonContext.Default.ControlResultMessage);
            return response is { Ok: true, State: "enabled" };
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The session host did not answer the enable request.", exception);
        }
    }
}

public static class OnCommand
{
    public static Command Create(
        ProviderSettingsStore? store = null,
        TextReader? input = null,
        TextWriter? output = null,
        TextWriter? error = null,
        Func<string, string, IEnableRequestSender>? senderFactory = null)
    {
        ProviderSettingsStore settingsStore = store ?? new ProviderSettingsStore();
        TextWriter standardOutput = output ?? Console.Out;
        TextWriter standardError = error ?? Console.Error;
        ConsentPrompt prompt = new(input ?? Console.In, standardOutput);

        Command command = new("on", "Enable translation for the current session.");
        command.SetAction(async (_, cancellationToken) =>
        {
            string? sessionId = Environment.GetEnvironmentVariable("TT_SESSION_ID");
            string? nonce = Environment.GetEnvironmentVariable("TT_SESSION_NONCE");
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(nonce))
            {
                await standardError.WriteLineAsync("No live translation session.").ConfigureAwait(false);
                return 5;
            }

            ProviderSettings? settings;
            try
            {
                settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                await standardError.WriteLineAsync("Unable to read provider settings.").ConfigureAwait(false);
                return 7;
            }

            if (settings is null ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(settings.ApiKeyEnvironmentVariable)))
            {
                await standardError.WriteLineAsync("Provider configuration or credential environment variable is invalid.").ConfigureAwait(false);
                return 4;
            }

            if (!await prompt.ConfirmAsync(settings, cancellationToken).ConfigureAwait(false))
            {
                await standardOutput.WriteLineAsync("Translation remains disabled.").ConfigureAwait(false);
                return 0;
            }

            string pipeName = SessionPipeNames.Control(sessionId, nonce);
            IEnableRequestSender sender = senderFactory?.Invoke(sessionId, nonce) ?? new MinimalEnableRequestSender(pipeName);
            try
            {
                bool enabled = await sender.SendAsync(
                    new ControlRequestMessage("enable", SessionProtocol.Version, sessionId, settings.Fingerprint, true),
                    cancellationToken).ConfigureAwait(false);
                if (!enabled)
                {
                    await standardError.WriteLineAsync("Translation session rejected the enable request.").ConfigureAwait(false);
                    return 5;
                }

                await standardOutput.WriteLineAsync("Translation enabled.").ConfigureAwait(false);
                return 0;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                await standardError.WriteLineAsync("No live translation session.").ConfigureAwait(false);
                return 5;
            }
        });

        return command;
    }
}
