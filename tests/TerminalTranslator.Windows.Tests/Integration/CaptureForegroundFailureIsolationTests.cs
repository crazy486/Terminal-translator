using System.Diagnostics;
using TerminalTranslator.Windows.Tests.TestDoubles;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class CaptureForegroundFailureIsolationTests
{
    [TestMethod]
    [DataRow("storage")]
    [DataRow("acl")]
    [DataRow("transcript-start")]
    [DataRow("transcript-stop")]
    [DataRow("metadata")]
    [DataRow("interval-restart")]
    public async Task ForegroundCaptureFault_PreservesUserStreamsExitFactsPromptAndShell(string fault)
    {
        using TemporaryDirectory temporary = new();
        await using InstalledPowerShellLoader installedLoader = await InstalledPowerShellLoader.CreateAsync();
        string loader = installedLoader.LoaderPath.Replace("'", "''", StringComparison.Ordinal);
        string staging = Path.Combine(temporary.Path, "staging.txt").Replace("'", "''", StringComparison.Ordinal);
        string blockedParent = Path.Combine(temporary.Path, "blocked-parent");
        await File.WriteAllTextAsync(blockedParent, "not a directory");
        string missingStaging = Path.Combine(blockedParent, "next.txt").Replace("'", "''", StringComparison.Ordinal);
        string initialize = fault switch
        {
            "storage" => "'unavailable=storage'",
            "acl" => "'unavailable=storage'",
            "transcript-start" => $"'session=33333333333333333333333333333333'; 'nonce=' + ('C' * 64); 'staging={missingStaging}'",
            _ => $"'session=33333333333333333333333333333333'; 'nonce=' + ('C' * 64); 'staging={staging}'",
        };
        string boundary = fault switch
        {
            "metadata" => "'unavailable=metadata'",
            "interval-restart" => $"'staging={missingStaging}'",
            _ => "'unavailable=transcriptstop'",
        };
        string stopOverride = fault == "transcript-stop"
            ? "function global:Stop-Transcript { throw 'synthetic stop failure' }"
            : string.Empty;
        string startOverride = fault == "interval-restart"
            ? "function global:Start-Transcript { throw 'synthetic restart failure' }"
            : string.Empty;
        string command = $@"
function global:tt {{
  param([Parameter(ValueFromRemainingArguments=$true)]$Remaining)
  if ($Remaining -contains 'initialize') {{ {initialize} }}
  elseif ($Remaining -contains 'boundary') {{ {boundary} }}
  elseif ($Remaining -contains 'recover') {{ 'unavailable=unknown' }}
}}
function global:prompt {{ 'CUSTOM:' + $? + ':' + $global:LASTEXITCODE + '> ' }}
. '{loader}'
{stopOverride}
{startOverride}
[Console]::Out.WriteLine('USER-STDOUT-{fault}')
[Console]::Error.WriteLine('USER-STDERR-{fault}')
& $env:ComSpec /d /c exit 23
prompt
'SHELL-ALIVE-{fault}'
";

        ProcessResult result = await RunPowerShellAsync(command);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        StringAssert.Contains(result.Output, $"USER-STDOUT-{fault}");
        StringAssert.Contains(result.Error, $"USER-STDERR-{fault}");
        // A direct scripted prompt invocation itself establishes a successful PowerShell pipeline,
        // while the native exit fact must remain the user's 23 across TT maintenance.
        StringAssert.Contains(result.Output, "CUSTOM:True:23> ");
        StringAssert.Contains(result.Output, $"SHELL-ALIVE-{fault}");
        StringAssert.Contains(result.Error, "Capture unavailable");
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
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        using Process process = Process.Start(start)!;
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-foreground-fault-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
