using System.CommandLine;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Commands;

public static class OffCommand
{
    public static Command Create(
        TextWriter? output = null,
        TextWriter? error = null,
        Func<string, string, IControlPipeClient>? clientFactory = null)
    {
        TextWriter standardOutput = output ?? Console.Out;
        TextWriter standardError = error ?? Console.Error;
        Command command = new("off", "Disable translation for the current session.");
        command.SetAction(async (_, cancellationToken) =>
        {
            if (!TryGetSession(out string sessionId, out string nonce))
            {
                await standardError.WriteLineAsync("No live translation session.").ConfigureAwait(false);
                return 5;
            }

            IControlPipeClient client = clientFactory?.Invoke(sessionId, nonce) ??
                new ControlPipeClient(SessionPipeNames.Control(sessionId, nonce), sessionId, nonce);
            try
            {
                ControlResultMessage result = await client.DisableAsync(cancellationToken).ConfigureAwait(false);
                if (!result.Ok || result.State != "disabled")
                {
                    await standardError.WriteLineAsync("Translation session rejected the disable request.").ConfigureAwait(false);
                    return 5;
                }

                await standardOutput.WriteLineAsync("Translation disabled.").ConfigureAwait(false);
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

    internal static bool TryGetSession(out string sessionId, out string nonce)
    {
        sessionId = Environment.GetEnvironmentVariable("TT_SESSION_ID") ?? string.Empty;
        nonce = Environment.GetEnvironmentVariable("TT_SESSION_NONCE") ?? string.Empty;
        return !string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(nonce);
    }
}
