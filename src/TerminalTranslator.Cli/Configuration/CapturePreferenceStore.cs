using System.Text.Json;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Cli.Configuration;

public sealed class CapturePreferenceStore
{
    private const string FileName = "capture-preference.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public CapturePreferenceStore(string? settingsDirectory = null)
    {
        SettingsDirectory = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerminalTranslator");
        PreferencePath = Path.Combine(SettingsDirectory, FileName);
    }

    public string SettingsDirectory { get; }

    public string PreferencePath { get; }

    public async Task<CapturePreference> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PreferencePath))
        {
            return CapturePreference.Disabled;
        }

        await using FileStream stream = new(PreferencePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        PersistedPreference? persisted;
        try
        {
            persisted = await JsonSerializer.DeserializeAsync<PersistedPreference>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Capture preference is invalid.", exception);
        }

        return persisted?.State?.ToLowerInvariant() switch
        {
            "enabled" => CapturePreference.Enabled,
            "disabled" => CapturePreference.Disabled,
            _ => throw new InvalidDataException("Capture preference is invalid."),
        };
    }

    public async Task SaveAsync(CapturePreference preference, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(SettingsDirectory);
        string temporaryPath = Path.Combine(SettingsDirectory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new PersistedPreference(preference == CapturePreference.Enabled ? "enabled" : "disabled"),
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, PreferencePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record PersistedPreference(string State);
}
