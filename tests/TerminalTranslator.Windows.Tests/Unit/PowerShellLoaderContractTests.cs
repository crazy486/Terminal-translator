using TerminalTranslator.Windows.PowerShell;

namespace TerminalTranslator.Windows.Tests.Unit;

[TestClass]
public sealed class PowerShellLoaderContractTests
{
    [TestMethod]
    public async Task Install_WritesVersionedLocalLoaderAndMarkedProfileBlockIdempotently()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        string executable = Path.Combine(temporary.Path, "published tt.exe");
        await File.WriteAllBytesAsync(executable, []);
        await File.WriteAllTextAsync(profile, "function prompt { 'CUSTOM> ' }\r\n");
        PowerShellIntegrationInstaller installer = new(
            Path.Combine(temporary.Path, "local"),
            executablePath: executable);

        await installer.InstallAsync(profile);
        string once = await File.ReadAllTextAsync(profile);
        await installer.InstallAsync(profile);

        Assert.AreEqual(once, await File.ReadAllTextAsync(profile));
        Assert.IsTrue(File.Exists(installer.LoaderPath));
        string loader = await File.ReadAllTextAsync(installer.LoaderPath);
        StringAssert.Contains(loader, "TT_HOSTED_SESSION_ID");
        StringAssert.Contains(loader, "TtOriginalPrompt");
        StringAssert.Contains(loader, "TtCaptureIntegrationVersion");
        StringAssert.Contains(loader, executable.Replace("'", "''", StringComparison.Ordinal));
        Assert.IsLessThan(
            loader.IndexOf("Initialize-TtCapture", StringComparison.Ordinal),
            loader.IndexOf("$env:TT_CAPTURE_SESSION_ID = $null", StringComparison.Ordinal));
        Assert.IsFalse(loader.Contains("& tt", StringComparison.Ordinal));
        Assert.IsFalse(loader.Contains("Get-Command tt", StringComparison.Ordinal));
        Assert.IsFalse(loader.Contains("^PS", StringComparison.Ordinal));
        StringAssert.Contains(once, installer.LoaderPath.Replace("'", "''", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InstalledLoader_UsesBoundExecutableWhenPathCannotResolveTt()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        string integration = Path.Combine(temporary.Path, "managed integration");
        string staging = Path.Combine(temporary.Path, "capture staging.txt");
        string bridge = Path.Combine(temporary.Path, "published bridge.ps1");
        string escapedStaging = staging.Replace("'", "''", StringComparison.Ordinal);
        await File.WriteAllTextAsync(bridge, $"param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Remaining)\r\nif ($Remaining -contains 'initialize') {{ 'session=33333333333333333333333333333333'; 'nonce=' + ('C' * 64); 'staging={escapedStaging}' }}\r\n");
        PowerShellIntegrationInstaller installer = new(integration, executablePath: bridge);
        await installer.InstallAsync(profile);

        string loader = installer.LoaderPath.Replace("'", "''", StringComparison.Ordinal);
        string command = $"$env:PATH=''; function prompt {{ 'PROBE> ' }}; . '{loader}'; 'ACTIVE=' + $script:TtCaptureActive; 'SHELL-ALIVE'";
        ProcessResult result = await RunPowerShellAsync(command);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        StringAssert.Contains(result.Output, "ACTIVE=True");
        StringAssert.Contains(result.Output, "SHELL-ALIVE");
        Assert.IsFalse(result.Error.Contains("Capture unavailable", StringComparison.Ordinal), result.Error);
    }

    [TestMethod]
    public async Task MissingBoundExecutable_ReportsContentFreeLoaderCategoryAndKeepsShellUsable()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        string missing = Path.Combine(temporary.Path, "missing", "tt.exe");
        PowerShellIntegrationInstaller installer = new(
            Path.Combine(temporary.Path, "integration"),
            executablePath: missing);
        await installer.InstallAsync(profile);

        string loader = installer.LoaderPath.Replace("'", "''", StringComparison.Ordinal);
        ProcessResult result = await RunPowerShellAsync($"function prompt {{ 'PROBE> ' }}; . '{loader}'; prompt; 'SHELL-ALIVE'");

        Assert.AreEqual(0, result.ExitCode, result.Error);
        StringAssert.Contains(result.Error, "LoaderBridgeFailed");
        StringAssert.Contains(result.Output, "PROBE> ");
        StringAssert.Contains(result.Output, "SHELL-ALIVE");
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string command)
    {
        string powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        System.Diagnostics.ProcessStartInfo start = new(powerShell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-loader-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
