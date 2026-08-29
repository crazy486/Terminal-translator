using TerminalTranslator.Windows.PowerShell;

namespace TerminalTranslator.Windows.Tests.TestDoubles;

internal sealed class InstalledPowerShellLoader : IAsyncDisposable
{
    private InstalledPowerShellLoader(string directory, string loaderPath)
    {
        DirectoryPath = directory;
        LoaderPath = loaderPath;
    }

    public string DirectoryPath { get; }

    public string LoaderPath { get; }

    public static async Task<InstalledPowerShellLoader> CreateAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tt-installed-loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string bridge = Path.Combine(directory, "test capture bridge.ps1");
            await File.WriteAllTextAsync(
                bridge,
                "param([Parameter(ValueFromRemainingArguments=$true)][object[]]$Remaining)\r\n" +
                "& (Get-Command -Name tt -CommandType Function -ErrorAction Stop) @Remaining\r\n");
            PowerShellIntegrationInstaller installer = new(
                Path.Combine(directory, "integration"),
                executablePath: bridge);
            await installer.InstallAsync(Path.Combine(directory, "profile.ps1"));
            return new InstalledPowerShellLoader(directory, installer.LoaderPath);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(DirectoryPath, recursive: true);
        return ValueTask.CompletedTask;
    }
}
