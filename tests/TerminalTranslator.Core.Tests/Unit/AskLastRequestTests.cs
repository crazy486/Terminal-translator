using System.Text;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class AskLastRequestTests
{
    [TestMethod]
    public void SelectionCarriesQuestionContextFactsLanguageAndNoConversationIdentity()
    {
        AssistanceRequest request = AssistanceRequest.CreateQuestionWithPreviousCommand(
            "为什么失败？",
            ResponseLanguagePolicy.Select("为什么失败？"),
            "dotnet build",
            "first stdout\nthen stderr",
            new TerminationFacts(1, true),
            LocalCaptureCompleteness.Complete,
            Encoding.UTF8.GetByteCount("first stdout\nthen stderr"));

        AiSelectionResult selected = AiRequestSelector.Select(request, AiInputBudgetPolicy.V1Default);

        Assert.IsTrue(selected.Supported);
        Assert.AreEqual(AssistanceRequestKind.QuestionWithPreviousCommand, selected.Request!.Kind);
        Assert.AreEqual("为什么失败？", selected.Request.Question);
        Assert.AreEqual("Simplified Chinese", selected.Request.ResponseLanguage);
        Assert.AreEqual("dotnet build", selected.Request.CommandText);
        Assert.AreEqual("first stdout\nthen stderr", selected.Request.SelectedOutput);
        Assert.AreEqual(1, selected.Request.Termination.ExitCode);
        Assert.IsTrue(selected.Request.Termination.WasInterrupted);
        Assert.IsFalse(typeof(AssistanceRequest).GetProperties().Any(property =>
            property.Name.Contains("Conversation", StringComparison.OrdinalIgnoreCase)));
    }
}
