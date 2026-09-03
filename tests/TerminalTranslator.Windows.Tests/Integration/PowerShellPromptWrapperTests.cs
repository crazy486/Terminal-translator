using System.Diagnostics;
using System.Text;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.Capture;
using TerminalTranslator.Windows.ConPty;
using TerminalTranslator.Windows.Console;
using TerminalTranslator.Windows.Tests.TestDoubles;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class PowerShellPromptWrapperTests
{
    [TestMethod]
    public async Task Wrapper_PreservesCustomPromptAndSnapshotsStateBeforeMaintenance()
    {
        await using InstalledPowerShellLoader installedLoader = await InstalledPowerShellLoader.CreateAsync();
        string escapedLoader = installedLoader.LoaderPath.Replace("'", "''", StringComparison.Ordinal);
        string command = $@"
function global:tt {{ param([Parameter(ValueFromRemainingArguments=$true)]$Remaining); if ($Remaining -contains 'initialize') {{ 'disabled=1' }} }}
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
        StringAssert.Contains(loader, "$ttHistory.ExecutionStatus -eq 'Stopped'");
        StringAssert.Contains(loader, "was-interrupted=$ttWasInterrupted");
        StringAssert.Contains(loader, "Get-TtReliableNativeExitCode");
        Assert.IsFalse(loader.Contains("$ttNativeExitCode = $global:LASTEXITCODE", StringComparison.Ordinal));
        StringAssert.Contains(loader, "$global:LASTEXITCODE = $ttObservedLastExitCode");
        StringAssert.Contains(loader, "Start-Transcript");
        StringAssert.Contains(loader, "Stop-Transcript");
        StringAssert.Contains(loader, "TtOriginalPrompt");
        Assert.IsLessThan(loader.IndexOf("Invoke-TtCaptureBridge $ttArguments", StringComparison.Ordinal), loader.IndexOf("$ttSucceeded = $?", StringComparison.Ordinal));
        Assert.IsFalse(loader.Contains("^PS", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ManagedLoader_CtrlCMarksPowerShellAndNativeCommandsInterruptedAndRecoversNextEpoch()
    {
        await using InstalledPowerShellLoader installedLoader = await InstalledPowerShellLoader.CreateAsync();
        string facts = Path.Combine(installedLoader.DirectoryPath, "interruption-facts.tsv");
        string startup = Path.Combine(installedLoader.DirectoryPath, "interruption-profile.ps1");
        string stagingDirectory = installedLoader.DirectoryPath.Replace("'", "''", StringComparison.Ordinal);
        string escapedFacts = facts.Replace("'", "''", StringComparison.Ordinal);
        string escapedLoader = installedLoader.LoaderPath.Replace("'", "''", StringComparison.Ordinal);
        string script = $@"
$global:TtStage = 1
function global:tt {{
  param([Parameter(ValueFromRemainingArguments=$true)][object[]]$Remaining)
  if ($Remaining -contains 'initialize') {{
    'session=88888888888888888888888888888888'
    'nonce=' + ('A' * 64)
    'staging={stagingDirectory}\interruption-staging-1.txt'
    return
  }}
  if ($Remaining -contains 'boundary') {{
    $commandArgument = $Remaining | Where-Object {{ $_ -like 'command-base64=*' }} | Select-Object -Last 1
    $command = if ($commandArgument) {{ [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($commandArgument.Substring(15))) }} else {{ '<none>' }}
    $interruptedArgument = $Remaining | Where-Object {{ $_ -like 'was-interrupted=*' }} | Select-Object -Last 1
    $interrupted = if ($interruptedArgument) {{ $interruptedArgument.Substring(16) }} else {{ '<missing>' }}
    Add-Content -LiteralPath '{escapedFacts}' -Encoding UTF8 -Value (""$command`t$interrupted"")
    $global:TtStage++
    'staging={stagingDirectory}\interruption-staging-' + $global:TtStage + '.txt'
    return
  }}
}}
function global:prompt {{ 'INTERRUPT> ' }}
. '{escapedLoader}'
";
        await File.WriteAllTextAsync(startup, script);
        string arguments = $"-NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File \"{startup}\"";
        await using ConPtySession session = ConPtySession.Start(
            "powershell.exe", arguments, Environment.CurrentDirectory, new Coord(220, 50));
        await using MemoryStream console = new();
        Task drain = new ConsoleOutputRelay(session.Output, console).CopyAsync(CancellationToken.None);

        await WaitForFactLinesAsync(facts, 1, () => Encoding.UTF8.GetString(console.ToArray()));
        const string powerShellCommand = "1..1000 | ForEach-Object { if ($_ -eq 1) { Write-Output 'T130-POWERSHELL-READY' }; Start-Sleep -Milliseconds 50 }";
        await SendAsync(session, powerShellCommand);
        await WaitForConsoleOccurrencesAsync(console, "T130-POWERSHELL-READY", 1);
        await SendCtrlCAsync(session);
        await WaitForFactLinesAsync(facts, 2, () => Encoding.UTF8.GetString(console.ToArray()));

        const string nativeCommand = "ping.exe -t 127.0.0.1";
        await SendAsync(session, nativeCommand);
        await WaitForConsoleOccurrencesAsync(console, "TTL=128", 1);
        await SendCtrlCAsync(session);
        await WaitForFactLinesAsync(facts, 3, () => Encoding.UTF8.GetString(console.ToArray()));

        const string recoveryCommand = "Write-Output 'T130-RECOVERED'";
        await SendAsync(session, recoveryCommand);
        await WaitForFactLinesAsync(facts, 4, () => Encoding.UTF8.GetString(console.ToArray()));
        await SendAsync(session, "exit");
        Assert.AreEqual(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(
            new[]
            {
                "<none>\t<missing>",
                $"{powerShellCommand}\tTrue",
                $"{nativeCommand}\tTrue",
                $"{recoveryCommand}\tFalse",
            },
            (await File.ReadAllLinesAsync(facts)).Select(line => line.TrimStart('\uFEFF')).ToArray());
        string allConsole = Encoding.UTF8.GetString(console.ToArray());
        StringAssert.Contains(allConsole, "T130-RECOVERED");
        Assert.IsFalse(allConsole.Contains("Capture unavailable", StringComparison.Ordinal), allConsole);
    }

    [TestMethod]
    public async Task ManagedLoader_AttributesNativeExitOnlyToOneProvableNativeInvocation()
    {
        await using InstalledPowerShellLoader installedLoader = await InstalledPowerShellLoader.CreateAsync();
        string facts = Path.Combine(installedLoader.DirectoryPath, "termination-facts.tsv");
        string startup = Path.Combine(installedLoader.DirectoryPath, "termination-profile.ps1");
        string stagingDirectory = installedLoader.DirectoryPath.Replace("'", "''", StringComparison.Ordinal);
        string escapedFacts = facts.Replace("'", "''", StringComparison.Ordinal);
        string escapedLoader = installedLoader.LoaderPath.Replace("'", "''", StringComparison.Ordinal);
        string script = $@"
$global:TtStage = 1
function global:tt {{
  param([Parameter(ValueFromRemainingArguments=$true)][object[]]$Remaining)
  if ($Remaining -contains 'initialize') {{
    'session=66666666666666666666666666666666'
    'nonce=' + ('E' * 64)
    'staging={stagingDirectory}\termination-staging-1.txt'
    return
  }}
  if ($Remaining -contains 'boundary') {{
    $commandArgument = $Remaining | Where-Object {{ $_ -like 'command-base64=*' }} | Select-Object -Last 1
    $command = if ($commandArgument) {{ [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($commandArgument.Substring(15))) }} else {{ '<none>' }}
    $succeeded = ($Remaining | Where-Object {{ $_ -like 'succeeded=*' }} | Select-Object -Last 1).Substring(10)
    $nativeArgument = $Remaining | Where-Object {{ $_ -like 'native-exit-code=*' }} | Select-Object -Last 1
    $native = if ($nativeArgument) {{ $nativeArgument.Substring(17) }} else {{ '<null>' }}
    Add-Content -LiteralPath '{escapedFacts}' -Encoding UTF8 -Value (""$command`t$succeeded`t$native"")
    $global:TtStage++
    'staging={stagingDirectory}\termination-staging-' + $global:TtStage + '.txt'
    return
  }}
}}
function global:prompt {{ 'TERM> ' }}
. '{escapedLoader}'
";
        await File.WriteAllTextAsync(startup, script);
        string arguments = $"-NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File \"{startup}\"";
        await using ConPtySession session = ConPtySession.Start("powershell.exe", arguments, Environment.CurrentDirectory, new Coord(180, 40));
        await using MemoryStream console = new();
        Task drain = new ConsoleOutputRelay(session.Output, console).CopyAsync(CancellationToken.None);

        await WaitForFactLinesAsync(facts, 1, () => Encoding.UTF8.GetString(console.ToArray()));
        string[] commands =
        [
            "cmd /d /c exit 5",
            "Write-Output \"SUCCESS\"",
            "Set-Location .",
            "Get-Item \"Z:\\TT_DEFINITELY_MISSING_002\"",
            "1 + 1 | Out-Null",
            "function Invoke-TtSuccess { Write-Output \"ok\" }; Invoke-TtSuccess | Out-Null",
            "cmd /d /c exit 0",
            "cmd /d /c exit 5",
            "cmd /d /c exit 5",
            "cmd /d /c \"exit 5\"; Write-Output \"after\"",
            "Write-Output \"before\"; cmd /d /c \"exit 5\"",
        ];
        for (int index = 0; index < commands.Length; index++)
        {
            await SendAsync(session, commands[index]);
            await WaitForFactLinesAsync(facts, index + 2, () => Encoding.UTF8.GetString(console.ToArray()));
        }
        await SendAsync(session, "exit");
        Assert.AreEqual(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        string[] actual = (await File.ReadAllLinesAsync(facts)).Select(line => line.TrimStart('\uFEFF')).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "<none>\tTrue\t<null>",
                "cmd /d /c exit 5\tFalse\t5",
                "Write-Output \"SUCCESS\"\tTrue\t<null>",
                "Set-Location .\tTrue\t<null>",
                "Get-Item \"Z:\\TT_DEFINITELY_MISSING_002\"\tFalse\t<null>",
                "1 + 1 | Out-Null\tTrue\t<null>",
                "function Invoke-TtSuccess { Write-Output \"ok\" }; Invoke-TtSuccess | Out-Null\tTrue\t<null>",
                "cmd /d /c exit 0\tTrue\t0",
                "cmd /d /c exit 5\tFalse\t5",
                "cmd /d /c exit 5\tFalse\t5",
                "cmd /d /c \"exit 5\"; Write-Output \"after\"\tTrue\t<null>",
                "Write-Output \"before\"; cmd /d /c \"exit 5\"\tFalse\t<null>",
            },
            actual,
            string.Join(" | ", actual));
    }

    [TestMethod]
    public async Task ManagedLoader_NativeOutcomeMatrixStopsOnlyAfterStdoutAndStderrAreCaptured()
    {
        await using InstalledPowerShellLoader installedLoader = await InstalledPowerShellLoader.CreateAsync();
        string facts = Path.Combine(installedLoader.DirectoryPath, "native-output-boundaries.tsv");
        string startup = Path.Combine(installedLoader.DirectoryPath, "native-output-profile.ps1");
        string stagingDirectory = installedLoader.DirectoryPath.Replace("'", "''", StringComparison.Ordinal);
        string escapedFacts = facts.Replace("'", "''", StringComparison.Ordinal);
        string escapedLoader = installedLoader.LoaderPath.Replace("'", "''", StringComparison.Ordinal);
        string script = $@"
$global:TtStage = 1
function global:tt {{
  param([Parameter(ValueFromRemainingArguments=$true)][object[]]$Remaining)
  if ($Remaining -contains 'initialize') {{
    'session=77777777777777777777777777777777'
    'nonce=' + ('F' * 64)
    'staging={stagingDirectory}\native-output-staging-1.txt'
    return
  }}
  if ($Remaining -contains 'boundary') {{
    $staging = ($Remaining | Where-Object {{ $_ -like 'staging=*' }} | Select-Object -Last 1).Substring(8)
    $commandArgument = $Remaining | Where-Object {{ $_ -like 'command-base64=*' }} | Select-Object -Last 1
    $command = if ($commandArgument) {{ [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($commandArgument.Substring(15))) }} else {{ '<none>' }}
    $succeeded = ($Remaining | Where-Object {{ $_ -like 'succeeded=*' }} | Select-Object -Last 1).Substring(10)
    $nativeArgument = $Remaining | Where-Object {{ $_ -like 'native-exit-code=*' }} | Select-Object -Last 1
    $native = if ($nativeArgument) {{ $nativeArgument.Substring(17) }} else {{ '<null>' }}
    $drained = ($Remaining | Where-Object {{ $_ -like 'transcript-drained=*' }} | Select-Object -Last 1).Substring(19)
    Add-Content -LiteralPath '{escapedFacts}' -Encoding UTF8 -Value (""$staging`t$command`t$succeeded`t$native`t$drained"")
    $global:TtStage++
    'staging={stagingDirectory}\native-output-staging-' + $global:TtStage + '.txt'
    return
  }}
}}
function global:prompt {{ 'NATIVE> ' }}
. '{escapedLoader}'
";
        await File.WriteAllTextAsync(startup, script);
        string arguments = $"-NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File \"{startup}\"";
        await using ConPtySession session = ConPtySession.Start(
            "powershell.exe", arguments, Environment.CurrentDirectory, new Coord(220, 500));
        await using MemoryStream console = new();
        Task drain = new ConsoleOutputRelay(session.Output, console).CopyAsync(CancellationToken.None);

        await WaitForFactLinesAsync(facts, 1, () => Encoding.UTF8.GetString(console.ToArray()));
        await WaitForConsoleOccurrencesAsync(console, "NATIVE>", 1);
        const int FailedNativeStressCount = 3;
        List<(string Command, string Marker, bool Succeeded, int ExitCode)> cases =
        [
            ("cmd /d /c \"echo The native command completed successfully. & exit 0\"", "The native command completed successfully.", true, 0),
            ("cmd /d /c \"echo The native command completed implicitly.\"", "The native command completed implicitly.", true, 0),
            ("cmd /d /c \"echo The native command reported stderr. 1>&2 & exit 7\"", "The native command reported stderr.", false, 7),
            ($"cmd /d /c \"echo LONG_NATIVE_OUTPUT_{new string('L', 512)} & exit 0\"", "LONG_NATIVE_OUTPUT_", true, 0),
        ];
        cases.AddRange(Enumerable.Repeat(
            ("cmd /d /c \"echo The native command reported failure. & exit 5\"", "The native command reported failure.", false, 5),
            FailedNativeStressCount));
        for (int index = 0; index < cases.Count; index++)
        {
            await SendAsync(session, cases[index].Command);
            await WaitForFactLinesAsync(facts, index + 2, () => Encoding.UTF8.GetString(console.ToArray()));
            await WaitForConsoleOccurrencesAsync(console, "NATIVE>", index + 2);
        }

        string[] boundaryLines = (await File.ReadAllLinesAsync(facts))
            .Select(line => line.TrimStart('\uFEFF'))
            .ToArray();
        Assert.HasCount(cases.Count + 1, boundaryLines);
        for (int index = 0; index < cases.Count; index++)
        {
            string[] parts = boundaryLines[index + 1].Split('\t');
            Assert.AreEqual(cases[index].Command, parts[1]);
            Assert.AreEqual(cases[index].Succeeded.ToString(), parts[2]);
            Assert.AreEqual(cases[index].ExitCode.ToString(), parts[3]);
            Assert.AreEqual(bool.TrueString, parts[4], $"Case {index + 1} did not establish transcript producer completion.");
            string transcript = await File.ReadAllTextAsync(parts[0]);
            Assert.IsGreaterThanOrEqualTo(
                2,
                CountOccurrences(transcript, cases[index].Marker),
                $"Case {index + 1} contains the marker only in the submitted command echo: {transcript}");
            PreviousCommandSnapshot snapshot = await FinalizeNativeTranscriptAsync(
                installedLoader.DirectoryPath,
                index,
                cases[index].Command,
                transcript,
                cases[index].Succeeded,
                cases[index].ExitCode);
            StringAssert.Contains(snapshot.Output, cases[index].Marker, $"Case {index + 1}: {transcript}");
            Assert.AreEqual(cases[index].Succeeded, snapshot.PowerShellSucceeded);
            Assert.AreEqual(cases[index].ExitCode, snapshot.NativeExitCode);
        }

        await SendAsync(session, "exit");
        Assert.AreEqual(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ManagedLoader_CodexReceivesRealConsoleHandlesWhileCaptureIsActive()
    {
        await using InstalledPowerShellLoader installedLoader = await InstalledPowerShellLoader.CreateAsync();
        string configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        string probe = Path.Combine(
            FindRepositoryRoot(),
            "tests", "TerminalTranslator.Windows.Tests", "Fixtures", "InteractiveProbe",
            "bin", configuration, "net10.0", "tt-interactive-probe.dll");
        Assert.IsTrue(File.Exists(probe), $"TTY probe executable is unavailable: {probe}");

        string startup = Path.Combine(installedLoader.DirectoryPath, "tty-profile.ps1");
        string escapedDirectory = installedLoader.DirectoryPath.Replace("'", "''", StringComparison.Ordinal);
        string escapedLoader = installedLoader.LoaderPath.Replace("'", "''", StringComparison.Ordinal);
        string escapedProbe = probe.Replace("'", "''", StringComparison.Ordinal);
        string script = $@"
$global:TtStage = 1
function global:tt {{
  param([Parameter(ValueFromRemainingArguments=$true)][object[]]$Remaining)
  if ($Remaining -contains 'initialize') {{
    'session=99999999999999999999999999999999'
    'nonce=' + ('9' * 64)
    'staging={escapedDirectory}\tty-staging-1.txt'
    return
  }}
  if ($Remaining -contains 'boundary') {{
    $global:TtStage++
    'staging={escapedDirectory}\tty-staging-' + $global:TtStage + '.txt'
    return
  }}
}}
function global:codex {{ & dotnet '{escapedProbe}' @args }}
function global:prompt {{ 'TTY> ' }}
. '{escapedLoader}'
";
        await File.WriteAllTextAsync(startup, script);

        string arguments = $"-NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File \"{startup}\"";
        await using ConPtySession session = ConPtySession.Start(
            "powershell.exe", arguments, Environment.CurrentDirectory, new Coord(180, 40));
        await using MemoryStream console = new();
        Task drain = new ConsoleOutputRelay(session.Output, console).CopyAsync(CancellationToken.None);

        await WaitForConsoleOccurrencesAsync(console, "TTY>", 1);
        await SendAsync(session, "codex tty");
        await WaitForConsoleOccurrencesAsync(console, "\"stderrTerminal\":true", 1);
        string output = Encoding.UTF8.GetString(console.ToArray());
        StringAssert.Contains(output, "\"stdinTerminal\":true");
        StringAssert.Contains(output, "\"stdoutTerminal\":true");
        StringAssert.Contains(output, "\"stderrTerminal\":true");
        await WaitForConsoleOccurrencesAsync(console, "TTY>", 2);

        const string nativeMarker = "T134_NATIVE_CAPTURE_SAFE";
        await SendAsync(session, $"cmd /d /c \"echo {nativeMarker} & exit 5\"");
        await WaitForConsoleOccurrencesAsync(console, nativeMarker, 1);
        await WaitForConsoleOccurrencesAsync(console, "TTY>", 3);
        string nativeTranscript = await File.ReadAllTextAsync(
            Path.Combine(installedLoader.DirectoryPath, "tty-staging-3.txt"));
        Assert.IsGreaterThanOrEqualTo(2, CountOccurrences(nativeTranscript, nativeMarker), nativeTranscript);
        Assert.IsFalse(
            Encoding.UTF8.GetString(console.ToArray()).Contains("Capture unavailable", StringComparison.Ordinal),
            Encoding.UTF8.GetString(console.ToArray()));

        await SendAsync(session, "exit");
        Assert.AreEqual(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
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

    [TestMethod]
    public async Task ManagedLoader_InteractiveFirstPromptAndReentrantPromptUseOpeningHistoryBaselineWithoutFlapping()
    {
        await using InstalledPowerShellLoader installedLoader = await InstalledPowerShellLoader.CreateAsync();
        string facts = Path.Combine(installedLoader.DirectoryPath, "managed-boundaries.tsv");
        string startup = Path.Combine(installedLoader.DirectoryPath, "isolated-profile.ps1");
        string stagingDirectory = installedLoader.DirectoryPath.Replace("'", "''", StringComparison.Ordinal);
        string escapedFacts = facts.Replace("'", "''", StringComparison.Ordinal);
        string escapedLoader = installedLoader.LoaderPath.Replace("'", "''", StringComparison.Ordinal);
        string script = $@"
$global:TtStage = 1
function global:tt {{
  param([Parameter(ValueFromRemainingArguments=$true)][object[]]$Remaining)
  if ($Remaining -contains 'initialize') {{
    'session=55555555555555555555555555555555'
    'nonce=' + ('D' * 64)
    'staging={stagingDirectory}\managed-staging-1.txt'
    return
  }}
  if ($Remaining -contains 'boundary') {{
    $opening = [long](($Remaining | Where-Object {{ $_ -like 'opening-history-id=*' }} | Select-Object -Last 1).Substring(19))
    $closing = [long](($Remaining | Where-Object {{ $_ -like 'history-id=*' }} | Select-Object -Last 1).Substring(11))
    $initial = [bool]::Parse(($Remaining | Where-Object {{ $_ -like 'initial-prompt=*' }} | Select-Object -Last 1).Substring(15))
    $hasCommand = [bool]($Remaining | Where-Object {{ $_ -like 'command-base64=*' }})
    Add-Content -LiteralPath '{escapedFacts}' -Encoding UTF8 -Value (""$opening`t$closing`t$initial`t$hasCommand"")
    $valid = (($initial -and -not $hasCommand) -or (-not $initial -and $closing -eq $opening -and -not $hasCommand) -or (-not $initial -and $closing -eq ($opening + 1) -and $hasCommand))
    if (-not $valid) {{ 'unavailable=boundary'; 'boundary-detail=historyidentitymismatch'; return }}
    $global:TtStage++
    'staging={stagingDirectory}\managed-staging-' + $global:TtStage + '.txt'
    return
  }}
  if ($Remaining -contains 'recover') {{ 'staging={stagingDirectory}\managed-recovered.txt' }}
}}
function global:prompt {{ 'CUSTOM> ' }}
";
        await File.WriteAllTextAsync(startup, script);
        string arguments = $"-NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File \"{startup}\"";
        await using ConPtySession session = ConPtySession.Start("powershell.exe", arguments, Environment.CurrentDirectory);
        await using MemoryStream console = new();
        Task drain = new ConsoleOutputRelay(session.Output, console).CopyAsync(CancellationToken.None);

        await WaitForConsoleOccurrencesAsync(console, "CUSTOM>", 1);
        await SendAsync(session, "Write-Output \"PREEXISTING\"");
        await WaitForConsoleOccurrencesAsync(console, "CUSTOM>", 2);
        await SendAsync(session, $". '{escapedLoader}'");
        await WaitForFactLinesAsync(facts, 1);
        await SendAsync(session, "Write-Output \"FIRST\"");
        await WaitForFactLinesAsync(facts, 2);
        await SendAsync(session, "prompt");
        await WaitForFactLinesAsync(facts, 4);
        await SendAsync(session, "Set-Location ..");
        await WaitForFactLinesAsync(facts, 5);
        await SendAsync(session, "Write-Output \"SECOND\"");
        await WaitForFactLinesAsync(facts, 6);
        await SendAsync(session, "exit");
        Assert.AreEqual(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(
            new[] { "1\t2\tTrue\tFalse", "2\t3\tFalse\tTrue", "3\t3\tFalse\tFalse", "3\t4\tFalse\tTrue", "4\t5\tFalse\tTrue", "5\t6\tFalse\tTrue" },
            (await File.ReadAllLinesAsync(facts)).Select(line => line.TrimStart('\uFEFF')).ToArray(),
            string.Join(" | ", await File.ReadAllLinesAsync(facts)));
        string allConsole = Encoding.UTF8.GetString(console.ToArray());
        Assert.IsFalse(allConsole.Contains("Capture unavailable", StringComparison.Ordinal), allConsole);
        Assert.IsFalse(allConsole.Contains("Capture restored", StringComparison.Ordinal), allConsole);
    }

    private static async Task<string> ReadLoaderAsync()
    {
        string loader = Path.Combine(AppContext.BaseDirectory, "TerminalTranslator.Profile.ps1");
        return await File.ReadAllTextAsync(loader);
    }

    private static async Task SendAsync(ConPtySession session, string command)
    {
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes(command + "\r"));
        await session.Input.FlushAsync();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TerminalTranslator.sln")))
            current = current.Parent;
        return current?.FullName ?? Environment.CurrentDirectory;
    }

    private static async Task SendCtrlCAsync(ConPtySession session)
    {
        await session.Input.WriteAsync(new byte[] { 3 });
        await session.Input.FlushAsync();
    }

    private static async Task WaitForFactLinesAsync(string path, int expected, Func<string>? diagnostic = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (File.Exists(path) && File.ReadAllLines(path).Length >= expected)
                    return;
            }
            catch (IOException)
            {
            }

            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(20))
                Assert.Fail($"Timed out waiting for managed boundary fact {expected}. {diagnostic?.Invoke()}");
            await Task.Delay(20);
        }
    }

    private static async Task WaitForConsoleOccurrencesAsync(MemoryStream console, string value, int expected)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (Encoding.UTF8.GetString(console.ToArray()).Split(value, StringSplitOptions.None).Length - 1 < expected)
        {
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(20))
                Assert.Fail($"Timed out waiting for console marker {expected}. Output: {Encoding.UTF8.GetString(console.ToArray())}");
            await Task.Delay(20);
        }
    }

    private static int CountOccurrences(string value, string needle)
    {
        int count = 0;
        int start = 0;
        while ((start = value.IndexOf(needle, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += needle.Length;
        }

        return count;
    }

    private static async Task<PreviousCommandSnapshot> FinalizeNativeTranscriptAsync(
        string root,
        int caseIndex,
        string command,
        string transcript,
        bool succeeded,
        int nativeExitCode)
    {
        string captureRoot = Path.Combine(root, $"capture-{caseIndex}");
        CaptureOwnerIdentity owner = new("S-1-5-21-native-matrix", 8234 + caseIndex, 638921000000000000 + caseIndex);
        CaptureBootstrapResult bootstrap = await new CaptureSessionBootstrap(captureRoot).InitializeAsync(
            CapturePreference.Enabled,
            owner,
            CaptureSessionBootstrap.CurrentIntegrationVersion,
            isHostedFeature001Session: false);
        await File.WriteAllTextAsync(bootstrap.StagingPath!, transcript);
        CaptureBoundaryProcessingResult result = await new CaptureBoundaryProcessor(captureRoot).ProcessAsync(
            new CaptureBoundaryRequest(
                bootstrap.Proof!.SessionId,
                bootstrap.Proof.Nonce,
                owner,
                CaptureSessionBootstrap.CurrentIntegrationVersion,
                bootstrap.StagingPath!,
                HistoryId: 1,
                CommandText: command,
                Succeeded: succeeded,
                NativeExitCode: nativeExitCode),
            CapturePreference.Enabled);
        Assert.AreEqual(CaptureBoundaryStatus.ReadyForNextInterval, result.Status, result.BoundaryFailureDetail.ToString());

        RetainedCaptureStore store = new(bootstrap.SessionDirectory!);
        RetainedGeneration generation = (await store.LoadCommittedAsync())!;
        RetainedRecordDescriptor descriptor = generation.Records.Single();
        CapturedCommand captured = RetainedCommandRecordCodec.Deserialize(
            await store.ReadContentAsync(descriptor),
            await store.ReadMetadataAsync(descriptor));
        PreviousCommandResult retrieved = PreviousCommandRetriever.Retrieve(new PreviousCommandRetrievalState(
            CapturePreference.Enabled,
            CaptureHealthState.Healthy,
            new CaptureSessionId(bootstrap.Proof.SessionId, bootstrap.Proof.Nonce),
            [captured],
            IsStoreReliable: true));
        Assert.AreEqual(PreviousCommandResultKind.Success, retrieved.Kind);
        return retrieved.Snapshot!;
    }
}
