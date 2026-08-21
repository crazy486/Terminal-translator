using System.CommandLine;
using System.Text.Json;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Commands;

public static class CompanionCommand
{
    public static Command Create(TextWriter? output = null, TextWriter? error = null)
    {
        TextWriter standardOutput = output ?? Console.Out;
        TextWriter standardError = error ?? Console.Error;
        Option<string> session = new("--session") { Required = true };
        Command command = new("__companion", "Internal companion process.") { Hidden = true };
        command.Options.Add(session);
        command.SetAction((parseResult, cancellationToken) => RunAsync(
            parseResult.GetRequiredValue(session),
            Environment.GetEnvironmentVariable("TT_SESSION_ID"),
            Environment.GetEnvironmentVariable("TT_SESSION_NONCE"),
            standardOutput,
            standardError,
            connectionTimeout: null,
            cancellationToken));
        return command;
    }

    public static async Task<int> RunAsync(
        string requestedSession,
        string? inheritedSession,
        string? nonce,
        TextWriter output,
        TextWriter error,
        TimeSpan? connectionTimeout,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedSession, inheritedSession, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(nonce))
        {
            await error.WriteLineAsync("Companion session authentication failed.").ConfigureAwait(false);
            return 5;
        }

        string pipeName = SessionPipeNames.Event(requestedSession, nonce);
        EventPipeClient client;
        try
        {
            client = await EventPipeClient.ConnectAsync(
                pipeName,
                requestedSession,
                nonce,
                connectionTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await error.WriteLineAsync(
                "Companion could not connect to the session host. Close this pane and start a new translation session.").ConfigureAwait(false);
            return 6;
        }

        await using (client)
        {
            await output.WriteLineAsync($"translation: {client.InitialState}").ConfigureAwait(false);
            CompanionRenderer renderer = new(output);
            while (await client.ReadEventAsync(cancellationToken).ConfigureAwait(false) is JsonDocument document)
            {
                using (document)
                {
                    if (!document.RootElement.TryGetProperty("type", out JsonElement typeElement))
                    {
                        continue;
                    }

                    string? type = typeElement.GetString();
                    string json = document.RootElement.GetRawText();
                    if (type == "translation")
                    {
                        TranslationEventMessage? translation = JsonSerializer.Deserialize(
                            json,
                            SessionJsonContext.Default.TranslationEventMessage);
                        if (translation is not null)
                        {
                            renderer.Render(translation);
                        }
                    }
                    else if (type == "state")
                    {
                        StateEventMessage? state = JsonSerializer.Deserialize(
                            json,
                            SessionJsonContext.Default.StateEventMessage);
                        if (state is not null)
                        {
                            renderer.Render(state);
                        }
                    }
                    else
                    {
                        StatusEventMessage? status = JsonSerializer.Deserialize(
                            json,
                            SessionJsonContext.Default.StatusEventMessage);
                        if (status is not null)
                        {
                            renderer.Render(status);
                        }
                    }
                }
            }

            return 0;
        }
    }
}
