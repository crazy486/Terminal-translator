using System.Diagnostics;
using TerminalTranslator.Windows.Tests.TestDoubles;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class CaptureHealthNotificationTests
{
    [TestMethod]
    public async Task FailureEpochWarnsOnceAndRecoveryRestoresOnceOutsideCommandTranscripts()
    {
        using TemporaryDirectory temporary = new();
        await using InstalledPowerShellLoader installedLoader = await InstalledPowerShellLoader.CreateAsync();
        string loader = installedLoader.LoaderPath.Replace("'", "''", StringComparison.Ordinal);
        string first = Path.Combine(temporary.Path, "first.txt").Replace("'", "''", StringComparison.Ordinal);
        string recovered = Path.Combine(temporary.Path, "recovered.txt").Replace("'", "''", StringComparison.Ordinal);
        string next = Path.Combine(temporary.Path, "next.txt").Replace("'", "''", StringComparison.Ordinal);
        string command = $@"
$global:TtBoundaryCalls = 0
$global:TtRecoverCalls = 0
function global:tt {{
  param([Parameter(ValueFromRemainingArguments=$true)]$Remaining)
  if ($Remaining -contains 'initialize') {{
    'session=22222222222222222222222222222222'; 'nonce=' + ('B' * 64); 'staging={first}'
  }}
  elseif ($Remaining -contains 'recover') {{
    $global:TtRecoverCalls++
    if ($global:TtRecoverCalls -eq 1) {{ 'unavailable=cleanup' }} else {{ 'staging={recovered}' }}
  }}
  elseif ($Remaining -contains 'boundary') {{
    $global:TtBoundaryCalls++
    if ($global:TtBoundaryCalls -eq 1) {{ 'unavailable=metadata' }} else {{ 'staging={next}' }}
  }}
}}
function global:prompt {{ 'CUSTOM> ' }}
. '{loader}'
Write-Output 'FIRST-USER-OUTPUT'
$null = prompt
$null = prompt
$null = prompt
Write-Output 'SECOND-USER-OUTPUT'
$null = prompt
'DONE'
";

        ProcessResult result = await RunPowerShellAsync(command);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.AreEqual(1, Count(result.Error, "Capture unavailable"));
        Assert.AreEqual(1, Count(result.Error, "Capture restored"));
        string firstTranscript = await File.ReadAllTextAsync(Path.Combine(temporary.Path, "first.txt"));
        string recoveredTranscript = await File.ReadAllTextAsync(Path.Combine(temporary.Path, "recovered.txt"));
        Assert.IsFalse(firstTranscript.Contains("Capture unavailable", StringComparison.Ordinal));
        Assert.IsFalse(firstTranscript.Contains("Capture restored", StringComparison.Ordinal));
        Assert.IsFalse(recoveredTranscript.Contains("Capture unavailable", StringComparison.Ordinal));
        Assert.IsFalse(recoveredTranscript.Contains("Capture restored", StringComparison.Ordinal));
        StringAssert.Contains(recoveredTranscript, "SECOND-USER-OUTPUT");
    }

    [TestMethod]
    public void HealthStateMachine_AllowsOneWarningPerFailureEpochAndOneRestoration()
    {
        TerminalTranslator.Core.Capture.CaptureHealth health = new();
        Assert.AreEqual(
            TerminalTranslator.Core.Capture.CaptureHealthNotification.CaptureUnavailable,
            health.MarkUnavailable(TerminalTranslator.Core.Capture.CaptureFailureReason.TranscriptStart));
        Assert.AreEqual(
            TerminalTranslator.Core.Capture.CaptureHealthNotification.None,
            health.MarkUnavailable(TerminalTranslator.Core.Capture.CaptureFailureReason.TranscriptStart));
        health.BeginRecovery();
        Assert.AreEqual(
            TerminalTranslator.Core.Capture.CaptureHealthNotification.CaptureRestored,
            health.MarkHealthy());
        Assert.AreEqual(
            TerminalTranslator.Core.Capture.CaptureHealthNotification.CaptureUnavailable,
            health.MarkUnavailable(TerminalTranslator.Core.Capture.CaptureFailureReason.Metadata));
    }

    private static int Count(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;

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
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-health-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
