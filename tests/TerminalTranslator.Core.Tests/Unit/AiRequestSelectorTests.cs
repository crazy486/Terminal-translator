using System.Text;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class AiRequestSelectorTests
{
    [TestMethod]
    public void Select_HandlesUnderExactOverAndIndependentAiState()
    {
        AssistanceRequest basis = Request("line one\nline two\nline three");
        int required = AssistanceRequestVariableInputSerializer.GetRequiredBytes(basis);
        int output = Encoding.UTF8.GetByteCount(basis.SelectedOutput);

        AiSelectionResult under = AiRequestSelector.Select(basis, new AiInputBudgetPolicy(required + output + 1));
        AiSelectionResult exact = AiRequestSelector.Select(basis, new AiInputBudgetPolicy(required + output));
        AiSelectionResult over = AiRequestSelector.Select(basis, new AiInputBudgetPolicy(required + 18));

        Assert.AreEqual(AiInputCompleteness.Complete, under.Request!.AiCompleteness);
        Assert.AreEqual(AiInputCompleteness.Complete, exact.Request!.AiCompleteness);
        Assert.AreEqual(AiInputCompleteness.AiHeadTail, over.Request!.AiCompleteness);
        Assert.AreEqual(LocalCaptureCompleteness.Complete, over.Request.LocalCompleteness);
        Assert.IsLessThanOrEqualTo(over.TotalVariableBytes, required + 18);
    }

    [TestMethod]
    public void Select_CountsCommandQuestionEquivalentAndTerminationAndFailsRequiredOverflow()
    {
        AssistanceRequest request = Request("output") with { };
        int required = AssistanceRequestVariableInputSerializer.GetRequiredBytes(request);
        Assert.IsFalse(AiRequestSelector.Select(request, new AiInputBudgetPolicy(required - 1)).Supported);
        Assert.IsTrue(AiRequestSelector.Select(request, new AiInputBudgetPolicy(required + 6)).Supported);
        Assert.IsGreaterThan(Encoding.UTF8.GetByteCount(request.CommandText), required);
    }

    private static AssistanceRequest Request(string output) => AssistanceRequest.CreateLastTranslation(
        "dotnet build --no-restore", output, new TerminationFacts(1, true),
        LocalCaptureCompleteness.Complete, Encoding.UTF8.GetByteCount(output));
}
