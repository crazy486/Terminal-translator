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
}
