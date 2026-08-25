using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class CaptureModelsTests
{
    private static readonly CaptureSessionId Session = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "nonce-a");

    [TestMethod]
    public void CaptureSessionId_RequiresNonEmptyGuidAndNonce()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CaptureSessionId(Guid.Empty, "nonce"));
        Assert.ThrowsExactly<ArgumentException>(() => new CaptureSessionId(Guid.NewGuid(), " "));
    }

    [TestMethod]
    public void CapturedCommand_RequiresReliableMatchingBoundary()
    {
        CommandBoundary boundary = new(Session, 1, "git status", true, 0, false);
        CapturedCommand command = new(Session, 1, "git status", "output", boundary, LocalCaptureCompleteness.Complete, 6, false);
        Assert.AreEqual(1L, command.Sequence);

        Assert.ThrowsExactly<ArgumentException>(() => new CapturedCommand(
            Session,
            2,
            "git status",
            "output",
            boundary,
            LocalCaptureCompleteness.Complete,
            6,
            false));
    }

    [TestMethod]
    public void LocalAndAiCompleteness_AreIndependent()
    {
        PreviousCommandSnapshot snapshot = new(
            Session,
            1,
            "build",
            "head\ntail",
            1,
            false,
            LocalCaptureCompleteness.LocalHeadTail,
            20_000_000);
        Assert.AreEqual(LocalCaptureCompleteness.LocalHeadTail, snapshot.LocalCompleteness);
        Assert.AreEqual(20_000_000, snapshot.OriginalOutputBytes);
    }

    [TestMethod]
    public void RetrievalOutcome_HasExclusiveKindsAndNeverCarriesSnapshotOnFailure()
    {
        PreviousCommandResult noOutput = PreviousCommandResult.NoOutput();
        Assert.AreEqual(PreviousCommandResultKind.NoOutput, noOutput.Kind);
        Assert.IsNull(noOutput.Snapshot);

        PreviousCommandSnapshot snapshot = new(Session, 1, "echo ok", "ok", 0, false, LocalCaptureCompleteness.Complete, 2);
        PreviousCommandResult success = PreviousCommandResult.Success(snapshot);
        Assert.AreEqual(PreviousCommandResultKind.Success, success.Kind);
        Assert.AreSame(snapshot, success.Snapshot);
    }
}
