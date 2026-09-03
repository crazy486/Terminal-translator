using System.Reflection;
using System.Security.Cryptography;
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
        string? executablePath = null,
        string? productDirectory = null)
    {
        string defaultProductDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerminalTranslator");
        ProductDirectory = Path.GetFullPath(productDirectory ??
            (integrationDirectory is null ? defaultProductDirectory : integrationDirectory));
        IntegrationDirectory = Path.GetFullPath(integrationDirectory ??
            Path.Combine(ProductDirectory, "PowerShell"));
        VersionsDirectory = Path.Combine(ProductDirectory, "Versions");
        LoaderPath = Path.Combine(IntegrationDirectory, "TerminalTranslator.Profile.ps1");
        SourceExecutablePath = Path.GetFullPath(executablePath ?? Environment.ProcessPath ??
            throw new InvalidOperationException("The Terminal Translator executable path is unavailable."));
        InstalledExecutablePath = ResolveInstalledExecutablePath(SourceExecutablePath, VersionsDirectory);
        ExecutablePath = InstalledExecutablePath;
        _profileInstaller = profileInstaller ?? new PowerShellProfileInstaller();
    }

    public string ProductDirectory { get; }

    public string IntegrationDirectory { get; }

    public string VersionsDirectory { get; }

    public string LoaderPath { get; }

    public string SourceExecutablePath { get; }

    public string InstalledExecutablePath { get; }

    public string ExecutablePath { get; }

    public async Task InstallAsync(string profilePath, CancellationToken cancellationToken = default)
    {
        CleanupTransactionResidue();
        byte[]? previousLoader = File.Exists(LoaderPath)
            ? await File.ReadAllBytesAsync(LoaderPath, cancellationToken).ConfigureAwait(false)
            : null;
        Directory.CreateDirectory(ProductDirectory);
        Directory.CreateDirectory(IntegrationDirectory);
        if (OperatingSystem.IsWindows())
        {
            ProtectedCaptureStorage protection = new(ProductDirectory);
            protection.ApplyProtectedAcl(ProductDirectory);
            protection.ApplyProtectedAcl(IntegrationDirectory);
        }

        bool installedExecutableCreated = false;
        try
        {
            installedExecutableCreated = await InstallExecutableAsync(cancellationToken).ConfigureAwait(false);
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

            if (installedExecutableCreated)
            {
                TryDeleteInstalledVersion(InstalledExecutablePath);
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
            CleanupInstalledVersionsBestEffort();
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

    private async Task<bool> InstallExecutableAsync(CancellationToken cancellationToken)
    {
        if (string.Equals(SourceExecutablePath, InstalledExecutablePath, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(SourceExecutablePath) ||
            !Path.GetExtension(SourceExecutablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string versionDirectory = Path.GetDirectoryName(InstalledExecutablePath)!;
        Directory.CreateDirectory(VersionsDirectory);
        Directory.CreateDirectory(versionDirectory);
        if (OperatingSystem.IsWindows())
        {
            ProtectedCaptureStorage protection = new(ProductDirectory);
            protection.ApplyProtectedAcl(VersionsDirectory);
            protection.ApplyProtectedAcl(versionDirectory);
        }

        string expectedHash = Path.GetFileName(versionDirectory);
        if (File.Exists(InstalledExecutablePath))
        {
            ValidateExecutableHash(InstalledExecutablePath, expectedHash);
            return false;
        }

        string temporaryPath = Path.Combine(versionDirectory, $".tt.{Guid.NewGuid():N}.tmp");
        try
        {
            await using FileStream source = new(
                SourceExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (FileStream target = new(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            ValidateExecutableHash(temporaryPath, expectedHash);
            try
            {
                File.Move(temporaryPath, InstalledExecutablePath, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(InstalledExecutablePath))
            {
                ValidateExecutableHash(InstalledExecutablePath, expectedHash);
                return false;
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private void CleanupInstalledVersionsBestEffort()
    {
        if (!Directory.Exists(VersionsDirectory)) return;
        try
        {
            Directory.Delete(VersionsDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteInstalledVersion(string executablePath)
    {
        try
        {
            string? versionDirectory = Path.GetDirectoryName(executablePath);
            if (versionDirectory is not null && Directory.Exists(versionDirectory))
                Directory.Delete(versionDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string ResolveInstalledExecutablePath(string sourceExecutablePath, string versionsDirectory)
    {
        if (!File.Exists(sourceExecutablePath) ||
            !Path.GetExtension(sourceExecutablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return sourceExecutablePath;
        }

        string hash = ComputeSha256(sourceExecutablePath);
        return Path.Combine(versionsDirectory, hash, "tt.exe");
    }

    private static void ValidateExecutableHash(string path, string expectedHash)
    {
        string actualHash = ComputeSha256(path);
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installed Terminal Translator executable failed integrity validation.");
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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
