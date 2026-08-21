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
    [DataRow("The build failed because a required configuration file is missing.", TranslationPriority.High)]
    [DataRow("The configuration file is missing and the application cannot start.", TranslationPriority.Normal)]
    [DataRow("The build may fail because the configuration is incomplete.", TranslationPriority.Normal)]
    [DataRow("ERROR: The application failed to start because the required configuration file could not be found.", TranslationPriority.High)]
    public void Classify_AcceptsManualAcceptanceMessages(string text, TranslationPriority expectedPriority)
    {
        CandidateClassification classification = Classify(text);

        Assert.IsTrue(classification.IsEligible);
        Assert.AreEqual(expectedPriority, classification.Priority);
    }

    [TestMethod]
    [DataRow("\u5904\u7406\u5df2\u5b8c\u6210\u3002")]
    [DataRow("C:\\src\\project\\Program.cs")]
    [DataRow("npm install --save-dev package")]
    [DataRow("const value = await client.SendAsync(request);")]
    [DataRow("v10.0.400")]
    [DataRow("Status")]
    public void Classify_RejectsLowValueOrTechnicalText(string text) => Assert.IsFalse(Classify(text).IsEligible);

    [TestMethod]
    [DataRow("> Write-Output \"The build failed because configuration is missing.\"")]
    [DataRow(">> > Write-Error \"The build failed because configuration is missing.\"")]
    [DataRow("> git status")]
    [DataRow("Translation enabled.")]
    public void Classify_RejectsPowerShellCommandEchoAndTranslatorStatus(string text) =>
        Assert.IsFalse(Classify(text).IsEligible);

    [TestMethod]
    public void Classify_AcceptsPowerShellErrorRecordEvenWhenItStartsWithCommandName()
    {
        const string errorRecord =
            "Write-Error 'The configuration file is missing.'\n" +
            " : The configuration file is missing.\n" +
            "    + CategoryInfo : NotSpecified: (:) [Write-Error], WriteErrorException\n" +
            "    + FullyQualifiedErrorId : Microsoft.PowerShell.Commands.WriteErrorException";

        CandidateClassification classification = Classify(errorRecord);

        Assert.IsTrue(classification.IsEligible);
        Assert.AreEqual(TranslationPriority.High, classification.Priority);
    }

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
