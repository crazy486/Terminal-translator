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
            Assert.AreEqual(AnalysisLineDisposition.PowerShellArtifact, tracker.ClassifyAnalysisLine(echo));
            Assert.AreEqual(AnalysisLineDisposition.PowerShellArtifact, tracker.ClassifyAnalysisLine(echo));
            Assert.AreEqual(
                AnalysisLineDisposition.ProgramOutput,
                tracker.ClassifyAnalysisLine($"The output for operation {index + 1} remains visible."));
        }
    }

    [TestMethod]
    public void ClassifyAnalysisLine_TerminalTranslatorCommandOwnsOutputUntilNextPrompt()
    {
        SubmittedCommandTracker tracker = new();
        tracker.Observe("tt on\r\n"u8.ToArray());

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
            AnalysisLineDisposition.PowerShellArtifact,
            tracker.ClassifyAnalysisLine("PS C:\\work>"));
        Assert.AreEqual(
            AnalysisLineDisposition.ProgramOutput,
            tracker.ClassifyAnalysisLine("The child program produced ordinary output."));

        tracker.Observe("tt status\r\n"u8.ToArray());
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellArtifact,
            tracker.ClassifyAnalysisLine("PS D:\\changed> tt status"));
        Assert.AreEqual(
            AnalysisLineDisposition.TerminalTranslatorControlOutput,
            tracker.ClassifyAnalysisLine("privacy-skipped: 0"));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellArtifact,
            tracker.ClassifyAnalysisLine("PS D:\\changed>"));

        tracker.Observe("tt off\r\n"u8.ToArray());
        Assert.AreEqual(
            AnalysisLineDisposition.TerminalTranslatorControlOutput,
            tracker.ClassifyAnalysisLine("Translation disabled."));
        Assert.AreEqual(
            AnalysisLineDisposition.PowerShellArtifact,
            tracker.ClassifyAnalysisLine("PS E:\\next>"));
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
}
