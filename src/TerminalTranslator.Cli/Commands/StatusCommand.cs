using System.CommandLine;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Commands;

public static class StatusCommand
{
    public static Command Create(
        TextWriter? output = null,
        TextWriter? error = null,
        Func<string, string, IControlPipeClient>? clientFactory = null)
    {
        TextWriter standardOutput = output ?? Console.Out;
        TextWriter standardError = error ?? Console.Error;
        Command command = new("status", "Show content-free session status.");
        command.SetAction(async (_, cancellationToken) =>
        {
            if (!OffCommand.TryGetSession(out string sessionId, out string nonce))
            {
                await standardError.WriteLineAsync("No live translation session.").ConfigureAwait(false);
                return 5;
            }

            IControlPipeClient client = clientFactory?.Invoke(sessionId, nonce) ??
                new ControlPipeClient(SessionPipeNames.Control(sessionId, nonce), sessionId, nonce);
            try
            {
                StatusResultMessage status = await client.StatusAsync(cancellationToken).ConfigureAwait(false);
                await standardOutput.WriteLineAsync("session: active").ConfigureAwait(false);
                await standardOutput.WriteLineAsync($"translation: {status.State}").ConfigureAwait(false);
                await standardOutput.WriteLineAsync($"provider: {status.ProviderHost}, {status.Model}").ConfigureAwait(false);
                await standardOutput.WriteLineAsync(
                    $"queue: high={status.HighQueued} normal={status.NormalQueued}").ConfigureAwait(false);
                await standardOutput.WriteLineAsync($"privacy-skipped: {status.PrivacySkipped}").ConfigureAwait(false);
                await standardOutput.WriteLineAsync($"overload-dropped: {status.OverloadDropped}").ConfigureAwait(false);
                return 0;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException)
            {
                await standardError.WriteLineAsync("No live translation session.").ConfigureAwait(false);
                return 5;
            }
        });
        return command;
    }
}
