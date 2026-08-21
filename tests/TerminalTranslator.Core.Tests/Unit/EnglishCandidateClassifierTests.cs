using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Parsing;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class EnglishCandidateClassifierTests
{
    private readonly EnglishCandidateClassifier _classifier = new();

    [TestMethod]
    [DataRow("The operation completed successfully.")]
    [DataRow("Unable to open the requested file because access was denied.")]
    [DataRow("Continue with the operation? [y/N]")]
    public void Classify_AcceptsUsefulEnglish(string text) => Assert.IsTrue(Classify(text).IsEligible);

    [TestMethod]
    [DataRow("\u5904\u7406\u5df2\u5b8c\u6210\u3002")]
    [DataRow("C:\\src\\project\\Program.cs")]
    [DataRow("npm install --save-dev package")]
    [DataRow("const value = await client.SendAsync(request);")]
    [DataRow("v10.0.400")]
    [DataRow("Status")]
    public void Classify_RejectsLowValueOrTechnicalText(string text) => Assert.IsFalse(Classify(text).IsEligible);

    [TestMethod]
    [DataRow("Continue? [y/N]")]
    [DataRow("Error: the requested operation failed.")]
    public void Classify_PrioritizesPromptsAndErrors(string text) =>
        Assert.AreEqual(TranslationPriority.High, Classify(text).Priority);

    [TestMethod]
    public void Classify_ProducesStableNormalizedDeduplicationKey()
    {
        CandidateClassification first = Classify("  File NOT found.  ");
        CandidateClassification second = Classify("file not found.");
        Assert.AreEqual(first.DeduplicationKey, second.DeduplicationKey);
        Assert.AreEqual(1, first.Layout.LineCount);
    }

    private CandidateClassification Classify(string text) =>
        _classifier.Classify(text, new LayoutHints(1, [0]), SourceBoundary.Line);
}
