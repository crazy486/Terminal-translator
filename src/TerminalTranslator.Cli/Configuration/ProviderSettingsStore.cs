using System.Text.Json;

namespace TerminalTranslator.Cli.Configuration;

public sealed class ProviderSettingsStore
{
    private const string FileName = "provider-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public ProviderSettingsStore(string? settingsDirectory = null)
    {
        SettingsDirectory = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerminalTranslator");
        SettingsPath = Path.Combine(SettingsDirectory, FileName);
    }

    public string SettingsDirectory { get; }

    public string SettingsPath { get; }

    public async Task SaveAsync(ProviderSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(SettingsDirectory);
        string temporaryPath = Path.Combine(SettingsDirectory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<ProviderSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            return null;
        }

        await using FileStream stream = new(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        ProviderSettings? settings = await JsonSerializer.DeserializeAsync<ProviderSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            throw new InvalidDataException("Provider settings are empty or invalid.");
        }

        return ProviderSettings.Create(
            settings.Endpoint,
            settings.Model,
            settings.ApiKeyEnvironmentVariable,
            settings.RequestTimeout,
            settings.Endpoint.Scheme == Uri.UriSchemeHttp && settings.Endpoint.IsLoopback);
    }
}
