using System.Text;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Parsing;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class VtTextExtractorTests
{
    [TestMethod]
    public void Feed_DecodesSplitUtf8AndRemovesBasicSgr()
    {
        VtTextExtractor extractor = new();
        byte[] bytes = Encoding.UTF8.GetBytes("\u001b[31mOperation failed \u4e2d\u6587\u001b[0m\r\n");
        Assert.AreEqual(0, extractor.Feed(bytes.AsSpan(0, bytes.Length - 2)).Count);
        IReadOnlyList<ExtractedText> result = extractor.Feed(bytes.AsSpan(bytes.Length - 2));
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Operation failed \u4e2d\u6587", result[0].Text);
        Assert.AreEqual(SourceBoundary.Line, result[0].Boundary);
    }

    [TestMethod]
    public void Feed_CoalescesIndentedLogicalBlockAndTracksLayout()
    {
        VtTextExtractor extractor = new();
        IReadOnlyList<ExtractedText> result = extractor.Feed(Encoding.UTF8.GetBytes("Build failed:\n  first reason\n    detail\n\n"));
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Build failed:\n  first reason\n    detail", result[0].Text);
        CollectionAssert.AreEqual(new[] { 0, 2, 4 }, result[0].Layout.Indent.ToArray());
        Assert.AreEqual(SourceBoundary.Block, result[0].Boundary);
    }

    [TestMethod]
    public void Feed_SupportsCrLfAndLfWithoutDuplicateEmptyCandidates()
    {
        VtTextExtractor extractor = new();
        IReadOnlyList<ExtractedText> result = extractor.Feed(Encoding.UTF8.GetBytes("First line\r\nSecond line\n"));
        Assert.AreEqual(2, result.Count);
        CollectionAssert.AreEqual(new[] { "First line", "Second line" }, result.Select(item => item.Text).ToArray());
    }

    [TestMethod]
    public void Feed_RepeatedCarriageReturnBeforeLineFeedPreservesCompletedText()
    {
        VtTextExtractor extractor = new();

        IReadOnlyList<ExtractedText> result = extractor.Feed(
            Encoding.UTF8.GetBytes("Translation resumed.\r\r\n"));

        CollectionAssert.AreEqual(
            new[] { "Translation resumed." },
            result.Select(item => item.Text).ToArray());
    }

    [TestMethod]
    public void FlushIdle_BottomRowScrollDoesNotDiscardCompletedOutput()
    {
        VtTextExtractor extractor = new(viewportRows: 20);

        _ = extractor.Feed(Encoding.UTF8.GetBytes(
            "\u001b[20;1HTranslation resumed.\r\nPS C:\\>   \u001b[20;10H"));
        IReadOnlyList<ExtractedText> result = extractor.FlushIdle();

        CollectionAssert.Contains(
            result.Select(item => item.Text).ToArray(),
            "Translation resumed.");
    }

    [TestMethod]
    public void Feed_DoesNotJoinDistinctLowercaseLogicalLines()
    {
        VtTextExtractor extractor = new();

        IReadOnlyList<ExtractedText> result = extractor.Feed(
            Encoding.UTF8.GetBytes("First complete line\r\nsecond independent line\r\n"));

        CollectionAssert.AreEqual(
            new[] { "First complete line", "second independent line" },
            result.Select(item => item.Text).ToArray());
    }

    [TestMethod]
    public void Feed_SkipsCandidateLargerThanEightKiBUtf8()
    {
        VtTextExtractor extractor = new();
        string oversized = new string('a', VtTextExtractor.MaximumCandidateBytes + 1) + "\n";
        Assert.AreEqual(0, extractor.Feed(Encoding.UTF8.GetBytes(oversized)).Count);
    }
}
