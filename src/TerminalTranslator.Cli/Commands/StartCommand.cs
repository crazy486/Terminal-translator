using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Windows.Terminal;

namespace TerminalTranslator.Cli.Commands;

public static class StartCommand
{
    public static Command Create(
        ProviderSettingsStore? store = null,
        WindowsTerminalLauncher? launcher = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        ProviderSettingsStore settingsStore = store ?? new ProviderSettingsStore();
        WindowsTerminalLauncher terminalLauncher = launcher ?? new WindowsTerminalLauncher();
        TextWriter standardOutput = output ?? Console.Out;
        TextWriter standardError = error ?? Console.Error;
        Option<string?> workingDirectory = new("--working-directory");

        Command command = new("start", "Start a dedicated translated PowerShell session.");
        command.Options.Add(workingDirectory);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
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

            string directory = parseResult.GetValue(workingDirectory) ?? Environment.CurrentDirectory;
            if (!Directory.Exists(directory))
            {
                await standardError.WriteLineAsync("Working directory does not exist.").ConfigureAwait(false);
                return 2;
            }

            string sessionId = Guid.NewGuid().ToString("N");
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            try
            {
                terminalLauncher.Launch(
                    ResolveExecutablePath(),
                    sessionId,
                    nonce,
                    Path.GetFullPath(directory));
                await standardOutput.WriteLineAsync($"Started translation session {sessionId}. Translation is disabled.").ConfigureAwait(false);
                return 0;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                await standardError.WriteLineAsync("Unable to launch Windows Terminal.").ConfigureAwait(false);
                return 6;
            }
        });

        return command;
    }

    private static string ResolveExecutablePath()
    {
        string processPath = Environment.ProcessPath ?? "tt.exe";
        if (!string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        string appHost = Path.Combine(AppContext.BaseDirectory, "tt.exe");
        return File.Exists(appHost) ? appHost : processPath;
    }
}
