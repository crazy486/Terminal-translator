using System.Text.Json;

namespace TerminalTranslator.Windows.Capture;

public sealed class FinalizationResidueManager
{
    private const string StateFileName = ".finalization-residue.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<string, bool> _delete;

    public FinalizationResidueManager(string sessionDirectory, Func<string, bool>? delete = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        SessionDirectory = Path.GetFullPath(sessionDirectory);
        Directory.CreateDirectory(SessionDirectory);
        StatePath = Path.Combine(SessionDirectory, StateFileName);
        _delete = delete ?? DeleteArtifact;
    }

    public string SessionDirectory { get; }

    public string StatePath { get; }

    public bool CanStartCapture => !File.Exists(StatePath);

    public async Task<bool> TryRegisterAsync(
        IReadOnlyList<string> artifactPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifactPaths);
        if (File.Exists(StatePath))
        {
            return false;
        }

        string[] paths = artifactPaths
            .Select(ValidateArtifactPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            throw new ArgumentException("A residue set requires at least one artifact.", nameof(artifactPaths));
        }

        string temporaryPath = Path.Combine(SessionDirectory, $".{StateFileName}.{Guid.NewGuid():N}.tmp");
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
                await JsonSerializer.SerializeAsync(stream, new ResidueState(1, paths), JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, StatePath, overwrite: false);
            return true;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<bool> TryRecoverAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(StatePath))
        {
            return true;
        }

        ResidueState? state;
        try
        {
            await using FileStream stream = new(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            state = await JsonSerializer.DeserializeAsync<ResidueState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        if (state is null || state.Version != 1 || state.Paths is null || state.Paths.Count == 0)
        {
            return false;
        }

        bool allDeleted = true;
        foreach (string path in state.Paths)
        {
            string validated;
            try
            {
                validated = ValidateArtifactPath(path);
            }
            catch
            {
                return false;
            }

            try
            {
                if ((File.Exists(validated) || Directory.Exists(validated)) && !_delete(validated))
                {
                    allDeleted = false;
                }
            }
            catch
            {
                allDeleted = false;
            }
        }

        if (!allDeleted)
        {
            return false;
        }

        try
        {
            File.Delete(StatePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string ValidateArtifactPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        string prefix = SessionDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Residue artifact is outside the bound session directory.", nameof(path));
        }

        return full;
    }

    private static bool DeleteArtifact(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: false);
        }

        return !File.Exists(path) && !Directory.Exists(path);
    }

    private sealed record ResidueState(int Version, IReadOnlyList<string> Paths);
}
