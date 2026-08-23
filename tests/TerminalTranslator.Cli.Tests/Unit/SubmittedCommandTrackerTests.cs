using System.Text;
using TerminalTranslator.Cli.Commands;

namespace TerminalTranslator.Cli.Tests.Unit;

[TestClass]
public sealed class SubmittedCommandTrackerTests
{
    [TestMethod]
    public void IsEcho_ReadHostAssignmentWithPsReadLineVtShape_SuppressesOnlyFirstMatch()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe(Encoding.UTF8.GetBytes("$value = Read-Host \"Paste Unicode text\"\r\n"));

        const string extractedEcho = "> $value = Read-Host \"Paste Unicode text\"";
        Assert.IsTrue(tracker.IsEcho(extractedEcho));
        Assert.IsFalse(tracker.IsEcho(extractedEcho), "A real program output equal to the command must remain eligible.");
    }

    [TestMethod]
    public void IsEcho_WrappedPsReadLinePrefixWithPrompt_SuppressesFragment()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe(Encoding.UTF8.GetBytes("$value = Read-Host \"Paste Unicode text\"\r\n"));

        Assert.IsTrue(tracker.IsEcho(
            "PS D:\\Projects\\Terminal Translator>\n> $value = Read-Host \"Paste Unico"));
    }

    [TestMethod]
    public void IsEcho_FragmentedPromptTailBeforeReadHostAssignment_IsSuppressed()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe(Encoding.UTF8.GetBytes("$value = Read-Host \"Paste Unicode text\"\r\n"));

        Assert.IsTrue(tracker.IsEcho("$> $value = Read-Host \"Paste Unico"));
        Assert.IsFalse(tracker.IsEcho("The actual command output remains eligible."));
    }

    [TestMethod]
    public void Observe_PsReadLineEditingAndSplitUtf8_TracksFinalSubmittedCommand()
    {
        SubmittedCommandTracker tracker = new();
        byte[] input = Encoding.UTF8.GetBytes("Write-Output \"Unicode: 你好，世届\b界\"\r\n");

        tracker.Observe(input.AsMemory(0, 19));
        tracker.Observe(input.AsMemory(19));

        Assert.IsTrue(tracker.IsEcho("> Write-Output \"Unicode: 你好，世界\""));
    }

    [TestMethod]
    public void IsEcho_DoesNotSuppressUnrelatedCommandOutput()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe("$value = Read-Host \"Paste Unicode text\"\r\n"u8.ToArray());

        Assert.IsFalse(tracker.IsEcho("The build failed because a required file is missing."));
    }

    [TestMethod]
    public void ClassifyAnalysisLine_RepeatedHereStringContinuationEchoesRemainArtifacts()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe(Encoding.UTF8.GetBytes(
            "@\"\r\n" +
            "The server is running normally.\r\n" +
            "Three background tasks are currently active.\r\n" +
            "No critical errors were detected.\r\n" +
            "\"@\r\n"));

        string[] continuationEchoes =
        [
            ">> The server is running normally.",
            ">> Three background tasks are currently active.",
            ">> No critical errors were detected.",
            ">> \"@",
        ];
        foreach (string echo in continuationEchoes)
        {
            Assert.AreEqual(AnalysisLineDisposition.PowerShellArtifact, tracker.ClassifyAnalysisLine(echo));
            Assert.AreEqual(AnalysisLineDisposition.PowerShellArtifact, tracker.ClassifyAnalysisLine(echo));
        }

        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine(">> This unmatched text is ordinary program output."));
    }

    [TestMethod]
    public void ClassifyAnalysisLine_VisuallyWrappedPromptAndContinuationSequenceIsArtifactBoundary()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe(Encoding.UTF8.GetBytes(
            "@\"\r\n" +
            "The server is running normally.\r\n" +
            "Three background tasks are currently active.\r\n" +
            "No critical errors were detected.\r\n" +
            "\"@\r\n"));

        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine(
                "PS D:\\a-very-long-working-directory>> @\">> The server is running normally."));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellArtifact,
            tracker.ClassifyAnalysisLine(">> Three background tasks are currently active."));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellArtifact,
            tracker.ClassifyAnalysisLine(">> No critical errors were detected."));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellArtifact,
            tracker.ClassifyAnalysisLine(">> \"@"));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("The server is running normally."));
    }

    [TestMethod]
    public void ClassifyAnalysisLine_HereStringBodyThatLooksLikeTtCommand_DoesNotOwnOutput()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe(Encoding.UTF8.GetBytes("@\"\r\ntt status\r\n\"@\r\n"));

        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellArtifact,
            tracker.ClassifyAnalysisLine(">> tt status"));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellArtifact,
            tracker.ClassifyAnalysisLine(">> \"@"));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("tt status"));
    }

    [TestMethod]
    public void ClassifyAnalysisLine_TracksMultipleRapidCommandsAcrossDirectoryChanges()
    {
        SubmittedCommandTracker tracker = new();
        string[] commands =
        [
            "Write-Output \"The first operation completed successfully.\"",
            "Write-Output \"The second operation completed successfully.\"",
            "Write-Output \"The third operation completed successfully.\"",
        ];
        tracker.Observe(Encoding.UTF8.GetBytes(string.Join("\r\n", commands) + "\r\n"));
        string[] prompts = ["PS C:\\>", "PS D:\\changed>", "PS D:\\changed\\again>"];

        for (int index = 0; index < commands.Length; index++)
        {
            string echo = $"{prompts[index]} {commands[index]}";
            Assert.AreEqual(AnalysisLineDisposition.PowerShellHardBoundary, tracker.ClassifyAnalysisLine(echo));
            Assert.AreEqual(AnalysisLineDisposition.PowerShellHardBoundary, tracker.ClassifyAnalysisLine(echo));
            Assert.AreEqual(
                AnalysisLineDisposition.ProgramOutput,
                tracker.ClassifyAnalysisLine($"The output for operation {index + 1} remains visible."));
        }
    }

    [TestMethod]
    public void ClassifyAnalysisLine_HistoricalPromptRedrawRemainsOwnedAfterLaterTransactionStarts()
    {
        SubmittedCommandTracker tracker = new();
        const string first = "Write-Output 'First operation completed successfully.'";
        const string second = "Write-Output 'Second operation completed successfully.'";
        tracker.Observe(Encoding.UTF8.GetBytes($"{first}\r{second}\r"));

        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine($"PS C:\\work> {first}"));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("First operation completed successfully."));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine($"PS C:\\work> {second}"));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine($"PS C:\\work> {first}"));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine(first),
            "Bare program output equal to a historical command is not a PSReadLine prompt redraw.");
    }

    [TestMethod]
    public void ClassifyAnalysisLine_TerminalTranslatorCommandOwnsOutputUntilNextPrompt()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe("tt on\r\n"u8.ToArray());

        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS C:\\work> tt on"));
        Assert.AreEqual(
            AnalysisLineDisposition.TerminalTranslatorControlOutput,
            tracker.ClassifyAnalysisLine("Translation enabled."));
        Assert.AreEqual(
            AnalysisLineDisposition.TerminalTranslatorControlOutput,
            tracker.ClassifyAnalysisLine("Translation resumed."));
        Assert.AreEqual(
            AnalysisLineDisposition.TerminalTranslatorControlOutput,
            tracker.ClassifyAnalysisLine("Provider status is available."));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS C:\\work>"));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("The child program produced ordinary output."));

        tracker.Observe("tt status\r\n"u8.ToArray());
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS D:\\changed> tt status"));
        Assert.AreEqual(
            AnalysisLineDisposition.TerminalTranslatorControlOutput,
            tracker.ClassifyAnalysisLine("privacy-skipped: 0"));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS D:\\changed>"));

        tracker.Observe("tt off\r\n"u8.ToArray());
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS E:\\next> tt off"));
        Assert.AreEqual(
            AnalysisLineDisposition.TerminalTranslatorControlOutput,
            tracker.ClassifyAnalysisLine("Translation disabled."));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS E:\\next>"));

        tracker.Observe("tt configure --model future-model\r\n"u8.ToArray());
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS E:\\next> tt configure --model future-model"));
        Assert.AreEqual(
            AnalysisLineDisposition.TerminalTranslatorControlOutput,
            tracker.ClassifyAnalysisLine("Arbitrary future control-plane output."));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS E:\\next>"));
    }

    [TestMethod]
    public void ClassifyAnalysisLine_ControlOwnershipSurvivesMissingEchoAndLaggingOldPrompt()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe("tt on\r\n"u8.ToArray());

        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS C:\\work>"),
            "A primary prompt emitted before the observed input can reach the asynchronous analyzer later.");
        Assert.AreEqual(
            AnalysisLineDisposition.TerminalTranslatorControlOutput,
            tracker.ClassifyAnalysisLine("Arbitrary future control-plane output."));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS C:\\work>"));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("The next child program output remains eligible."));
    }

    [TestMethod]
    public void ClassifyAnalysisLine_ControlOwnershipEndsWhenWrappedPrimaryPromptEchoesNextSubmittedCommand()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe("tt on\r"u8.ToArray());

        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS C:\\work> tt on"));
        Assert.AreEqual(
            AnalysisLineDisposition.TerminalTranslatorControlOutput,
            tracker.ClassifyAnalysisLine("Arbitrary future control-plane output."));

        // With a narrow pane PowerShell can retain the prompt prefix on the
        // previous visual row and redraw only its final '>' plus the next
        // submitted command. Matching the pending submitted command is the
        // semantic proof that control has returned to the shell.
        tracker.Observe("Write-Output \"The real application output starts here.\"\r"u8.ToArray());
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine(
                "> Write-Output \"The real application output starts here.\""));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("The real application output starts here."));
    }

    [TestMethod]
    public void ClassifyAnalysisLine_PrimaryPromptWithPartialAndFinalPsReadLineRedrawIsSubmittedEcho()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe("exit 0\r"u8.ToArray());

        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS C:\\work> exit exit 0"));
    }

    [TestMethod]
    public void ClassifyAnalysisLine_UnmatchedPromptShapedAndMarkerOutputRemainProgramOutput()
    {
        SubmittedCommandTracker tracker = new();

        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("PS C:\\> this is ordinary output"));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine(">> This is ordinary output without matching submitted input."));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("The report contains > and >> plus PS markers."));
    }

    [TestMethod]
    public void ClassifyAnalysisLine_PromptWithAttachedContinuationRedrawIsHardBoundary()
    {
        SubmittedCommandTracker tracker = new();

        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS C:\\work> >>"));
    }

    [TestMethod]
    public void ClassifyAnalysisLine_UnrecognizedPromptReleasesControlAtNextSubmittedCommand()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe("tt status\r"u8.ToArray());

        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS C:\\work> tt status"));
        Assert.AreEqual(
            AnalysisLineDisposition.TerminalTranslatorControlOutput,
            tracker.ClassifyAnalysisLine("arbitrary control-plane output"));

        tracker.Observe("Write-Output \"Real output.\"\r"u8.ToArray());

        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("λ Write-Output \"Real output.\""));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("Real output."));
    }

    [TestMethod]
    public void ClassifyAnalysisLine_MissingBoundaryHasFiniteControlOwnershipBudget()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe("tt status\r"u8.ToArray());
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine("PS C:\\work> tt status"));

        AnalysisLineDisposition[] dispositions = Enumerable.Range(1, 128)
            .Select(index => tracker.ClassifyAnalysisLine($"Unrecognized boundary output {index}."))
            .ToArray();

        Assert.IsTrue(
            dispositions.Contains(AnalysisLineDisposition.ProgramOutput),
            "Control ownership must fail open after a finite number of unconfirmed output lines.");
    }

    [TestMethod]
    public void ClassifyAnalysisLine_GreaterThanPendingCommandCollisionFailsOpen()
    {
        SubmittedCommandTracker tracker = new();
        const string producingCommand =
            "Write-Output '> tt status'; Write-Output 'Collision survived.'";
        tracker.Observe(Encoding.UTF8.GetBytes(producingCommand + "\rtt status\r"));

        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine($"PS C:\\work> {producingCommand}"));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("> tt status"));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("Collision survived."));
    }

    [TestMethod]
    public void ClassifyAnalysisLine_GreaterThanProgramOutputNeverChangesTransactionOwnership()
    {
        SubmittedCommandTracker tracker = new();

        AnalysisLineDisposition bareMarker = tracker.ClassifyAnalysisLine(">");
        Assert.AreNotEqual(AnalysisLineDisposition.PowerShellHardBoundary, bareMarker);
        Assert.AreNotEqual(AnalysisLineDisposition.TerminalTranslatorControlOutput, bareMarker);
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("value > threshold"));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("PS > something"));

        tracker.Observe("Write-Output \"The next operation completed normally.\"\r"u8.ToArray());
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine(
                "PS C:\\work> Write-Output \"The next operation completed normally.\""));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("The next operation completed normally."));
    }

    [TestMethod]
    public void ObservationEpoch_RejectsStaleCallsAndStartsWithNoPriorOwnership()
    {
        SubmittedCommandTracker tracker = new();
        tracker.BeginObservationEpoch(1);
        Assert.IsTrue(tracker.Observe(1, "tt status\r"u8.ToArray()));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine(1, "PS C:\\work> tt status"));

        tracker.BeginObservationEpoch(2);

        Assert.IsFalse(tracker.Observe(1, "Write-Output \"stale input\"\r"u8.ToArray()));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine(1, "Stale output cannot mutate the new epoch."));
        Assert.IsTrue(tracker.Observe(
            2,
            "Write-Output \"The new epoch completed normally.\"\r"u8.ToArray()));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine(
                2,
                "PS C:\\work> Write-Output \"The new epoch completed normally.\""));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine(2, "The new epoch completed normally."));
    }

    [TestMethod]
    public void ObservationEpoch_ExplicitClaimRebindsOnlyDormantTtOnControl()
    {
        SubmittedCommandTracker tracker = new();
        tracker.BeginObservationEpoch(1);
        Assert.IsTrue(tracker.ObserveDormantInput(
            1,
            "Write-Output \"Disabled ordinary input.\"\rtt on\ry\r"u8.ToArray()));

        tracker.BeginObservationEpoch(2, claimDormantEnableControl: true);

        Assert.AreEqual(
            AnalysisLineDisposition.TerminalTranslatorControlOutput,
            tracker.ClassifyAnalysisLine(2, "Translation enabled."));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellHardBoundary,
            tracker.ClassifyAnalysisLine(2, "PS C:\\work> "));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine(2, "The new epoch completed normally."));
    }

    [TestMethod]
    public void ObservationEpoch_DormantTtOnWithoutExplicitClaimIsNotOwnership()
    {
        SubmittedCommandTracker tracker = new();
        tracker.BeginObservationEpoch(1);
        Assert.IsTrue(tracker.ObserveDormantInput(1, "tt on\r"u8.ToArray()));

        tracker.BeginObservationEpoch(2);

        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine(2, "Translation enabled."));
    }
}
