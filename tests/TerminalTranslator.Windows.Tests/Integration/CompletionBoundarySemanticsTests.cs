using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class CompletionBoundarySemanticsTests
{
    private static readonly CaptureSessionId Session = new(Guid.Parse("77777777-7777-7777-7777-777777777777"), "nonce");

    [TestMethod]
    public void PostPromptAsyncOutputIsNotReassignedBackward()
    {
        CommandBoundaryEvidence evidence = Evidence("Write-Output done", "done") with { PostCompletionOutput = "late background output" };
        BoundaryValidationResult result = CommandBoundaryValidator.Validate(evidence);
        Assert.AreEqual("done", result.OrderedOutput);
        Assert.IsFalse(result.OrderedOutput!.Contains("late", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ClearHostPreservesOlderHistoryButItselfIsStrictNoOutput()
    {
        PreviousCommandRetrievalState state = new(CapturePreference.Enabled, CaptureHealthState.Healthy, Session,
            [Command(1, "Write-Output useful", "useful"), Command(2, "Clear-Host", string.Empty)], true);
        Assert.AreEqual(PreviousCommandResultKind.NoOutput, PreviousCommandRetriever.Retrieve(state).Kind);
    }

    private static CommandBoundaryEvidence Evidence(string command, string output) =>
        new(1, Session, Session, 1, 1, true, true, 1, command, command, output, 0, false, true);

    private static CapturedCommand Command(long sequence, string text, string output)
    {
        CommandBoundary boundary = new(Session, sequence, text, true, 0, false);
        return new(Session, sequence, text, output, boundary, LocalCaptureCompleteness.Complete,
            System.Text.Encoding.UTF8.GetByteCount(output), false);
    }
}
