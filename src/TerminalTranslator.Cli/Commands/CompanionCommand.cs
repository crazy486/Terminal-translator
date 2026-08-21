using System.CommandLine;
using System.Text.Json;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Commands;

public static class CompanionCommand
{
    public static Command Create(TextWriter? output = null)
    {
        TextWriter standardOutput = output ?? Console.Out;
        Option<string> session = new("--session") { Required = true };
        Command command = new("__companion", "Internal companion process.") { Hidden = true };
        command.Options.Add(session);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string requestedSession = parseResult.GetRequiredValue(session);
            string? inheritedSession = Environment.GetEnvironmentVariable("TT_SESSION_ID");
            string? nonce = Environment.GetEnvironmentVariable("TT_SESSION_NONCE");
            if (!string.Equals(requestedSession, inheritedSession, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(nonce))
            {
                return 5;
            }

            string pipeName = $"tt-{requestedSession}-{nonce[..Math.Min(12, nonce.Length)]}-events";
            await using EventPipeClient client = await EventPipeClient.ConnectAsync(
                pipeName,
                requestedSession,
                nonce,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            CompanionRenderer renderer = new(standardOutput);
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
        });
        return command;
    }
}
