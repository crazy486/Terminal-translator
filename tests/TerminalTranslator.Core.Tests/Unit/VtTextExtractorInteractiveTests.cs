using System.Text;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Parsing;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class VtTextExtractorInteractiveTests
{
    [TestMethod]
    public void Feed_HandlesSplitVtAndSuppressesOscAndDcs()
    {
        VtTextExtractor extractor = new();
        Assert.AreEqual(0, extractor.Feed(Encoding.UTF8.GetBytes("\u001b[3")).Count);
        Assert.AreEqual(0, extractor.Feed(Encoding.UTF8.GetBytes("1mUseful English message\u001b]0;private title\a\u001bPprivate payload\u001b\\\u001b[0m\r")).Count);
        IReadOnlyList<ExtractedText> result = extractor.Feed("\n"u8);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Useful English message", result[0].Text);
    }

    [TestMethod]
    public void Feed_BareCarriageReturnAndEraseKeepOnlyFinalFrame()
    {
        VtTextExtractor extractor = new();
        IReadOnlyList<ExtractedText> result = extractor.Feed(
            Encoding.UTF8.GetBytes("Downloading ten percent\rDownloading complete\u001b[2K\rFinal useful result\r\n"));
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Final useful result", result[0].Text);
    }

    [TestMethod]
    public void Feed_SuppressesAlternateScreenContent()
    {
        VtTextExtractor extractor = new();
        IReadOnlyList<ExtractedText> result = extractor.Feed(
            Encoding.UTF8.GetBytes("\u001b[?1049hHidden alternate content\r\n\u001b[?1049lVisible useful content\r\n"));
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Visible useful content", result[0].Text);
    }

    [TestMethod]
    public void Feed_OverlongControlInvalidatesPartialCandidateAndRecovers()
    {
        VtTextExtractor extractor = new();
        string malformed = "Stale secret text\u001b]" +
            new string('x', VtTextExtractor.MaximumControlSequenceCharacters + 1) +
            "\aRecovered useful output\r\n";
        IReadOnlyList<ExtractedText> result = extractor.Feed(Encoding.UTF8.GetBytes(malformed));
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Recovered useful output", result[0].Text);
    }

    [TestMethod]
    public void FlushIdle_EmitsPromptWithIdleBoundary()
    {
        VtTextExtractor extractor = new();
        Assert.AreEqual(0, extractor.Feed(Encoding.UTF8.GetBytes("Would you like to continue? ")).Count);
        ExtractedText prompt = extractor.FlushIdle().Single();
        Assert.AreEqual(SourceBoundary.IdlePrompt, prompt.Boundary);
        Assert.AreEqual("Would you like to continue?", prompt.Text);
    }

    [TestMethod]
    public void Feed_RejoinsRightMarginVisualWrapIntoLogicalLine()
    {
        VtTextExtractor extractor = new(viewportColumns: 10);

        IReadOnlyList<ExtractedText> result = extractor.Feed(
            Encoding.UTF8.GetBytes("Productio\r\n\u001b[3;10HonRuntimeJourneyTests.cs\r\n"));

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("ProductionRuntimeJourneyTests.cs", result[0].Text);
    }

    [TestMethod]
    public void Feed_PsReadLineRightMarginRedrawDoesNotDuplicateSubmittedCommand()
    {
        VtTextExtractor extractor = new(viewportColumns: 60, viewportRows: 20);

        Assert.AreEqual(0, extractor.Feed(Encoding.UTF8.GetBytes(
            "\u001b[18;56H\u001b[92mexit ")).Count);
        IReadOnlyList<ExtractedText> result = extractor.Feed(Encoding.UTF8.GetBytes(
            "\u001b[m\r\n\u001b[92m\u001b[19;56Hexit \u001b[97m0\u001b[?25h\u001b[m\r\n"));

        CollectionAssert.AreEqual(
            new[] { "exit 0" },
            result.Select(item => item.Text).ToArray());
    }

    [TestMethod]
    public void Feed_PsReadLineRightMarginRedrawRejoinsLeadingSpaceTail()
    {
        VtTextExtractor extractor = new(viewportColumns: 60, viewportRows: 20);

        IReadOnlyList<ExtractedText> result = extractor.Feed(Encoding.UTF8.GetBytes(
            "\u001b[18;57H\u001b[92mexit\u001b[m\r\n" +
            "\u001b[92m\u001b[19;57Hexit\u001b[m\r\n \u001b[97m0\u001b[K\u001b[?25h\u001b[m\r\n"));

        CollectionAssert.AreEqual(
            new[] { "exit 0" },
            result.Select(item => item.Text).ToArray());
    }

    [TestMethod]
    public void Feed_TreatsSgrBetweenCarriageReturnAndLineFeedAsCrlf()
    {
        VtTextExtractor extractor = new(viewportColumns: 20);

        IReadOnlyList<ExtractedText> result = extractor.Feed(
            Encoding.UTF8.GetBytes("Warning text        \r\u001b[m\ncontinues here\r\n"));

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Warning text continues here", result[0].Text);
    }

    [TestMethod]
    public void Feed_RejoinsCrLfAtRightMarginButDoesNotJoinNextPrimaryPrompt()
    {
        VtTextExtractor extractor = new(viewportColumns: 20);

        IReadOnlyList<ExtractedText> result = extractor.Feed(Encoding.UTF8.GetBytes(
            "The operation faile\r\nd because input was invalid.\r\n" +
            "PS C:\\work>\r\n"));

        CollectionAssert.AreEqual(
            new[] { "The operation failed because input was invalid.", "PS C:\\work>" },
            result.Select(item => item.Text).ToArray());
    }

    [TestMethod]
    public void Feed_RejoinsRightMarginPromptAfterViewportScrollCursorPositioning()
    {
        VtTextExtractor extractor = new(viewportColumns: 60);
        _ = extractor.Feed(Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Repeat("completed output\r\n", 25))));
        string firstPromptPart = "PS D:\\" + new string('p', 53) + ".";

        IReadOnlyList<ExtractedText> result = extractor.Feed(Encoding.UTF8.GetBytes(
            firstPromptPart + "\r\n\u001b[19;60H.tail>\r\n"));

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(firstPromptPart + "tail>", result[0].Text);
    }

    [TestMethod]
    public void FlushIdle_RejoinsRightMarginPromptTailWithoutTrailingNewline()
    {
        VtTextExtractor extractor = new(viewportColumns: 60);
        string firstPromptPart = "PS D:\\Projects\\Terminal Translator\\tests\\TerminalTranslator.";

        IReadOnlyList<ExtractedText> immediate = extractor.Feed(Encoding.UTF8.GetBytes(
            firstPromptPart + "\r\n\u001b[19;60H.Cli.Tests\\bin\\Debug\\net10.0>\u001b[1C"));
        IReadOnlyList<ExtractedText> idle = extractor.FlushIdle();

        Assert.AreEqual(0, immediate.Count);
        Assert.AreEqual(1, idle.Count);
        Assert.AreEqual(
            "PS D:\\Projects\\Terminal Translator\\tests\\TerminalTranslator.Cli.Tests\\bin\\Debug\\net10.0>",
            idle[0].Text);
        Assert.AreEqual(SourceBoundary.IdlePrompt, idle[0].Boundary);
    }
}
