using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class InterruptedCommandBoundaryTests
{
    private static readonly CaptureSessionId Session = new(Guid.Parse("66666666-6666-6666-6666-666666666666"), "nonce");

    [TestMethod]
    public void ReliablePowerShellAndNativeInterruptionsPreservePartialOutput()
    {
        foreach (string command in new[] { "Start-Sleep 10", "native.exe --wait" })
        {
            BoundaryValidationResult result = CommandBoundaryValidator.Validate(Evidence(command, reliableTermination: true));
            Assert.IsTrue(result.IsReliable);
            Assert.IsTrue(result.Boundary!.WasInterrupted);
            Assert.AreEqual("partial output", result.OrderedOutput);
        }
    }

    [TestMethod]
    public void AmbiguousInterruptionFailsClosed()
    {
        BoundaryValidationResult result = CommandBoundaryValidator.Validate(Evidence("native.exe", reliableTermination: false));
        Assert.IsFalse(result.IsReliable);
        Assert.AreEqual("unreliable-interruption", result.FailureReason);
    }

    private static CommandBoundaryEvidence Evidence(string command, bool reliableTermination) => new(
        1, Session, Session, 1, 1, true, true, 1, command, command, "partial output", null, true, reliableTermination);
}
