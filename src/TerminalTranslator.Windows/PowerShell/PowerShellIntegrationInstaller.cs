using System.Reflection;
using System.Text;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.PowerShell;

public sealed class PowerShellIntegrationInstaller
{
    private readonly PowerShellProfileInstaller _profileInstaller;

    public PowerShellIntegrationInstaller(
        string? integrationDirectory = null,
        PowerShellProfileInstaller? profileInstaller = null)
    {
        IntegrationDirectory = integrationDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerminalTranslator",
            "PowerShell");
        LoaderPath = Path.Combine(IntegrationDirectory, "TerminalTranslator.Profile.ps1");
        _profileInstaller = profileInstaller ?? new PowerShellProfileInstaller();
    }

    public string IntegrationDirectory { get; }

    public string LoaderPath { get; }

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
            await using (FileStream target = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await resource.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
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
