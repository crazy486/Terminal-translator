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
    public void Classify_RejectsCommandShapedTechnicalText(string text) =>
        Assert.IsFalse(Classify(text).IsEligible);

    [TestMethod]
    [DataRow("Translation enabled.")]
    [DataRow("Arbitrary future control-plane output remains English.")]
    public void Classify_DoesNotOwnTerminalTranslatorControlFiltering(string text) =>
        Assert.IsTrue(Classify(text).IsEligible);

    [TestMethod]
    [DataRow("The build output contains PS D:\\Projects\\Terminal Translator> as diagnostic text.")]
    [DataRow("The ordinary English stdout remains available after the prompt.")]
    public void Classify_PromptFilteringDoesNotRejectRealOutput(string text) =>
        Assert.IsTrue(Classify(text).IsEligible);

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
    [DataRow("Get-ChildItem : Cannot find path 'X' because it does not exist.")]
    [DataRow("npm ERR! missing script: build")]
    [DataRow("npm ERR! code ENOENT")]
    [DataRow("git: 'foo' is not a git command. See 'git --help'.")]
    [DataRow("git error: failed to push some refs to 'origin'")]
    [DataRow("python: can't open file 'test.py': [Errno 2] No such file or directory")]
    public void Classify_AcceptsCommandShapedErrorsWhenProgramOwnershipIsEstablished(string text) =>
        Assert.IsTrue(_classifier.Classify(
            text,
            new LayoutHints(1, [0]),
            SourceBoundary.Line,
            programOutputOwnershipEstablished: true).IsEligible);

    [TestMethod]
    [DataRow("Get-ChildItem -Force")]
    [DataRow("npm install")]
    [DataRow("git status")]
    [DataRow("> tt status")]
    public void Classify_RejectsCompleteCommandsWhenProgramOwnershipIsEstablished(string text) =>
        Assert.IsFalse(_classifier.Classify(
            text,
            new LayoutHints(1, [0]),
            SourceBoundary.Line,
            programOutputOwnershipEstablished: true).IsEligible);

    [TestMethod]
    [DataRow("处理已完成。", false)]
    [DataRow("构建已经完成 build ok", false)]
    [DataRow("ERROR 构建失败 because package is missing", true)]
    public void Classify_AppliesCjkToEnglishRatio(string text, bool expectedEligible) =>
        Assert.AreEqual(expectedEligible, Classify(text).IsEligible);

    [TestMethod]
    public void Classify_CjkRangeBoundsParticipateInRatio()
    {
        string lowerBoundDominant = new string('\u4E00', 8) + " build ok";
        string upperBoundDominant = new string('\u9FFF', 8) + " build ok";

        Assert.IsFalse(Classify(lowerBoundDominant).IsEligible);
        Assert.IsFalse(Classify(upperBoundDominant).IsEligible);
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
