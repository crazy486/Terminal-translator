using TerminalTranslator.Core.Assistance;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class QuestionOnlyRequestTests
{
    [TestMethod]
    public void QuestionOnly_DefaultsToSimplifiedChineseAndContainsNoContextOrConversationIdentity()
    {
        const string question = "Explain detached HEAD";
        AssistanceRequest request = AssistanceRequest.CreateQuestionOnly(
            question,
            ResponseLanguagePolicy.Select(question));

        Assert.AreEqual(AssistanceRequestKind.QuestionOnly, request.Kind);
        Assert.AreEqual(question, request.Question);
        Assert.AreEqual("Simplified Chinese", request.ResponseLanguage);
        Assert.AreEqual(string.Empty, request.CommandText);
        Assert.AreEqual(string.Empty, request.SelectedOutput);
        Assert.IsFalse(typeof(AssistanceRequest).GetProperties().Any(property =>
            property.Name.Contains("Conversation", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("History", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Session", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    [DataRow("Please answer in English: what is detached HEAD?", "English")]
    [DataRow("Answer this question in English: what is detached HEAD?", "English")]
    [DataRow("请用英文回答：什么是 detached HEAD？", "English")]
    [DataRow("用日语解释这个错误", "Japanese")]
    public void ExplicitLanguageRequestOverridesDefault(string question, string expected)
    {
        Assert.AreEqual(expected, ResponseLanguagePolicy.Select(question).Language);
    }

    [TestMethod]
    public void EnglishQuestionWithoutExplicitLanguageRequestStillDefaultsToSimplifiedChinese()
    {
        Assert.AreEqual("Simplified Chinese", ResponseLanguagePolicy.Select("What is detached HEAD?").Language);
    }
}
