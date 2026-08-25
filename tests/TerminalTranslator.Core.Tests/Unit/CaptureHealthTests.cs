using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class CaptureHealthTests
{
    [TestMethod]
    public void FirstOutageWarnsOnceAndValidatedRecoveryRestoresOnce()
    {
        CaptureHealth health = new();
        Assert.AreEqual(CaptureHealthState.Starting, health.State);
        Assert.AreEqual(CaptureHealthNotification.None, health.MarkHealthy());

        Assert.AreEqual(
            CaptureHealthNotification.CaptureUnavailable,
            health.MarkUnavailable(CaptureFailureReason.TranscriptStart));
        Assert.AreEqual(
            CaptureHealthNotification.None,
            health.MarkUnavailable(CaptureFailureReason.TranscriptStart));
        Assert.AreEqual(CaptureHealthState.Unavailable, health.State);

        Assert.AreEqual(CaptureHealthNotification.None, health.BeginRecovery());
        Assert.AreEqual(CaptureHealthNotification.CaptureRestored, health.MarkHealthy());
        Assert.AreEqual(CaptureHealthNotification.None, health.MarkHealthy());
        Assert.AreEqual(CaptureHealthState.Healthy, health.State);
    }

    [TestMethod]
    public void HealthFactsAreContentFreeEnumsOnly()
    {
        CaptureHealth health = new();
        _ = health.MarkUnavailable(CaptureFailureReason.Retention);
        Assert.AreEqual(CaptureFailureReason.Retention, health.FailureReason);
        Assert.IsNull(health.Detail);
        Assert.AreEqual(CaptureHealthNotification.None, health.Close());
        Assert.AreEqual(CaptureHealthState.Closing, health.State);
    }
}
