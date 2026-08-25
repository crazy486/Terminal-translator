using System.Text;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class LocalCaptureTruncationTests
{
    private static readonly CaptureSessionId Session = new(Guid.Parse("99999999-9999-9999-9999-999999999999"), "nonce");

    [TestMethod]
    public void Create_ReservesDynamicMetadataAndPreservesTrustedIdentityBoundaryAndOriginalBytes()
    {
        CapturedCommand source = Command(new string('x', 200));
        LocalRetentionResult result = OversizedCommandRetention.Create(source, metadataBytes: 40, indexBytes: 10, manifestBytes: 10, maximumBytes: 100);

        Assert.IsTrue(result.Supported);
        Assert.AreEqual(LocalCaptureCompleteness.LocalHeadTail, result.Command!.LocalCompleteness);
        Assert.AreEqual(200, result.Command.OriginalOutputBytes);
        Assert.AreEqual(source.Session, result.Command.Session);
        Assert.AreSame(source.Boundary, result.Command.Boundary);
        Assert.IsLessThanOrEqualTo(result.RetainedLogicalBytes, 100);
        Assert.AreEqual(20, Encoding.UTF8.GetByteCount(result.Command.Output[..(result.Command.Output.Length / 2)]));

        LocalRetentionResult lessMetadata = OversizedCommandRetention.Create(source, 20, 10, 10, 100);
        Assert.IsGreaterThan(Encoding.UTF8.GetByteCount(result.Command.Output), Encoding.UTF8.GetByteCount(lessMetadata.Command!.Output));
    }

    [TestMethod]
    public void Create_FailsClosedWhenEssentialMetadataCannotFit()
    {
        LocalRetentionResult result = OversizedCommandRetention.Create(Command("output"), 80, 10, 10, 100);
        Assert.IsFalse(result.Supported);
        Assert.IsNull(result.Command);
    }

    [TestMethod]
    public void DefaultCap_BoundsSingleCommandAbove10180000Bytes()
    {
        string output = new('a', checked((int)CaptureRetentionPolicy.MaximumRetainedBytes + 1));
        CapturedCommand source = Command(output);
        LocalRetentionResult result = OversizedCommandRetention.Create(source, 512, 128, 256);
        Assert.IsTrue(result.Supported);
        Assert.AreEqual(CaptureRetentionPolicy.MaximumRetainedBytes + 1, result.Command!.OriginalOutputBytes);
        Assert.IsLessThanOrEqualTo(result.RetainedLogicalBytes, CaptureRetentionPolicy.MaximumRetainedBytes);
    }

    private static CapturedCommand Command(string output)
    {
        CommandBoundary boundary = new(Session, 1, "generate", true, 0, false);
        return new(Session, 1, "generate", output, boundary, LocalCaptureCompleteness.Complete,
            Encoding.UTF8.GetByteCount(output), false);
    }
}
