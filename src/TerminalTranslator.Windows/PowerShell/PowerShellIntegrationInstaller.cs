using System.Reflection;
using System.Text;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.PowerShell;

public sealed class PowerShellIntegrationInstaller
{
    private const string ExecutablePathToken = "__TT_EXECUTABLE_PATH__";
    private readonly PowerShellProfileInstaller _profileInstaller;

    public PowerShellIntegrationInstaller(
        string? integrationDirectory = null,
        PowerShellProfileInstaller? profileInstaller = null,
        string? executablePath = null)
    {
        IntegrationDirectory = integrationDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerminalTranslator",
            "PowerShell");
        LoaderPath = Path.Combine(IntegrationDirectory, "TerminalTranslator.Profile.ps1");
        ExecutablePath = Path.GetFullPath(executablePath ?? Environment.ProcessPath ??
            throw new InvalidOperationException("The Terminal Translator executable path is unavailable."));
        _profileInstaller = profileInstaller ?? new PowerShellProfileInstaller();
    }

    public string IntegrationDirectory { get; }

    public string LoaderPath { get; }

    public string ExecutablePath { get; }

    public async Task InstallAsync(string profilePath, CancellationToken cancellationToken = default)
    {
        CleanupTransactionResidue();
        byte[]? previousLoader = File.Exists(LoaderPath)
            ? await File.ReadAllBytesAsync(LoaderPath, cancellationToken).ConfigureAwait(false)
            : null;
        Directory.CreateDirectory(IntegrationDirectory);
        if (OperatingSystem.IsWindows())
        {
            new ProtectedCaptureStorage(IntegrationDirectory).ApplyProtectedAcl(IntegrationDirectory);
        }

        try
        {
            await WriteLoaderAsync(cancellationToken).ConfigureAwait(false);
            await _profileInstaller.InstallAsync(profilePath, LoaderPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (previousLoader is null)
            {
                if (File.Exists(LoaderPath))
                {
                    File.Delete(LoaderPath);
                }
            }
            else
            {
                await RestoreLoaderAsync(previousLoader).ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task RemoveAsync(string profilePath, CancellationToken cancellationToken = default)
    {
        CleanupTransactionResidue();
        Directory.CreateDirectory(IntegrationDirectory);
        string heldLoader = Path.Combine(IntegrationDirectory, $".{Path.GetFileName(LoaderPath)}.{Guid.NewGuid():N}.rollback.tmp");
        bool held = false;
        try
        {
            if (File.Exists(LoaderPath))
            {
                File.Move(LoaderPath, heldLoader, overwrite: false);
                held = true;
            }
            await _profileInstaller.RemoveAsync(profilePath, cancellationToken).ConfigureAwait(false);
            if (held && File.Exists(heldLoader)) File.Delete(heldLoader);
        }
        catch
        {
            if (held && File.Exists(heldLoader) && !File.Exists(LoaderPath))
                File.Move(heldLoader, LoaderPath, overwrite: false);
            throw;
        }
    }

    private void CleanupTransactionResidue()
    {
        if (!Directory.Exists(IntegrationDirectory)) return;
        string prefix = $".{Path.GetFileName(LoaderPath)}.";
        foreach (string path in Directory.EnumerateFiles(IntegrationDirectory, $"{prefix}*.tmp", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task WriteLoaderAsync(CancellationToken cancellationToken)
    {
        string temporaryPath = Path.Combine(IntegrationDirectory, $".{Path.GetFileName(LoaderPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using Stream resource = OpenLoaderResource();
            using StreamReader reader = new(resource, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string template = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            string loader = template.Replace(
                ExecutablePathToken,
                ExecutablePath.Replace("'", "''", StringComparison.Ordinal),
                StringComparison.Ordinal);
            byte[] loaderBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(loader);
            await using (FileStream target = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await target.WriteAsync(loaderBytes, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, LoaderPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task RestoreLoaderAsync(byte[] previousLoader)
    {
        string temporaryPath = Path.Combine(
            IntegrationDirectory,
            $".{Path.GetFileName(LoaderPath)}.{Guid.NewGuid():N}.rollback.tmp");
        try
        {
            await using (FileStream target = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await target.WriteAsync(previousLoader, CancellationToken.None).ConfigureAwait(false);
                await target.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            File.Move(temporaryPath, LoaderPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static Stream OpenLoaderResource()
    {
        Assembly assembly = typeof(PowerShellIntegrationInstaller).Assembly;
        string name = assembly.GetManifestResourceNames().Single(value =>
            value.EndsWith("PowerShell.TerminalTranslator.Profile.ps1", StringComparison.Ordinal));
        return assembly.GetManifestResourceStream(name) ??
            throw new InvalidOperationException("Managed PowerShell loader resource is unavailable.");
    }
}
