using System.CommandLine;
using TerminalTranslator.Cli.Configuration;

namespace TerminalTranslator.Cli.Commands;

public static class ConfigureCommand
{
    public static Command Create(
        ProviderSettingsStore? store = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        ProviderSettingsStore settingsStore = store ?? new ProviderSettingsStore();
        TextWriter standardOutput = output ?? Console.Out;
        TextWriter standardError = error ?? Console.Error;

        Option<string> endpoint = new("--endpoint") { Required = true };
        Option<string> model = new("--model") { Required = true };
        Option<string> apiKeyEnvironmentVariable = new("--api-key-env") { Required = true };
        Option<int?> timeout = new("--timeout-ms");
        Option<bool> allowLoopbackHttp = new("--allow-loopback-http");

        Command command = new("configure", "Configure a translation provider.");
        command.Options.Add(endpoint);
        command.Options.Add(model);
        command.Options.Add(apiKeyEnvironmentVariable);
        command.Options.Add(timeout);
        command.Options.Add(allowLoopbackHttp);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                string endpointValue = parseResult.GetRequiredValue(endpoint);
                if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out Uri? endpointUri))
                {
                    throw new ArgumentException("Endpoint must be an absolute URI.");
                }

                ProviderSettings settings = ProviderSettings.Create(
                    endpointUri,
                    parseResult.GetRequiredValue(model),
                    parseResult.GetRequiredValue(apiKeyEnvironmentVariable),
                    TimeSpan.FromMilliseconds(parseResult.GetValue(timeout) ?? 1500),
                    parseResult.GetValue(allowLoopbackHttp));
                await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
                await standardOutput.WriteLineAsync(
                    $"Configured provider host={settings.Endpoint.Host} model={settings.Model} key-env={settings.ApiKeyEnvironmentVariable} settings={settingsStore.SettingsPath}").ConfigureAwait(false);
                return 0;
            }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException)
            {
                await standardError.WriteLineAsync(exception.Message).ConfigureAwait(false);
                return 4;
            }
            catch (IOException)
            {
                await standardError.WriteLineAsync("Unable to write provider settings.").ConfigureAwait(false);
                return 7;
            }
        });

        return command;
    }
}
