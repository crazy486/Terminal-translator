using System.Diagnostics;
using System.Text;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Core.Parsing;
using TerminalTranslator.Windows.Capture;
using TerminalTranslator.Windows.ConPty;
using TerminalTranslator.Windows.Console;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class TranscriptEnvelopeExtractionTests
{
    private const string Separator = "**********************";
    private static readonly CaptureOwnerIdentity Owner = new("S-1-5-21-envelope-test", 8123, 638920000000000000);

    [TestMethod]
    public async Task ProductionFinalization_UsesSessionMarkerForFullPathContextualInvocation()
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult bootstrap = await new CaptureSessionBootstrap(temporary.Path).InitializeAsync(
            CapturePreference.Enabled, Owner, CaptureSessionBootstrap.CurrentIntegrationVersion, false);
        Dictionary<string, string> environment = new(bootstrap.Environment);
        Assert.IsTrue(await ContextualInvocationMarker.TryMarkCurrentAsync(
            temporary.Path,
            name => environment.GetValueOrDefault(name),
            () => (true, Owner)));

        const string command = "& 'C:\\tools\\tt.exe' last";
        CaptureBoundaryProcessingResult result = await ProcessIntervalAsync(
            temporary.Path, bootstrap, 0, 1, command,
            Transcript(command, "[tt] Assistance provider request failed.",
                "Windows PowerShell transcript start", "Start time: 1",
                "Windows PowerShell transcript end", "End time: 2"));
        Assert.AreEqual(CaptureBoundaryStatus.ReadyForNextInterval, result.Status);

        RetainedCaptureStore store = new(bootstrap.SessionDirectory!);
        RetainedGeneration generation = (await store.LoadCommittedAsync())!;
        RetainedRecordDescriptor descriptor = generation.Records.Single();
        CapturedCommand captured = RetainedCommandRecordCodec.Deserialize(
            await store.ReadContentAsync(descriptor), await store.ReadMetadataAsync(descriptor));
        Assert.IsTrue(captured.IsContextualAssistanceCommand);
        Assert.IsFalse(ContextualInvocationMarker.IsMarked(bootstrap.SessionDirectory!, 1));
    }

    [TestMethod]
    [DataRow("Windows PowerShell transcript start", "Start time: 20260829175900", "Windows PowerShell transcript end", "End time: 20260829175932")]
    [DataRow("Windows PowerShell 脚本开始", "开始时间: 20260829175900", "Windows PowerShell 脚本结束", "结束时间: 20260829175932")]
    public async Task ProductionFinalization_RemovesLocalizedTranscriptEnvelopeBeforeRetention(
        string startLabel,
        string startTime,
        string endLabel,
        string endTime)
    {
        const string command = "Write-Output \"TT_REAL_OUTPUT_LINE\"";
        string transcript = Transcript(command, "TT_REAL_OUTPUT_LINE", startLabel, startTime, endLabel, endTime);

        (CapturedCommand captured, PreviousCommandResult retrieved) = await FinalizeAndRetrieveAsync(command, transcript);

        Assert.AreEqual("TT_REAL_OUTPUT_LINE", captured.Output);
        Assert.AreEqual(PreviousCommandResultKind.Success, retrieved.Kind);
        Assert.AreEqual("TT_REAL_OUTPUT_LINE", retrieved.Snapshot!.Output);
        Assert.IsFalse(captured.Output.Contains(Separator, StringComparison.Ordinal));
        Assert.IsFalse(captured.Output.Contains("20260829175932", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProductionFinalization_ZeroOutputPersistsEmptyAndRetrievesCanonicalNoOutput()
    {
        const string command = "Set-Location ..";
        string transcript = Transcript(
            command,
            string.Empty,
            "Windows PowerShell 脚本开始",
            "开始时间: 20260829175900",
            "Windows PowerShell 脚本结束",
            "结束时间: 20260829175932");

        (CapturedCommand captured, PreviousCommandResult retrieved) = await FinalizeAndRetrieveAsync(command, transcript);

        Assert.AreEqual(0, captured.Output.Length);
        Assert.AreEqual(PreviousCommandResultKind.NoOutput, retrieved.Kind);
    }

    [TestMethod]
    public async Task ProductionFinalization_NativeFileRedirectionRemovesOnlyPowerShellOutFilePlumbing()
    {
        const string command = "cmd /d /c \"echo redirected & exit 5\" > 'result.txt'";
        const string frameworkTrace = ">> ParameterBinding(Out-File): 名称 =“InputObject”; 值 =“redirected ”";
        (CapturedCommand captured, PreviousCommandResult retrieved) = await FinalizeAndRetrieveAsync(
            command,
            Transcript(command, frameworkTrace, "start", "time", "end", "time"),
            succeeded: false,
            nativeExitCode: 5,
            hasNativeFileRedirection: true);

        Assert.AreEqual(string.Empty, captured.Output);
        Assert.AreEqual(PreviousCommandResultKind.NoOutput, retrieved.Kind);
        Assert.AreEqual(5, captured.Boundary.NativeExitCode);
    }

    [TestMethod]
    public async Task ProductionFinalization_UserLookalikeOutputWithoutNativeRedirectionIsPreserved()
    {
        const string command = "Write-Output $value";
        const string lookalike = "PS>ParameterBinding(Out-File): name=\"InputObject\"; value=\"user text\"";
        (CapturedCommand captured, _) = await FinalizeAndRetrieveAsync(
            command,
            Transcript(command, lookalike, "start", "time", "end", "time"));

        Assert.AreEqual(lookalike, captured.Output);
    }

    [TestMethod]
    public async Task ProductionFinalization_PersistsReliableInterruptionThroughStrictPreviousRetrieval()
    {
        const string command = "Start-Sleep -Seconds 30";
        const string partialOutput = "Partial output before Ctrl+C.";
        (CapturedCommand captured, PreviousCommandResult retrieved) = await FinalizeAndRetrieveAsync(
            command,
            Transcript(command, partialOutput, "Windows PowerShell transcript start", "Start time: 1", "Windows PowerShell transcript end", "End time: 2"),
            succeeded: false,
            wasInterrupted: true);

        Assert.IsTrue(captured.Boundary.WasInterrupted);
        Assert.IsFalse(captured.Boundary.PowerShellSucceeded);
        Assert.AreEqual(PreviousCommandResultKind.Success, retrieved.Kind);
        Assert.IsTrue(retrieved.Snapshot!.WasInterrupted);
        Assert.AreEqual(partialOutput, retrieved.Snapshot.Output);
    }

    [TestMethod]
    public async Task ProductionFinalization_PreservesUserOutputThatLooksExactlyLikeLocalizedFooter()
    {
        const string command = "Write-Output $lookalike";
        string lookalike = string.Join("\r\n", Separator, "Windows PowerShell 脚本结束", "结束时间: 123", Separator);
        string transcript = Transcript(
            command,
            lookalike,
            "Windows PowerShell 脚本开始",
            "开始时间: 20260829175900",
            "Windows PowerShell 脚本结束",
            "结束时间: 20260829175932");

        (CapturedCommand captured, PreviousCommandResult retrieved) = await FinalizeAndRetrieveAsync(command, transcript);

        Assert.AreEqual(lookalike, captured.Output);
        Assert.AreEqual(lookalike, retrieved.Snapshot!.Output);
    }

    [TestMethod]
    public async Task ProductionFinalization_ErrorRecordCommandReproductionRemainsOutputWithoutAmbiguousEcho()
    {
        const string command = "Get-Item \"Z:\\TT_DEFINITELY_MISSING_002\"";
        string error = string.Join("\r\n",
            "Get-Item : Cannot find drive. A drive with the name 'Z' does not exist.",
            "At line:1 char:1",
            $"+ {command}",
            "+ ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~",
            "    + CategoryInfo          : ObjectNotFound: (Z:String) [Get-Item], DriveNotFoundException",
            "    + FullyQualifiedErrorId : DriveNotFound,Microsoft.PowerShell.Commands.GetItemCommand");

        (CapturedCommand captured, PreviousCommandResult retrieved) = await FinalizeAndRetrieveAsync(
            command,
            Transcript(command, error, "Windows PowerShell transcript start", "Start time: 1", "Windows PowerShell transcript end", "End time: 2"));

        Assert.AreEqual(error, captured.Output);
        Assert.AreEqual(PreviousCommandResultKind.Success, retrieved.Kind);
        StringAssert.Contains(captured.Output, $"+ {command}");
        Assert.AreEqual(1, Count(captured.Output, command));
        Assert.IsFalse(captured.Output.StartsWith(command, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProductionPath_NativeMixedStreamsRoundTripAndRemainSelectable()
    {
        const string command = "native-stream-probe";
        const string output = "The native command completed successfully.\r\nThe native command reported a recoverable warning.";
        (CapturedCommand captured, PreviousCommandResult retrieved) = await FinalizeAndRetrieveAsync(
            command,
            Transcript(command, output, "Windows PowerShell transcript start", "Start time: 1", "Windows PowerShell transcript end", "End time: 2"));

        Assert.AreEqual(output, captured.Output);
        Assert.AreEqual(output, retrieved.Snapshot!.Output);
        Assert.IsTrue(WholeOutputEligibility.HasTranslatableEnglish(retrieved.Snapshot.Output));
        AssistanceRequest request = AssistanceRequest.CreateLastTranslation(
            retrieved.Snapshot.CommandText,
            retrieved.Snapshot.Output,
            new TerminationFacts(retrieved.Snapshot.ExitCode, retrieved.Snapshot.WasInterrupted),
            retrieved.Snapshot.LocalCompleteness,
            retrieved.Snapshot.OriginalOutputBytes);
        AiSelectionResult selection = AiRequestSelector.Select(request, AiInputBudgetPolicy.V1Default);
        Assert.IsTrue(selection.Supported);
        Assert.AreEqual(output, selection.Request!.SelectedOutput);
    }

    [TestMethod]
    public async Task T135SmokeOutputs_RoundTripIntoTheExactProviderSelection()
    {
        const string stdoutCommand = "cmd /d /c \"echo T134 final smoke. & exit 5\"";
        const string stdoutOutput = "T134 final smoke.";
        (_, PreviousCommandResult stdoutRetrieved) = await FinalizeAndRetrieveAsync(
            stdoutCommand,
            Transcript(stdoutCommand, stdoutOutput, "start", "time", "end", "time"),
            succeeded: false,
            nativeExitCode: 5);
        AssistanceRequest stdoutRequest = AssistanceRequest.CreateLastTranslation(
            stdoutRetrieved.Snapshot!.CommandText,
            stdoutRetrieved.Snapshot.Output,
            new TerminationFacts(
                stdoutRetrieved.Snapshot.PowerShellSucceeded,
                stdoutRetrieved.Snapshot.NativeExitCode,
                stdoutRetrieved.Snapshot.WasInterrupted));
        AiSelectionResult stdoutSelection = AiRequestSelector.Select(stdoutRequest, AiInputBudgetPolicy.V1Default);
        Assert.IsTrue(stdoutSelection.Supported);
        Assert.AreEqual(stdoutOutput, stdoutSelection.Request!.SelectedOutput);

        const string stderrCommand = "cmd /d /c \"echo stderr smoke 1>&2 & exit 7\"";
        string stderrOutput = string.Join("\r\n",
            "cmd : stderr smoke",
            "所在位置 行:1 字符: 1",
            $"+ {stderrCommand}",
            "+ ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~",
            "    + CategoryInfo          : NotSpecified: (stderr smoke  :String) [], RemoteException",
            "    + FullyQualifiedErrorId : NativeCommandError");
        (_, PreviousCommandResult stderrRetrieved) = await FinalizeAndRetrieveAsync(
            stderrCommand,
            Transcript(stderrCommand, stderrOutput, "start", "time", "end", "time"),
            succeeded: false,
            nativeExitCode: 7);
        AssistanceRequest stderrRequest = AssistanceRequest.CreateLastTranslation(
            stderrRetrieved.Snapshot!.CommandText,
            stderrRetrieved.Snapshot.Output,
            new TerminationFacts(
                stderrRetrieved.Snapshot.PowerShellSucceeded,
                stderrRetrieved.Snapshot.NativeExitCode,
                stderrRetrieved.Snapshot.WasInterrupted));
        AiSelectionResult stderrSelection = AiRequestSelector.Select(stderrRequest, AiInputBudgetPolicy.V1Default);
        Assert.IsTrue(stderrSelection.Supported);
        Assert.AreEqual(stderrOutput, stderrSelection.Request!.SelectedOutput);
        StringAssert.Contains(stderrSelection.Request.SelectedOutput, "NativeCommandError");
    }

    [TestMethod]
    public void GitLikeOutput_IsMeaningfulEnglishWithoutGitSpecificSelectionRules()
    {
        const string output = "On branch test\r\nChanges not staged for commit:\r\n  (use \"git add <file>...\" to update what will be committed)";
        Assert.IsTrue(WholeOutputEligibility.HasTranslatableEnglish(output));
    }

    [TestMethod]
    public async Task PowerShell51RealTranscript_ProductionFinalizationReturnsOnlyCommandOutput()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows PowerShell 5.1 is Windows-only.");

        const string command = "$global:TtStop = $true; Write-Output \"TT_REAL_OUTPUT_LINE\"";
        using TemporaryDirectory temporary = new();
        string transcriptPath = Path.Combine(temporary.Path, "real-ps51-transcript.txt");
        ProcessStartInfo start = new("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        using Process process = Process.Start(start)!;
        await process.StandardInput.WriteLineAsync("function global:prompt { if ($global:TtStop) { $null = Stop-Transcript; $global:TtStop = $false }; 'CUSTOM> ' }");
        await process.StandardInput.WriteLineAsync($"$null = Start-Transcript -LiteralPath '{transcriptPath.Replace("'", "''", StringComparison.Ordinal)}' -Force");
        await process.StandardInput.WriteLineAsync(command);
        await process.StandardInput.WriteLineAsync("exit");
        process.StandardInput.Close();
        string standardOutput = await process.StandardOutput.ReadToEndAsync();
        string standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.AreEqual(0, process.ExitCode, standardError);
        Assert.IsTrue(File.Exists(transcriptPath), standardOutput);
        string transcript = await File.ReadAllTextAsync(transcriptPath);
        Assert.IsTrue(transcript.Split(Separator, StringSplitOptions.None).Length >= 5);
        (CapturedCommand captured, PreviousCommandResult retrieved) = await FinalizeAndRetrieveAsync(command, transcript);

        Assert.AreEqual("TT_REAL_OUTPUT_LINE", captured.Output);
        Assert.AreEqual("TT_REAL_OUTPUT_LINE", retrieved.Snapshot!.Output);
    }

    [TestMethod]
    public async Task PowerShell51ConPty_FirstPromptAndThreeCommandsExposeCanonicalHistorySequence()
    {
        using TemporaryDirectory temporary = new();
        string script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PowerShellCaptureProbe", "Invoke-PromptBoundaryProbe.ps1");
        string arguments = $"-NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File \"{script}\" -OutputDirectory \"{temporary.Path}\"";
        await using ConPtySession session = ConPtySession.Start("powershell.exe", arguments, Environment.CurrentDirectory);
        await using MemoryStream console = new();
        Task drain = new ConsoleOutputRelay(session.Output, console).CopyAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => Encoding.UTF8.GetString(console.ToArray()).Contains("CUSTOM>", StringComparison.Ordinal),
            TimeSpan.FromSeconds(8),
            () => Encoding.UTF8.GetString(console.ToArray()));
        string factsPath = Path.Combine(temporary.Path, "boundary-facts.tsv");
        await SendCommandAndWaitForFactsAsync(session, "Write-Output \"FIRST\"", factsPath, 2);
        await SendCommandAndWaitForFactsAsync(session, "Set-Location ..", factsPath, 3);
        await SendCommandAndWaitForFactsAsync(session, "Write-Output \"SECOND\"", factsPath, 4);
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("exit\r"));
        await session.Input.FlushAsync();
        Assert.AreEqual(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        string[][] facts = (await File.ReadAllLinesAsync(factsPath))
            .Select(line => line.TrimStart('\uFEFF').Split('\t'))
            .ToArray();
        Assert.HasCount(4, facts);
        CollectionAssert.AreEqual(new[] { "1", "1", "0", string.Empty }, facts[0]);
        CollectionAssert.AreEqual(new[] { "2", "1", "1", Convert.ToBase64String(Encoding.UTF8.GetBytes("Write-Output \"FIRST\"")) }, facts[1]);
        CollectionAssert.AreEqual(
            new[] { "3", "2", "2", Convert.ToBase64String(Encoding.UTF8.GetBytes("Set-Location ..")) },
            facts[2],
            string.Join(" | ", facts.Select(parts => string.Join(",", parts))));
        CollectionAssert.AreEqual(new[] { "4", "3", "3", Convert.ToBase64String(Encoding.UTF8.GetBytes("Write-Output \"SECOND\"")) }, facts[3]);
    }

    [TestMethod]
    public async Task PowerShell51ConPty_ErrorRecordAdvancesHistoryOnceRetainsDiagnosticsAndNextCommandIsHealthy()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows PowerShell 5.1 is Windows-only.");

        const string errorCommand = "Get-Item \"Z:\\TT_DEFINITELY_MISSING_002\"";
        const string nextCommand = "Write-Output \"The next command remains healthy.\"";
        using TemporaryDirectory temporary = new();
        string script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PowerShellCaptureProbe", "Invoke-PromptBoundaryProbe.ps1");
        string arguments = $"-NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File \"{script}\" -OutputDirectory \"{temporary.Path}\"";
        await using ConPtySession session = ConPtySession.Start(
            "powershell.exe", arguments, Environment.CurrentDirectory, new Coord(220, 50));
        await using MemoryStream console = new();
        Task drain = new ConsoleOutputRelay(session.Output, console).CopyAsync(CancellationToken.None);
        string factsPath = Path.Combine(temporary.Path, "boundary-facts.tsv");
        await WaitUntilAsync(() => File.Exists(factsPath) && File.ReadAllLines(factsPath).Length >= 1, TimeSpan.FromSeconds(8));
        await SendCommandAndWaitForFactsAsync(session, errorCommand, factsPath, 2);
        await SendCommandAndWaitForFactsAsync(session, nextCommand, factsPath, 3);
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("exit\r"));
        await session.Input.FlushAsync();
        Assert.AreEqual(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        string[][] facts = (await File.ReadAllLinesAsync(factsPath))
            .Select(line => line.TrimStart('\uFEFF').Split('\t'))
            .ToArray();
        CollectionAssert.AreEqual(new[] { "0", "1", "2" }, facts.Take(3).Select(parts => parts[2]).ToArray());
        Assert.AreEqual(errorCommand, Encoding.UTF8.GetString(Convert.FromBase64String(facts[1][3])));
        Assert.AreEqual(nextCommand, Encoding.UTF8.GetString(Convert.FromBase64String(facts[2][3])));

        CaptureBootstrapResult bootstrap = await new CaptureSessionBootstrap(temporary.Path).InitializeAsync(
            CapturePreference.Enabled, Owner, CaptureSessionBootstrap.CurrentIntegrationVersion, false);
        string errorTranscript = await File.ReadAllTextAsync(Path.Combine(temporary.Path, "staging-00000000000000000001.txt"));
        CaptureBoundaryProcessingResult errorResult = await ProcessIntervalAsync(
            temporary.Path, bootstrap, 0, 1, errorCommand, errorTranscript, succeeded: false);
        Assert.AreEqual(CaptureBoundaryStatus.ReadyForNextInterval, errorResult.Status);
        PreviousCommandResult errorRetrieved = await RetrieveCommittedAsync(bootstrap);
        Assert.AreEqual(PreviousCommandResultKind.Success, errorRetrieved.Kind);
        Assert.IsFalse(errorRetrieved.Snapshot!.PowerShellSucceeded);
        Assert.IsNull(errorRetrieved.Snapshot.NativeExitCode);
        StringAssert.Contains(errorRetrieved.Snapshot.Output, $"+ {errorCommand}");
        StringAssert.Contains(errorRetrieved.Snapshot.Output, "CategoryInfo");
        StringAssert.Contains(errorRetrieved.Snapshot.Output, "DriveNotFoundException");
        StringAssert.Contains(errorRetrieved.Snapshot.Output, "FullyQualifiedErrorId");
        StringAssert.Contains(errorRetrieved.Snapshot.Output, "Microsoft.PowerShell.Commands.GetItemCommand");
        Assert.IsFalse(errorRetrieved.Snapshot.Output.StartsWith(errorCommand, StringComparison.Ordinal));

        string nextTranscript = await File.ReadAllTextAsync(Path.Combine(temporary.Path, "staging-00000000000000000002.txt"));
        CaptureBoundaryProcessingResult nextResult = await ProcessIntervalAsync(
            temporary.Path, bootstrap, 1, 2, nextCommand, nextTranscript);
        Assert.AreEqual(CaptureBoundaryStatus.ReadyForNextInterval, nextResult.Status);
        CaptureOwnerManifest manifest = await CaptureSessionBootstrap.ReadOwnerManifestAsync(bootstrap.SessionDirectory!);
        Assert.AreEqual(CaptureHealthState.Healthy, manifest.HealthState);
        PreviousCommandSnapshot nextSnapshot = (await RetrieveCommittedAsync(bootstrap)).Snapshot!;
        Assert.AreEqual("The next command remains healthy.", nextSnapshot.Output);
        Assert.IsTrue(nextSnapshot.PowerShellSucceeded);
        Assert.IsNull(nextSnapshot.NativeExitCode);
    }

    [TestMethod]
    public async Task PowerShell51ConPty_NativeStdoutAndStderrReachRetentionInTerminalOrder()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows PowerShell 5.1 is Windows-only.");

        const string stdout = "The native command completed successfully.";
        const string stderr = "The native command reported a recoverable warning.";
        const string command = "cmd /d /c \"echo The native command completed successfully. & echo The native command reported a recoverable warning. 1>&2\"";
        using TemporaryDirectory temporary = new();
        string script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PowerShellCaptureProbe", "Invoke-PromptBoundaryProbe.ps1");
        string arguments = $"-NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File \"{script}\" -OutputDirectory \"{temporary.Path}\"";
        await using ConPtySession session = ConPtySession.Start(
            "powershell.exe", arguments, Environment.CurrentDirectory, new Coord(220, 50));
        await using MemoryStream console = new();
        Task drain = new ConsoleOutputRelay(session.Output, console).CopyAsync(CancellationToken.None);
        string factsPath = Path.Combine(temporary.Path, "boundary-facts.tsv");
        await WaitUntilAsync(() => File.Exists(factsPath) && File.ReadAllLines(factsPath).Length >= 1, TimeSpan.FromSeconds(8));
        await SendCommandAndWaitForFactsAsync(session, command, factsPath, 2);
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("exit\r"));
        await session.Input.FlushAsync();
        Assert.AreEqual(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        string transcript = await File.ReadAllTextAsync(Path.Combine(temporary.Path, "staging-00000000000000000001.txt"));
        int stdoutIndex = transcript.IndexOf(stdout, StringComparison.Ordinal);
        int stderrIndex = transcript.IndexOf(stderr, StringComparison.Ordinal);
        Assert.IsTrue(stdoutIndex >= 0, transcript);
        Assert.IsTrue(stderrIndex > stdoutIndex, transcript);

        (CapturedCommand captured, PreviousCommandResult retrieved) = await FinalizeAndRetrieveAsync(command, transcript, nativeExitCode: 0);
        Assert.IsTrue(
            captured.Output.IndexOf(stdout, StringComparison.Ordinal) < captured.Output.IndexOf(stderr, StringComparison.Ordinal),
            captured.Output);
        Assert.AreEqual(captured.Output, retrieved.Snapshot!.Output);
        Assert.IsTrue(retrieved.Snapshot.PowerShellSucceeded);
        Assert.AreEqual(0, retrieved.Snapshot.NativeExitCode);
        Assert.IsTrue(WholeOutputEligibility.HasTranslatableEnglish(retrieved.Snapshot.Output));
        AssistanceRequest request = AssistanceRequest.CreateLastTranslation(
            retrieved.Snapshot.CommandText,
            retrieved.Snapshot.Output,
            new TerminationFacts(retrieved.Snapshot.ExitCode, retrieved.Snapshot.WasInterrupted),
            retrieved.Snapshot.LocalCompleteness,
            retrieved.Snapshot.OriginalOutputBytes);
        Assert.IsTrue(AiRequestSelector.Select(request, AiInputBudgetPolicy.V1Default).Request!.SelectedOutput.Contains(stderr, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PowerShell51ConPty_GitStatusTranscriptRetentionEligibilityAndSelectionRemainNonEmpty()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows PowerShell 5.1 is Windows-only.");

        using TemporaryDirectory temporary = new();
        string repository = Path.Combine(temporary.Path, "repository");
        string transcripts = Path.Combine(temporary.Path, "transcripts");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(transcripts);
        await RunGitAsync(repository, "init", "--initial-branch=test");
        await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "initial\n");
        await RunGitAsync(repository, "add", "tracked.txt");
        await RunGitAsync(repository, "-c", "user.name=TT Test", "-c", "user.email=tt@example.invalid", "commit", "-m", "initial");
        await File.AppendAllTextAsync(Path.Combine(repository, "tracked.txt"), "changed\n");
        await File.WriteAllTextAsync(Path.Combine(repository, "untracked.txt"), "untracked\n");

        const string command = "git -c color.ui=false status";
        string script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PowerShellCaptureProbe", "Invoke-PromptBoundaryProbe.ps1");
        string arguments = $"-NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File \"{script}\" -OutputDirectory \"{transcripts}\"";
        await using ConPtySession session = ConPtySession.Start(
            "powershell.exe", arguments, repository, new Coord(220, 50));
        await using MemoryStream console = new();
        Task drain = new ConsoleOutputRelay(session.Output, console).CopyAsync(CancellationToken.None);
        string factsPath = Path.Combine(transcripts, "boundary-facts.tsv");
        await WaitUntilAsync(() => File.Exists(factsPath) && File.ReadAllLines(factsPath).Length >= 1, TimeSpan.FromSeconds(8));
        await SendCommandAndWaitForFactsAsync(session, command, factsPath, 2);
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("exit\r"));
        await session.Input.FlushAsync();
        Assert.AreEqual(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        string transcript = await File.ReadAllTextAsync(Path.Combine(transcripts, "staging-00000000000000000001.txt"));
        StringAssert.Contains(transcript, "On branch test");
        StringAssert.Contains(transcript, "Changes not staged for commit:");
        StringAssert.Contains(transcript, "Untracked files:");
        (CapturedCommand captured, PreviousCommandResult retrieved) = await FinalizeAndRetrieveAsync(command, transcript, nativeExitCode: 0);
        Assert.IsGreaterThan(0, captured.Output.Length);
        Assert.AreEqual(captured.Output, retrieved.Snapshot!.Output);
        Assert.IsTrue(retrieved.Snapshot.PowerShellSucceeded);
        Assert.AreEqual(0, retrieved.Snapshot.NativeExitCode);
        Assert.IsTrue(WholeOutputEligibility.HasTranslatableEnglish(retrieved.Snapshot.Output));
        AssistanceRequest request = AssistanceRequest.CreateLastTranslation(
            retrieved.Snapshot.CommandText,
            retrieved.Snapshot.Output,
            new TerminationFacts(retrieved.Snapshot.ExitCode, retrieved.Snapshot.WasInterrupted),
            retrieved.Snapshot.LocalCompleteness,
            retrieved.Snapshot.OriginalOutputBytes);
        AiSelectionResult selection = AiRequestSelector.Select(request, AiInputBudgetPolicy.V1Default);
        Assert.IsTrue(selection.Supported);
        StringAssert.Contains(selection.Request!.SelectedOutput, "Changes not staged for commit:");
    }

    [TestMethod]
    public async Task ProductionBoundaryLifecycle_FirstPromptDuplicatePromptAndThreeCommandsNeverFlap()
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult bootstrap = await new CaptureSessionBootstrap(temporary.Path).InitializeAsync(
            CapturePreference.Enabled,
            Owner,
            CaptureSessionBootstrap.CurrentIntegrationVersion,
            isHostedFeature001Session: false);

        List<CaptureBoundaryProcessingResult> transitions = [];
        transitions.Add(await ProcessIntervalAsync(temporary.Path, bootstrap, 0, 7, null, string.Empty, isInitialPrompt: true));
        transitions.Add(await ProcessIntervalAsync(
            temporary.Path, bootstrap, 7, 8, "Write-Output \"FIRST\"",
            Transcript("Write-Output \"FIRST\"", "FIRST", "start", "time", "end", "time")));
        transitions.Add(await ProcessIntervalAsync(temporary.Path, bootstrap, 8, 8, null, string.Empty));
        transitions.Add(await ProcessIntervalAsync(
            temporary.Path, bootstrap, 8, 9, "Set-Location ..",
            Transcript("Set-Location ..", string.Empty, "start", "time", "end", "time")));

        PreviousCommandResult zeroOutput = await RetrieveCommittedAsync(bootstrap);
        Assert.AreEqual(PreviousCommandResultKind.NoOutput, zeroOutput.Kind);

        transitions.Add(await ProcessIntervalAsync(
            temporary.Path, bootstrap, 9, 10, "Write-Output \"SECOND\"",
            Transcript("Write-Output \"SECOND\"", "SECOND", "start", "time", "end", "time")));

        Assert.IsTrue(transitions.All(result => result.Status == CaptureBoundaryStatus.ReadyForNextInterval));
        Assert.IsTrue(transitions.All(result => result.BoundaryFailureDetail == CaptureBoundaryFailureDetail.None));
        PreviousCommandResult latest = await RetrieveCommittedAsync(bootstrap);
        Assert.AreEqual(PreviousCommandResultKind.Success, latest.Kind);
        Assert.AreEqual("SECOND", latest.Snapshot!.Output);
        RetainedGeneration generation = (await new RetainedCaptureStore(bootstrap.SessionDirectory!).LoadCommittedAsync())!;
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, generation.Records.Select(record => record.Sequence).ToArray());
    }

    [TestMethod]
    public async Task ProductionBoundaryLifecycle_HistoryJumpFailsClosedWithContentFreeSubreasonAndRecoveryResetsEpoch()
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult bootstrap = await new CaptureSessionBootstrap(temporary.Path).InitializeAsync(
            CapturePreference.Enabled,
            Owner,
            CaptureSessionBootstrap.CurrentIntegrationVersion,
            isHostedFeature001Session: false);
        CaptureBoundaryProcessingResult failed = await ProcessIntervalAsync(
            temporary.Path,
            bootstrap,
            openingHistoryId: 4,
            closingHistoryId: 6,
            command: "Write-Output \"AMBIGUOUS\"",
            transcript: Transcript("Write-Output \"AMBIGUOUS\"", "AMBIGUOUS", "start", "time", "end", "time"));

        Assert.AreEqual(CaptureBoundaryStatus.Unavailable, failed.Status);
        Assert.AreEqual(CaptureBoundaryFailureDetail.HistoryIdentityMismatch, failed.BoundaryFailureDetail);
        CaptureBoundaryProcessingResult recovered = await new CaptureBoundaryProcessor(temporary.Path).RecoverAsync(
            bootstrap.Proof!.SessionId,
            bootstrap.Proof.Nonce,
            Owner,
            CaptureSessionBootstrap.CurrentIntegrationVersion);
        Assert.AreEqual(CaptureBoundaryStatus.ReadyForNextInterval, recovered.Status);

        CaptureBoundaryProcessingResult emptyRecoveryInterval = await ProcessIntervalAsync(
            temporary.Path, bootstrap, 6, 6, null, string.Empty);
        CaptureBoundaryProcessingResult nextCommand = await ProcessIntervalAsync(
            temporary.Path, bootstrap, 6, 7, "Write-Output \"RECOVERED\"",
            Transcript("Write-Output \"RECOVERED\"", "RECOVERED", "start", "time", "end", "time"));
        Assert.AreEqual(CaptureBoundaryStatus.ReadyForNextInterval, emptyRecoveryInterval.Status);
        Assert.AreEqual(CaptureBoundaryStatus.ReadyForNextInterval, nextCommand.Status);
        Assert.AreEqual("RECOVERED", (await RetrieveCommittedAsync(bootstrap)).Snapshot!.Output);
    }

    [TestMethod]
    public async Task ProductionBoundaryLifecycle_SameNativeFiveTwiceThenCmdletDoesNotInheritIt()
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult bootstrap = await new CaptureSessionBootstrap(temporary.Path).InitializeAsync(
            CapturePreference.Enabled,
            Owner,
            CaptureSessionBootstrap.CurrentIntegrationVersion,
            isHostedFeature001Session: false);

        const string native = "cmd /d /c exit 5";
        Assert.AreEqual(CaptureBoundaryStatus.ReadyForNextInterval, (await ProcessIntervalAsync(
            temporary.Path, bootstrap, 0, 1, native,
            Transcript(native, string.Empty, "start", "time", "end", "time"),
            succeeded: false, nativeExitCode: 5)).Status);
        Assert.AreEqual(CaptureBoundaryStatus.ReadyForNextInterval, (await ProcessIntervalAsync(
            temporary.Path, bootstrap, 1, 2, native,
            Transcript(native, "The native command reported failure.", "start", "time", "end", "time"),
            succeeded: false, nativeExitCode: 5)).Status);
        PreviousCommandSnapshot repeatedNative = (await RetrieveCommittedAsync(bootstrap)).Snapshot!;
        Assert.IsFalse(repeatedNative.PowerShellSucceeded);
        Assert.AreEqual(5, repeatedNative.NativeExitCode);
        TerminationFacts repeatedFacts = new(
            repeatedNative.PowerShellSucceeded,
            repeatedNative.NativeExitCode,
            repeatedNative.WasInterrupted);
        StringAssert.Contains(repeatedFacts.ToProviderText(), "nativeExitCode=5");

        const string cmdlet = "Write-Output \"SUCCESS\"";
        Assert.AreEqual(CaptureBoundaryStatus.ReadyForNextInterval, (await ProcessIntervalAsync(
            temporary.Path, bootstrap, 2, 3, cmdlet,
            Transcript(cmdlet, "SUCCESS", "start", "time", "end", "time"),
            succeeded: true, nativeExitCode: null)).Status);

        RetainedCaptureStore store = new(bootstrap.SessionDirectory!);
        RetainedGeneration generation = (await store.LoadCommittedAsync())!;
        List<CapturedCommand> records = [];
        foreach (RetainedRecordDescriptor descriptor in generation.Records.OrderBy(record => record.Sequence))
        {
            records.Add(RetainedCommandRecordCodec.Deserialize(
                await store.ReadContentAsync(descriptor),
                await store.ReadMetadataAsync(descriptor)));
        }

        CollectionAssert.AreEqual(new int?[] { 5, 5, null }, records.Select(record => record.Boundary.NativeExitCode).ToArray());
        CollectionAssert.AreEqual(new bool?[] { false, false, true }, records.Select(record => record.Boundary.PowerShellSucceeded).ToArray());
        PreviousCommandSnapshot selected = (await RetrieveCommittedAsync(bootstrap)).Snapshot!;
        Assert.AreEqual("SUCCESS", selected.Output);
        Assert.IsTrue(selected.PowerShellSucceeded);
        Assert.IsNull(selected.NativeExitCode);

        const string zeroOutput = "Set-Location .";
        Assert.AreEqual(CaptureBoundaryStatus.ReadyForNextInterval, (await ProcessIntervalAsync(
            temporary.Path, bootstrap, 3, 4, zeroOutput,
            Transcript(zeroOutput, string.Empty, "start", "time", "end", "time"),
            succeeded: true, nativeExitCode: null)).Status);
        RetainedGeneration afterZero = (await store.LoadCommittedAsync())!;
        RetainedRecordDescriptor zeroDescriptor = afterZero.Records.Single(record => record.Sequence == 4);
        CapturedCommand persistedZero = RetainedCommandRecordCodec.Deserialize(
            await store.ReadContentAsync(zeroDescriptor),
            await store.ReadMetadataAsync(zeroDescriptor));
        Assert.IsTrue(persistedZero.Boundary.PowerShellSucceeded);
        Assert.IsNull(persistedZero.Boundary.NativeExitCode);
        Assert.AreEqual(PreviousCommandResultKind.NoOutput, (await RetrieveCommittedAsync(bootstrap)).Kind);
    }

    [TestMethod]
    public async Task LegacyFirstPromptInterpretation_ProducesExactCommandIdentityMismatchSubreason()
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult bootstrap = await new CaptureSessionBootstrap(temporary.Path).InitializeAsync(
            CapturePreference.Enabled,
            Owner,
            CaptureSessionBootstrap.CurrentIntegrationVersion,
            isHostedFeature001Session: false);
        CaptureBoundaryProcessingResult result = await ProcessIntervalAsync(
            temporary.Path,
            bootstrap,
            openingHistoryId: 1,
            closingHistoryId: 2,
            command: ". 'managed-loader.ps1'",
            transcript: Transcript("Write-Output \"not-the-startup-command\"", string.Empty, "start", "time", "end", "time"));

        Assert.AreEqual(CaptureBoundaryStatus.Unavailable, result.Status);
        Assert.AreEqual(CaptureBoundaryFailureDetail.CommandIdentityMismatch, result.BoundaryFailureDetail);
    }

    private static async Task<(CapturedCommand Captured, PreviousCommandResult Retrieved)> FinalizeAndRetrieveAsync(
        string command,
        string transcript,
        bool succeeded = true,
        int? nativeExitCode = null,
        bool wasInterrupted = false,
        bool hasNativeFileRedirection = false)
    {
        using TemporaryDirectory temporary = new();
        CaptureBootstrapResult bootstrap = await new CaptureSessionBootstrap(temporary.Path).InitializeAsync(
            CapturePreference.Enabled,
            Owner,
            CaptureSessionBootstrap.CurrentIntegrationVersion,
            isHostedFeature001Session: false);
        await File.WriteAllTextAsync(bootstrap.StagingPath!, transcript);

        CaptureBoundaryProcessingResult result = await new CaptureBoundaryProcessor(temporary.Path).ProcessAsync(
            new CaptureBoundaryRequest(
                bootstrap.Proof!.SessionId,
                bootstrap.Proof.Nonce,
                Owner,
                CaptureSessionBootstrap.CurrentIntegrationVersion,
                bootstrap.StagingPath!,
                1,
                command,
                Succeeded: succeeded,
                NativeExitCode: nativeExitCode)
            {
                WasInterrupted = wasInterrupted,
                HasNativeFileRedirection = hasNativeFileRedirection,
            },
            CapturePreference.Enabled);

        Assert.AreEqual(CaptureBoundaryStatus.ReadyForNextInterval, result.Status);
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
        return (captured, retrieved);
    }

    private static async Task<CaptureBoundaryProcessingResult> ProcessIntervalAsync(
        string captureRoot,
        CaptureBootstrapResult bootstrap,
        long openingHistoryId,
        long closingHistoryId,
        string? command,
        string transcript,
        bool isInitialPrompt = false,
        bool succeeded = true,
        int? nativeExitCode = null)
    {
        CaptureOwnerManifest manifest = await CaptureSessionBootstrap.ReadOwnerManifestAsync(bootstrap.SessionDirectory!);
        string staging = CaptureSessionBootstrap.GetStagingPath(bootstrap.SessionDirectory!, manifest.NextSequence);
        await File.WriteAllTextAsync(staging, transcript);
        return await new CaptureBoundaryProcessor(captureRoot).ProcessAsync(
            new CaptureBoundaryRequest(
                bootstrap.Proof!.SessionId,
                bootstrap.Proof.Nonce,
                Owner,
                CaptureSessionBootstrap.CurrentIntegrationVersion,
                staging,
                closingHistoryId,
                command,
                Succeeded: succeeded,
                NativeExitCode: nativeExitCode)
            {
                OpeningHistoryId = openingHistoryId,
                IsInitialPrompt = isInitialPrompt,
            },
            CapturePreference.Enabled);
    }

    private static async Task<PreviousCommandResult> RetrieveCommittedAsync(CaptureBootstrapResult bootstrap)
    {
        RetainedCaptureStore store = new(bootstrap.SessionDirectory!);
        RetainedGeneration? generation = await store.LoadCommittedAsync();
        List<CapturedCommand> commands = [];
        if (generation is not null)
        {
            foreach (RetainedRecordDescriptor descriptor in generation.Records.OrderBy(record => record.Sequence))
            {
                commands.Add(RetainedCommandRecordCodec.Deserialize(
                    await store.ReadContentAsync(descriptor),
                    await store.ReadMetadataAsync(descriptor)));
            }
        }

        return PreviousCommandRetriever.Retrieve(new PreviousCommandRetrievalState(
            CapturePreference.Enabled,
            CaptureHealthState.Healthy,
            new CaptureSessionId(bootstrap.Proof!.SessionId, bootstrap.Proof.Nonce),
            commands,
            IsStoreReliable: true));
    }

    private static string Transcript(
        string command,
        string output,
        string startLabel,
        string startTime,
        string endLabel,
        string endTime)
    {
        string outputLine = output.Length == 0 ? string.Empty : output + "\r\n";
        return string.Join("\r\n",
            Separator,
            startLabel,
            startTime,
            "Username: test",
            Separator,
            $"CUSTOM> {command}",
            outputLine + Separator,
            endLabel,
            endTime,
            Separator,
            string.Empty);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string>? diagnostic = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
                Assert.Fail($"Timed out waiting for the isolated PowerShell prompt. Output: {diagnostic?.Invoke()}");
            await Task.Delay(20);
        }
    }

    private static async Task SendCommandAndWaitForFactsAsync(
        ConPtySession session,
        string command,
        string factsPath,
        int expectedLines)
    {
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes(command + "\r"));
        await session.Input.FlushAsync();
        await WaitUntilAsync(
            () => HasCompleteFactLines(factsPath, expectedLines),
            TimeSpan.FromSeconds(8));
    }

    private static bool HasCompleteFactLines(string path, int expectedLines)
    {
        try
        {
            return File.Exists(path) && File.ReadAllLines(path).Length >= expectedLines;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int Count(string value, string needle)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, output + error);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-envelope-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch (IOException) { }
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}
