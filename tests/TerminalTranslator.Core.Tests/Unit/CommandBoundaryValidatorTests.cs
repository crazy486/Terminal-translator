using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class CommandBoundaryValidatorTests
{
    private static readonly CaptureSessionId Session = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "nonce-b");

    [TestMethod]
    public void Validate_AcceptsMatchingVersionedCompletedBoundary()
    {
        CommandBoundaryEvidence evidence = CreateEvidence();
        BoundaryValidationResult result = CommandBoundaryValidator.Validate(evidence);

        Assert.IsTrue(result.IsReliable);
        Assert.AreEqual("line 1\r\nline 2\r\n", result.OrderedOutput);
        Assert.AreEqual(42L, result.Boundary!.Sequence);
        Assert.AreEqual(1, result.Boundary.ExitCode);
    }

    [TestMethod]
    public void Validate_RejectsMissingOrMismatchedSessionSequenceAndEcho()
    {
        Assert.IsFalse(CommandBoundaryValidator.Validate(CreateEvidence() with { HasClosingBoundary = false }).IsReliable);
        Assert.IsFalse(CommandBoundaryValidator.Validate(CreateEvidence() with
        {
            ClosingSession = new CaptureSessionId(Guid.NewGuid(), "other"),
        }).IsReliable);
        Assert.IsFalse(CommandBoundaryValidator.Validate(CreateEvidence() with { ClosingSequence = 43 }).IsReliable);
        Assert.IsFalse(CommandBoundaryValidator.Validate(CreateEvidence() with { TranscriptCommandEcho = "git log" }).IsReliable);
        Assert.IsFalse(CommandBoundaryValidator.Validate(CreateEvidence() with { FormatVersion = 99 }).IsReliable);
    }

    [TestMethod]
    public void Validate_PreservesMultilineCommandAsOneHistoryEntry()
    {
        const string multiline = "& {`n Write-Output one`n Write-Output two`n}";
        BoundaryValidationResult result = CommandBoundaryValidator.Validate(CreateEvidence() with
        {
            CommandText = multiline,
            TranscriptCommandEcho = multiline,
        });
        Assert.IsTrue(result.IsReliable);
        Assert.AreEqual(multiline, result.Boundary!.CommandText);
    }

    [TestMethod]
    public void Validate_AcceptsEmptyOutputContinuationAndCustomPromptWithoutParsingPromptText()
    {
        BoundaryValidationResult empty = CommandBoundaryValidator.Validate(CreateEvidence() with { OrderedOutput = string.Empty });
        Assert.IsTrue(empty.IsReliable);
        Assert.AreEqual(string.Empty, empty.OrderedOutput);

        const string continuation = "Get-ChildItem `\n  -Path .";
        BoundaryValidationResult multiline = CommandBoundaryValidator.Validate(CreateEvidence() with
        {
            CommandText = continuation,
            TranscriptCommandEcho = continuation,
            OrderedOutput = "custom λ> is output text, not a parsed prompt",
        });
        Assert.IsTrue(multiline.IsReliable);
        StringAssert.Contains(multiline.OrderedOutput!, "custom λ>");
    }

    [TestMethod]
    public void Validate_RejectsAmbiguousEchoAndPreEchoUnattributedContent()
    {
        Assert.IsFalse(CommandBoundaryValidator.Validate(CreateEvidence() with { CommandEchoMatchCount = 2 }).IsReliable);
        Assert.IsFalse(CommandBoundaryValidator.Validate(CreateEvidence() with { HasUnattributedContentBeforeEcho = true }).IsReliable);
    }

    private static CommandBoundaryEvidence CreateEvidence() => new(
        CommandBoundaryValidator.CurrentFormatVersion,
        Session,
        Session,
        42,
        42,
        true,
        true,
        10,
        "git status",
        "git status",
        "line 1\r\nline 2\r\n",
        1,
        false,
        true);
}
