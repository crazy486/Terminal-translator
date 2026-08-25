using System.CommandLine;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.PowerShell;

namespace TerminalTranslator.Cli.Commands;

public static class ConfigureCommand
{
    public static Command Create(
        ProviderSettingsStore? store = null,
        TextWriter? output = null,
        TextWriter? error = null,
        CapturePreferenceStore? captureStore = null,
        Func<CapturePreference, CancellationToken, Task>? captureMutation = null)
    {
        ProviderSettingsStore settingsStore = store ?? new ProviderSettingsStore();
        TextWriter standardOutput = output ?? Console.Out;
        TextWriter standardError = error ?? Console.Error;
        CapturePreferenceStore preferenceStore = captureStore ?? new CapturePreferenceStore();

        Option<string?> endpoint = new("--endpoint");
        Option<string?> model = new("--model");
        Option<string?> apiKeyEnvironmentVariable = new("--api-key-env");
        Option<int?> timeout = new("--timeout-ms");
        Option<bool> allowLoopbackHttp = new("--allow-loopback-http");
        Option<string?> capture = new("--capture");

        Command command = new("configure", "Configure a translation provider.");
        command.Options.Add(endpoint);
        command.Options.Add(model);
        command.Options.Add(apiKeyEnvironmentVariable);
        command.Options.Add(timeout);
        command.Options.Add(allowLoopbackHttp);
        command.Options.Add(capture);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool captureOperation = parseResult.GetValue(capture) is not null;
            try
            {
                string? captureValue = parseResult.GetValue(capture);
                bool providerSupplied = parseResult.Tokens.Any(token => token.Value is
                    "--endpoint" or "--model" or "--api-key-env" or "--timeout-ms" or "--allow-loopback-http");
                if (captureValue is not null)
                {
                    if (providerSupplied)
                    {
                        throw new ArgumentException("--capture cannot be combined with provider settings in one mutation.");
                    }

                    CapturePreference preference = captureValue.ToLowerInvariant() switch
                    {
                        "enabled" => CapturePreference.Enabled,
                        "disabled" => CapturePreference.Disabled,
                        _ => throw new ArgumentException("--capture must be 'enabled' or 'disabled'."),
                    };
                    await standardOutput.WriteLineAsync(
                        "Capture stores bounded local command output for on-demand assistance in the current PowerShell session; it is not provider consent.")
                        .ConfigureAwait(false);
                    await (captureMutation ?? ((value, token) => MutateCaptureAsync(value, preferenceStore, token)))(preference, cancellationToken)
                        .ConfigureAwait(false);
                    await standardOutput.WriteLineAsync($"Capture preference: {captureValue.ToLowerInvariant()}.").ConfigureAwait(false);
                    return 0;
                }

                string? endpointValue = parseResult.GetValue(endpoint);
                string? modelValue = parseResult.GetValue(model);
                string? keyValue = parseResult.GetValue(apiKeyEnvironmentVariable);
                if (!providerSupplied || string.IsNullOrWhiteSpace(endpointValue) ||
                    string.IsNullOrWhiteSpace(modelValue) || string.IsNullOrWhiteSpace(keyValue))
                {
                    throw new ArgumentException("Provider configuration requires --endpoint, --model, and --api-key-env together.");
                }
                if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out Uri? endpointUri))
                {
                    throw new ArgumentException("Endpoint must be an absolute URI.");
                }

                int? configuredTimeout = parseResult.GetValue(timeout);
                bool usesDefaultTimeout = configuredTimeout is null;
                ProviderSettings settings = ProviderSettings.Create(
                    endpointUri,
                    modelValue,
                    keyValue,
                    usesDefaultTimeout
                        ? ProviderSettings.DefaultRequestTimeout
                        : TimeSpan.FromMilliseconds(configuredTimeout!.Value),
                    parseResult.GetValue(allowLoopbackHttp),
                    usesDefaultTimeout);
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                await standardError.WriteLineAsync(captureOperation
                    ? $"Unable to update capture configuration: {exception.Message}"
                    : $"Unable to write provider settings at '{settingsStore.SettingsPath}': {exception.Message}").ConfigureAwait(false);
                return 7;
            }
        });

        return command;
    }

    private static async Task MutateCaptureAsync(
        CapturePreference requested,
        CapturePreferenceStore preferenceStore,
        CancellationToken cancellationToken)
    {
        CapturePreference previous = await preferenceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        string profilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WindowsPowerShell",
            "Microsoft.PowerShell_profile.ps1");
        PowerShellIntegrationInstaller installer = new();
        try
        {
            if (requested == CapturePreference.Enabled)
            {
                await installer.InstallAsync(profilePath, cancellationToken).ConfigureAwait(false);
                await preferenceStore.SaveAsync(requested, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Persist disablement first. Loaded wrappers observe it at their next safe prompt
                // boundary before profile reversal affects only future shells.
                await preferenceStore.SaveAsync(requested, cancellationToken).ConfigureAwait(false);
                await installer.RemoveAsync(profilePath, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                if (previous == CapturePreference.Enabled)
                    await installer.InstallAsync(profilePath, CancellationToken.None).ConfigureAwait(false);
                else
                    await installer.RemoveAsync(profilePath, CancellationToken.None).ConfigureAwait(false);
                await preferenceStore.SaveAsync(previous, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original failure; rollback is best-effort and never exposes capture content.
            }
            throw;
        }
    }
}
