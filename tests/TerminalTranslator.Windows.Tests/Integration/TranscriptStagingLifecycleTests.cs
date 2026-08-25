using System.Diagnostics;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class TranscriptStagingLifecycleTests
{
    [TestMethod]
    public async Task PowerShell51_RepeatedCyclesPreserveMixedStreamsWithoutStatusNoise()
    {
        using TemporaryDirectory temporary = new();
        string script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PowerShellCaptureProbe", "Invoke-CaptureProbe.ps1");
        ProcessStartInfo start = new("powershell.exe")
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
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-OutputDirectory");
        start.ArgumentList.Add(temporary.Path);

        using Process process = Process.Start(start)!;
        string standardOutput = await process.StandardOutput.ReadToEndAsync();
        string standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.AreEqual(0, process.ExitCode, standardError);
        Assert.IsFalse(standardOutput.Contains("Transcript started", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(standardOutput.Contains("Transcript stopped", StringComparison.OrdinalIgnoreCase));
        for (int index = 1; index <= 3; index++)
        {
            string transcript = await File.ReadAllTextAsync(Path.Combine(temporary.Path, $"cycle-{index}.txt"));
            StringAssert.Contains(standardOutput, $"NATIVE-OUT-{index}");
            StringAssert.Contains(standardError, $"NATIVE-ERR-{index}");
            StringAssert.Contains(transcript, $"PS-OUT-{index}");
            StringAssert.Contains(transcript, $"PS-ERR-{index}");
        }
    }

    [TestMethod]
    public async Task Controller_ContainsStartAndStopFailures()
    {
        FakeTranscriptAdapter adapter = new() { FailStart = true };
        TranscriptStagingController controller = new(adapter);
        Assert.IsFalse(await controller.TryStartAsync("staging.txt", CancellationToken.None));
        Assert.AreEqual(TranscriptStagingState.Abandoned, controller.State);

        adapter.FailStart = false;
        Assert.IsTrue(await controller.TryStartAsync("staging.txt", CancellationToken.None));
        adapter.FailStop = true;
        Assert.IsFalse(await controller.TryStopAsync(CancellationToken.None));
        Assert.AreEqual(TranscriptStagingState.Abandoned, controller.State);
    }

    private sealed class FakeTranscriptAdapter : ITranscriptLifecycleAdapter
    {
        public bool FailStart { get; set; }
        public bool FailStop { get; set; }

        public Task StartAsync(string path, CancellationToken cancellationToken) =>
            FailStart ? Task.FromException(new IOException("start")) : Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) =>
            FailStop ? Task.FromException(new IOException("stop")) : Task.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-transcript-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
