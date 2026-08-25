using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class ContextualCommandSelectionTests
{
    private static readonly CaptureSessionId Session = new(Guid.Parse("55555555-5555-5555-5555-555555555555"), "nonce");

    [TestMethod]
    public void ConsecutiveContextualCommandsDoNotSelfPollute()
    {
        CapturedCommand target = Command(1, "git status", "output");
        CapturedCommand? selected = ContextualCommandSelection.SelectStrictTarget([
            target,
            Command(2, "tt last", "translation"),
            Command(3, "tt last", "translation"),
            Command(4, "tt ask last why", "answer"),
        ]);
        Assert.AreSame(target, selected);
    }

    [TestMethod]
    public void OrdinaryAskRemainsAnOrdinaryStrictPreviousCommand()
    {
        Assert.IsFalse(ContextualCommandSelection.IsContextual("tt ask why is this failing"));
        Assert.IsTrue(ContextualCommandSelection.IsContextual("tt last"));
        Assert.IsTrue(ContextualCommandSelection.IsContextual("tt ask last why"));

        CapturedCommand ask = Command(2, "tt ask why", "answer");
        Assert.AreSame(ask, ContextualCommandSelection.SelectStrictTarget([
            Command(1, "git status", "output"), ask, Command(3, "tt last", "translation")
        ]));
    }

    private static CapturedCommand Command(long sequence, string text, string output)
    {
        CommandBoundary boundary = new(Session, sequence, text, true, 0, false);
        return new(Session, sequence, text, output, boundary, LocalCaptureCompleteness.Complete,
            System.Text.Encoding.UTF8.GetByteCount(output), ContextualCommandSelection.IsContextual(text));
    }
}
