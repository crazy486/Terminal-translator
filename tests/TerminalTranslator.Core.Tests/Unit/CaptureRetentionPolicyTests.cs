using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class CaptureRetentionPolicyTests
{
    private static readonly Func<IReadOnlyList<RetentionRecord>, long> ManifestSizer = _ => 100;

    [TestMethod]
    public void Plan_HandlesOneUnderExactAndOneOverHardLimit()
    {
        Assert.AreEqual(10_179_999, PlanSingle(10_179_899).RetainedBytes);
        Assert.AreEqual(10_180_000, PlanSingle(10_179_900).RetainedBytes);
        Assert.IsFalse(PlanSingle(10_179_901).CanFit);
    }

    [TestMethod]
    public void Plan_EvictsOldestWholeRecordsAndPreservesNewest()
    {
        RetentionRecord first = new("first", 1, 4_000_000);
        RetentionRecord second = new("second", 2, 4_000_000);
        RetentionRecord newest = new("newest", 3, 4_000_000);

        CaptureRetentionPlan plan = CaptureRetentionPolicy.Plan([first, second], newest, ManifestSizer);

        Assert.IsTrue(plan.CanFit);
        CollectionAssert.AreEqual(new[] { "first" }, plan.EvictedRecordIds.ToArray());
        CollectionAssert.AreEqual(new[] { "second", "newest" }, plan.RetainedRecords.Select(record => record.RecordId).ToArray());
        Assert.AreEqual(8_000_100, plan.RetainedBytes);
    }

    [TestMethod]
    public void Plan_NeverSplitsAnOrdinaryRecord()
    {
        RetentionRecord first = new("first", 1, 6_000_000);
        RetentionRecord newest = new("newest", 2, 5_000_000);
        CaptureRetentionPlan plan = CaptureRetentionPolicy.Plan([first], newest, ManifestSizer);
        Assert.AreEqual(1, plan.RetainedRecords.Count);
        Assert.AreSame(newest, plan.RetainedRecords.Single());
    }

    private static CaptureRetentionPlan PlanSingle(long bytes) =>
        CaptureRetentionPolicy.Plan([], new RetentionRecord("candidate", 1, bytes), ManifestSizer);
}
