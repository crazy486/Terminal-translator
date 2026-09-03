using TerminalTranslator.Windows.PowerShell;

namespace TerminalTranslator.Windows.Tests.Unit;

[TestClass]
public sealed class PowerShellLoaderContractTests
{
    [TestMethod]
    public async Task InstallerGeneratedLoader_ExactlyPreservesRepositoryProducerDrainContract()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        string bridge = Path.Combine(temporary.Path, "capture bridge.ps1");
        await File.WriteAllTextAsync(bridge, "param()\r\n");
        PowerShellIntegrationInstaller installer = new(
            Path.Combine(temporary.Path, "PowerShell"),
            executablePath: bridge,
            productDirectory: temporary.Path);

        await installer.InstallAsync(profile);

        string repositoryProfile = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "TerminalTranslator.Profile.ps1"));
        string generated = await File.ReadAllTextAsync(installer.LoaderPath);
        string expected = repositoryProfile.Replace(
            "__TT_EXECUTABLE_PATH__",
            bridge.Replace("'", "''", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.AreEqual(expected, generated);
        AssertDrainContract(generated);
    }

    [TestMethod]
    public async Task DefaultInstallLayout_UsesProductOwnedLocalApplicationDataVersion()
    {
        using TemporaryDirectory temporary = new();
        string executable = Path.Combine(temporary.Path, "published.exe");
        await File.WriteAllTextAsync(executable, "published-product");

        PowerShellIntegrationInstaller installer = new(executablePath: executable);

        string expectedProduct = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerminalTranslator");
        Assert.AreEqual(Path.GetFullPath(expectedProduct), installer.ProductDirectory);
        Assert.AreEqual(Path.Combine(expectedProduct, "PowerShell"), installer.IntegrationDirectory);
        StringAssert.StartsWith(
            installer.InstalledExecutablePath,
            Path.Combine(expectedProduct, "Versions") + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual("tt.exe", Path.GetFileName(installer.InstalledExecutablePath));
    }

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
        StringAssert.Contains(loader, installer.InstalledExecutablePath.Replace("'", "''", StringComparison.Ordinal));
        Assert.IsTrue(File.Exists(installer.InstalledExecutablePath));
        Assert.IsLessThan(
            loader.IndexOf("Initialize-TtCapture", StringComparison.Ordinal),
            loader.IndexOf("$env:TT_CAPTURE_SESSION_ID = $null", StringComparison.Ordinal));
        Assert.IsFalse(loader.Contains("& tt", StringComparison.Ordinal));
        StringAssert.Contains(loader, "Get-Command -Name tt");
        StringAssert.Contains(loader, "Set-Alias -Name tt -Value $script:TtExecutablePath -Scope Global -Force");
        StringAssert.Contains(loader, "TtManagedCommandOwner");
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

    private static void AssertDrainContract(string loader)
    {
        int pipelineComplete = loader.IndexOf("TranscribePipelineComplete", StringComparison.Ordinal);
        int flush = loader.IndexOf("FlushContentToDisk", StringComparison.Ordinal);
        int nativeReaderMode = loader.IndexOf("AlwaysCaptureApplicationIO", StringComparison.Ordinal);
        int completion = loader.IndexOf("Complete-TtTranscriptProducer", nativeReaderMode, StringComparison.Ordinal);
        int firstPass = loader.IndexOf("producer-pass-one", completion, StringComparison.Ordinal);
        int secondPass = loader.IndexOf("producer-pass-two", firstPass, StringComparison.Ordinal);
        int stop = loader.IndexOf("Stop-Transcript -ErrorAction Stop", secondPass, StringComparison.Ordinal);
        int metadata = loader.IndexOf("transcript-drained=$ttTranscriptDrained", stop, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, pipelineComplete);
        Assert.IsGreaterThan(pipelineComplete, flush);
        Assert.IsGreaterThanOrEqualTo(0, nativeReaderMode);
        Assert.IsGreaterThan(nativeReaderMode, completion);
        Assert.IsGreaterThan(completion, firstPass);
        Assert.IsGreaterThan(firstPass, secondPass);
        Assert.IsGreaterThan(secondPass, stop);
        Assert.IsGreaterThan(stop, metadata);
        StringAssert.Contains(loader, "OutputToLog");
        StringAssert.Contains(loader, "OutputBeingLogged");
        StringAssert.Contains(loader, "native-file-redirection=");
        StringAssert.Contains(loader, "FileRedirectionAst");
        StringAssert.Contains(loader, "Register-TtNativeCaptureCommandHook");
        StringAssert.Contains(loader, "AddToHistoryHandler");
        StringAssert.Contains(loader, "priorHandler(line)");
        StringAssert.Contains(loader, "_delayedOneTimeInitCompleted");
        Assert.IsFalse(loader.Contains("GetBufferState", StringComparison.Ordinal));
        StringAssert.Contains(loader, "ReadLastHistoryCommand");
        StringAssert.Contains(loader, "ApplyLastAccepted");
        StringAssert.Contains(loader, "HandleAcceptedHistory");
        StringAssert.Contains(loader, "Restore-TtNativeCaptureCommandHook");
        StringAssert.Contains(loader, "IsTtySensitive");
        StringAssert.Contains(loader, "\"codex\"");
        StringAssert.Contains(loader, "Set-TtNativeCaptureMode $false");
        Assert.IsLessThan(
            loader.IndexOf("Start-Transcript -LiteralPath $Path", StringComparison.Ordinal),
            loader.IndexOf("Set-TtNativeCaptureMode $false", StringComparison.Ordinal));
    }

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
