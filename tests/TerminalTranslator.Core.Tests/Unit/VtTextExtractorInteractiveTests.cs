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
}
