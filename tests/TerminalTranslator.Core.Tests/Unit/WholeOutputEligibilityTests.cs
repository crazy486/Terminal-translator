using TerminalTranslator.Core.Parsing;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class WholeOutputEligibilityTests
{
    [TestMethod]
    public void TinyEnglishEnglishOnlyAndMixedAreEligible()
    {
        Assert.IsTrue(WholeOutputEligibility.HasTranslatableEnglish("Failed"));
        Assert.IsTrue(WholeOutputEligibility.HasTranslatableEnglish("The build failed because configuration is missing."));
        Assert.IsTrue(WholeOutputEligibility.HasTranslatableEnglish("构建失败: package was not found"));
    }

    [TestMethod]
    public void ChineseOnlyAndTechnicalOnlyAreNotEligible()
    {
        Assert.IsFalse(WholeOutputEligibility.HasTranslatableEnglish("构建已完成，没有错误。"));
        foreach (string technical in new[]
        {
            @"C:\src\app.cs",
            "https://example.com/api?q=x",
            "--no-restore -v:q",
            "ERR42 E_ACCESSDENIED 0x80070005",
            "SomeIdentifier snake_case package.module Newtonsoft.Json",
            "if (x == null) { return false; }",
            "npm:@scope/package Microsoft.PowerShell.Management",
        })
            Assert.IsFalse(WholeOutputEligibility.HasTranslatableEnglish(technical), technical);
    }

    [TestMethod]
    public void GitLikeEnglishWithTechnicalTokensIsEligibleAsAWhole()
    {
        const string output = "On branch test\nChanges not staged for commit:\n  (use \"git add <file>...\" to update what will be committed)";
        Assert.IsTrue(WholeOutputEligibility.HasTranslatableEnglish(output));
    }
}
