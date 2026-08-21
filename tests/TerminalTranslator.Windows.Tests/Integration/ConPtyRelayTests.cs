using System.Diagnostics;
using System.Text;
using TerminalTranslator.Windows.ConPty;
using TerminalTranslator.Windows.Console;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class ConPtyRelayTests
{
    [TestMethod]
    public async Task WindowsPowerShell51_DefaultConPty_RoundTripsUtf8InputAndOutput()
    {
        await using ConPtySession session = ConPtySession.StartPowerShell(Environment.CurrentDirectory);
        await using MemoryStream programOutput = new();
        Task outputTask = new ConsoleOutputRelay(session.Output, programOutput).CopyAsync(CancellationToken.None);

        const string expected = "Unicode round trip: 你好，世界";
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes($"Write-Output '{expected}'\r\nexit 0\r\n"));
        await session.Input.FlushAsync();

        int exitCode = await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await outputTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(Encoding.UTF8.GetString(programOutput.ToArray()), expected);
    }

    [TestMethod]
    public async Task PowerShellChild_RelaysStdinStdoutUnicodeAndExitCode()
    {
        await using ConPtySession session = ConPtySession.StartPowerShell(Environment.CurrentDirectory);
        await using MemoryStream programOutput = new();
        ConsoleOutputRelay outputRelay = new(session.Output, programOutput);
        Task outputTask = outputRelay.CopyAsync(CancellationToken.None);

        string command = "$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); " +
            "Write-Output 'ConPTY says hello 你好'; exit 23\r\n";
        await new ConsoleInputRelay(
            new MemoryStream(Encoding.UTF8.GetBytes(command)),
            session.Input).CopyAsync(CancellationToken.None);

        int exitCode = await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await outputTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(23, exitCode);
        StringAssert.Contains(Encoding.UTF8.GetString(programOutput.ToArray()), "ConPTY says hello 你好");
    }

    [TestMethod]
    public async Task ChildExitCode_IsPropagated()
    {
        await using ConPtySession session = ConPtySession.Start(
            "powershell.exe",
            "-NoLogo -NoProfile -Command \"exit 23\"",
            Environment.CurrentDirectory);
        Task drain = new ConsoleOutputRelay(session.Output, Stream.Null).CopyAsync(CancellationToken.None);
        int exitCode = await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        session.ClosePseudoConsole();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(23, exitCode);
    }

    [TestMethod]
    public async Task InteractiveProbe_FinalFrameIsDrainedBeforeExitCodeReturnsToHost()
    {
        string repositoryRoot = FindRepositoryRoot();
        string configuration = AppContext.BaseDirectory.Contains("\\Debug\\", StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";
        string probe = Path.Combine(
            repositoryRoot,
            "tests", "TerminalTranslator.Windows.Tests", "Fixtures", "InteractiveProbe",
            "bin", configuration, "net10.0", "tt-interactive-probe.dll");
        Assert.IsTrue(File.Exists(probe), probe);
        await using ConPtySession session = ConPtySession.Start(
            "dotnet.exe", $"\"{probe}\" frames", repositoryRoot);
        await using MemoryStream output = new();
        Task drain = new ConsoleOutputRelay(session.Output, output).CopyAsync(CancellationToken.None);

        int exitCode = await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        session.ClosePseudoConsole();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(17, exitCode);
        StringAssert.Contains(Encoding.UTF8.GetString(output.ToArray()), "final visible frame");
    }

    [TestMethod]
    public async Task OutputRelay_PreservesEveryProgramByteAndAnalysisOfferDoesNotAwaitConsumer()
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes("\u001b[31mraw 中文 output\u001b[0m\r\n");
        await using MemoryStream source = new(sourceBytes);
        await using MemoryStream program = new();
        RecordingSink analysis = new();
        Stopwatch stopwatch = Stopwatch.StartNew();

        await new ConsoleOutputRelay(source, program, analysis).CopyAsync(CancellationToken.None);

        stopwatch.Stop();
        CollectionAssert.AreEqual(sourceBytes, program.ToArray());
        CollectionAssert.AreEqual(sourceBytes, analysis.Offered.Single());
        Assert.IsLessThan(TimeSpan.FromMilliseconds(100), stopwatch.Elapsed);
    }

    [TestMethod]
    public async Task InputRelay_PreservesCtrlCAndPastedUnicodeBytes()
    {
        byte[] input = [3, .. Encoding.UTF8.GetBytes("粘贴 text\r\n")];
        await using MemoryStream source = new(input);
        await using MemoryStream destination = new();
        await new ConsoleInputRelay(source, destination).CopyAsync(CancellationToken.None);
        CollectionAssert.AreEqual(input, destination.ToArray());
    }

    [TestMethod]
    public async Task PowerShellChild_CtrlCInterruptsCommandAndSessionRemainsInteractive()
    {
        await using ConPtySession session = ConPtySession.StartPowerShell(Environment.CurrentDirectory);
        await using MemoryStream programOutput = new();
        Task outputTask = new ConsoleOutputRelay(session.Output, programOutput).CopyAsync(CancellationToken.None);

        await WaitUntilAsync(
            () => Encoding.UTF8.GetString(programOutput.ToArray()).Contains("PS ", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("Start-Sleep -Seconds 30\r\n"));
        await session.Input.FlushAsync();
        await WaitUntilAsync(
            () => Encoding.UTF8.GetString(programOutput.ToArray()).Contains("Start-Sleep", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        await Task.Delay(500);
        await session.Input.WriteAsync(new byte[] { 3 });
        await session.Input.FlushAsync();
        await WaitUntilAsync(
            () => CountOccurrences(Encoding.UTF8.GetString(programOutput.ToArray()), "PS ") >= 2,
            TimeSpan.FromSeconds(5));
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("Write-Output 'after interrupt'; exit 0\r\n"));
        await session.Input.FlushAsync();

        int exitCode = await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await outputTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(Encoding.UTF8.GetString(programOutput.ToArray()), "after interrupt");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                Assert.Fail("Timed out waiting for ConPTY output.");
            }

            await Task.Delay(20);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TerminalTranslator.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new AssertFailedException("Repository root was not found.");
    }

    private sealed class RecordingSink : INonBlockingAnalysisSink
    {
        public bool IsEnabled => true;

        public List<byte[]> Offered { get; } = [];

        public bool TryOffer(ReadOnlyMemory<byte> bytes)
        {
            Offered.Add(bytes.ToArray());
            return true;
        }
    }
}
