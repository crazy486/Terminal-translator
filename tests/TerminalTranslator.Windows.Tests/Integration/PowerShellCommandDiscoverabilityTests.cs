using System.Diagnostics;
using TerminalTranslator.Windows.PowerShell;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class PowerShellCommandDiscoverabilityTests
{
    [TestMethod]
    public async Task FreshPowerShell_ResolvesManagedCommandAndPreservesArgumentsStreamsExitAndRedirection()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows PowerShell 5.1 is Windows-only.");

        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        string bridge = await WriteBridgeAsync(temporary.Path, "bridge.cmd", "v1");
        PowerShellIntegrationInstaller installer = new(
            Path.Combine(temporary.Path, "PowerShell"),
            executablePath: bridge,
            productDirectory: temporary.Path);
        await installer.InstallAsync(profile);
        string redirected = Path.Combine(temporary.Path, "redirected.txt");

        string command = string.Join("; ",
            $". '{Escape(profile)}'",
            "'TYPE=' + (Get-Command tt).CommandType",
            "tt last 'two words' '中文'",
            "$successState=$?",
            "'SUCCESS_EXIT=' + $LASTEXITCODE",
            "'SUCCESS_STATE=' + $successState",
            "$env:TT_TEST_EXIT='23'",
            "tt status",
            "$failureState=$?",
            "'FAILURE_EXIT=' + $LASTEXITCODE",
            "'FAILURE_STATE=' + $failureState",
            "$env:TT_TEST_EXIT='0'",
            $"tt configure enabled > '{Escape(redirected)}'",
            "'REDIRECT_EXIT=' + $LASTEXITCODE",
            $"'REDIRECTED=' + ((Get-Content -Raw '{Escape(redirected)}').Trim())");
        ProcessResult result = await RunPowerShellAsync(command);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        StringAssert.Contains(result.Output, "TYPE=Alias");
        StringAssert.Contains(result.Output, "STDOUT:v1:last \"two words\"");
        StringAssert.Contains(result.Error, "STDERR:v1:last \"two words\"");
        StringAssert.Contains(result.Output, "SUCCESS_EXIT=0");
        StringAssert.Contains(result.Output, "SUCCESS_STATE=True");
        StringAssert.Contains(result.Error, "STDERR:v1:status");
        StringAssert.Contains(result.Output, "FAILURE_EXIT=23");
        StringAssert.Contains(result.Output, "FAILURE_STATE=False");
        StringAssert.Contains(result.Output, "REDIRECT_EXIT=0");
        StringAssert.Contains(result.Output, "REDIRECTED=STDOUT:v1:configure enabled");
        Assert.IsFalse(result.Output.Contains("STDOUT:v1:configure enabled\r\nSTDOUT", StringComparison.Ordinal));
        Assert.IsFalse((await File.ReadAllTextAsync(redirected)).Contains("\u001b[", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExistingTtFunction_IsPreservedWithOneContentFreeWarning()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows PowerShell 5.1 is Windows-only.");

        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        string bridge = await WriteBridgeAsync(temporary.Path, "bridge.cmd", "managed");
        PowerShellIntegrationInstaller installer = new(
            Path.Combine(temporary.Path, "PowerShell"),
            executablePath: bridge,
            productDirectory: temporary.Path);
        await installer.InstallAsync(profile);

        ProcessResult result = await RunPowerShellAsync(
            $"function global:tt {{ 'EXISTING:' + ($args -join '|') }}; . '{Escape(profile)}'; tt owner check; 'WARNINGS=' + @($Warning).Count");

        Assert.AreEqual(0, result.ExitCode, result.Error);
        StringAssert.Contains(result.Output, "EXISTING:owner|check");
        Assert.IsFalse(result.Output.Contains("STDOUT:managed", StringComparison.Ordinal));
        string combined = result.Output + result.Error;
        StringAssert.Contains(combined, "Terminal Translator did not replace it");
        Assert.AreEqual(1, Count(combined, "Terminal Translator did not replace it"));
    }

    [TestMethod]
    public async Task InstallUpgradeAndRemove_KeepOneLoaderCommandTargetAndContentAddressedProductBinary()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        string source1 = Path.Combine(temporary.Path, "source-v1.exe");
        string source2 = Path.Combine(temporary.Path, "source-v2.exe");
        await File.WriteAllTextAsync(source1, "product-v1");
        await File.WriteAllTextAsync(source2, "product-v2");

        PowerShellIntegrationInstaller first = new(
            Path.Combine(temporary.Path, "PowerShell"),
            executablePath: source1,
            productDirectory: temporary.Path);
        await first.InstallAsync(profile);
        string firstProfile = await File.ReadAllTextAsync(profile);
        string firstLoader = await File.ReadAllTextAsync(first.LoaderPath);
        Assert.IsTrue(File.Exists(first.InstalledExecutablePath));
        Assert.AreEqual("product-v1", await File.ReadAllTextAsync(first.InstalledExecutablePath));
        StringAssert.Contains(firstLoader, first.InstalledExecutablePath.Replace("'", "''", StringComparison.Ordinal));
        Assert.AreEqual(1, Count(firstLoader, "$script:TtExecutablePath = '"));

        await first.InstallAsync(profile);
        Assert.AreEqual(firstProfile, await File.ReadAllTextAsync(profile));
        Assert.AreEqual(firstLoader, await File.ReadAllTextAsync(first.LoaderPath));

        PowerShellIntegrationInstaller second = new(
            Path.Combine(temporary.Path, "PowerShell"),
            executablePath: source2,
            productDirectory: temporary.Path);
        await second.InstallAsync(profile);
        string upgradedLoader = await File.ReadAllTextAsync(second.LoaderPath);
        Assert.AreNotEqual(first.InstalledExecutablePath, second.InstalledExecutablePath);
        Assert.AreEqual("product-v2", await File.ReadAllTextAsync(second.InstalledExecutablePath));
        StringAssert.Contains(upgradedLoader, second.InstalledExecutablePath.Replace("'", "''", StringComparison.Ordinal));
        Assert.IsFalse(upgradedLoader.Contains(first.InstalledExecutablePath, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, Count(await File.ReadAllTextAsync(profile), PowerShellProfileInstaller.BlockBegin));

        await second.RemoveAsync(profile);
        Assert.IsFalse(File.Exists(second.LoaderPath));
        Assert.AreEqual(string.Empty, await File.ReadAllTextAsync(profile));
        Assert.IsFalse(Directory.Exists(second.VersionsDirectory));
    }

    private static async Task<string> WriteBridgeAsync(string directory, string name, string version)
    {
        string path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, string.Join("\r\n",
            "@echo off",
            "if /I \"%~1\"==\"__capture\" (",
            "  if /I \"%~2\"==\"initialize\" echo disabled=1",
            "  exit /b 0",
            ")",
            $"echo STDOUT:{version}:%*",
            $"echo STDERR:{version}:%* 1>&2",
            "if defined TT_TEST_EXIT exit /b %TT_TEST_EXIT%",
            "exit /b 0",
            string.Empty));
        return path;
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string command)
    {
        ProcessStartInfo start = new("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private static string Escape(string path) => path.Replace("'", "''", StringComparison.Ordinal);

    private static int Count(string value, string needle) =>
        value.Split(needle, StringSplitOptions.None).Length - 1;

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt command {Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
