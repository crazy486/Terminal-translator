using System.Diagnostics;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class PowerShellPromptWrapperTests
{
    [TestMethod]
    public async Task Wrapper_PreservesCustomPromptAndSnapshotsStateBeforeMaintenance()
    {
        string loader = Path.Combine(AppContext.BaseDirectory, "TerminalTranslator.Profile.ps1");
        string escapedLoader = loader.Replace("'", "''", StringComparison.Ordinal);
        string command = $@"
function global:tt {{ param([Parameter(ValueFromRemainingArguments=$true)]$Remaining); if ($Remaining -contains 'initialize') {{ 'disabled' }} }}
function global:prompt {{ 'CUSTOM:' + $? + ':' + $global:LASTEXITCODE + '> ' }}
. '{escapedLoader}'
& $env:ComSpec /d /c exit 7
prompt
";
        ProcessStartInfo start = new("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.AreEqual(0, process.ExitCode, error);
        StringAssert.Contains(output, "CUSTOM:True:7> ");
    }

    [TestMethod]
    public async Task Loader_UsesExplicitHistoryMetadataAndNativeTranscriptLifecycleWithoutPromptRegex()
    {
        string loader = await ReadLoaderAsync();
        StringAssert.Contains(loader, "Get-History -Count 1");
        StringAssert.Contains(loader, "$ttSucceeded = $?");
        StringAssert.Contains(loader, "$ttNativeExitCode = $global:LASTEXITCODE");
        StringAssert.Contains(loader, "Start-Transcript");
        StringAssert.Contains(loader, "Stop-Transcript");
        StringAssert.Contains(loader, "TtOriginalPrompt");
        Assert.IsLessThan(loader.IndexOf("& tt @ttArguments", StringComparison.Ordinal), loader.IndexOf("$ttSucceeded = $?", StringComparison.Ordinal));
        Assert.IsFalse(loader.Contains("^PS", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HostedSessionGuard_DoesNotReplacePrompt()
    {
        string loader = Path.Combine(AppContext.BaseDirectory, "TerminalTranslator.Profile.ps1");
        string escapedLoader = loader.Replace("'", "''", StringComparison.Ordinal);
        string command = $"$env:TT_HOSTED_SESSION_ID='hosted'; function prompt {{ 'ORIGINAL> ' }}; . '{escapedLoader}'; prompt";
        ProcessStartInfo start = new("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, error);
        StringAssert.Contains(output, "ORIGINAL> ");
    }

    private static async Task<string> ReadLoaderAsync()
    {
        string loader = Path.Combine(AppContext.BaseDirectory, "TerminalTranslator.Profile.ps1");
        return await File.ReadAllTextAsync(loader);
    }
}
