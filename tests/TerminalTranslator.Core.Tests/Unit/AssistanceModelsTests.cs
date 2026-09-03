using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class AssistanceModelsTests
{
    [TestMethod]
    public void LastTranslationRequest_PreservesProviderNeutralContextAndIndependentCompleteness()
    {
        AssistanceRequest request = AssistanceRequest.CreateLastTranslation(
            "dotnet build",
            "Build FAILED.",
            new TerminationFacts(1, true),
            LocalCaptureCompleteness.LocalHeadTail,
            20_000_000,
            AiInputCompleteness.AiHeadTail);

        Assert.AreEqual(AssistanceRequestKind.LastTranslation, request.Kind);
        Assert.AreEqual("dotnet build", request.CommandText);
        Assert.AreEqual("Build FAILED.", request.SelectedOutput);
        Assert.IsTrue(request.Termination.WasInterrupted);
        Assert.AreEqual(LocalCaptureCompleteness.LocalHeadTail, request.LocalCompleteness);
        Assert.AreEqual(AiInputCompleteness.AiHeadTail, request.AiCompleteness);
    }

    [TestMethod]
    public void Result_IsDisplayOnlyStructuredText()
    {
        AssistanceResult result = new("构建失败。", "检查配置后重新运行 dotnet build。", "request-1");

        Assert.AreEqual("构建失败。", result.Translation);
        Assert.AreEqual("检查配置后重新运行 dotnet build。", result.Recommendation);
        Assert.IsFalse(typeof(AssistanceResult).GetMethods().Any(method =>
            method.Name.Contains("Execute", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Start", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void TerminationFacts_SerializeUnknownNativeExitWithoutInventingZeroOrOne()
    {
        TerminationFacts facts = new(PowerShellSucceeded: true, NativeExitCode: null, WasInterrupted: false);
        Assert.AreEqual(
            "powerShellSucceeded=true; nativeExitCode=unknown; interrupted=false",
            facts.ToProviderText());
        Assert.IsFalse(facts.ToProviderText().Contains("nativeExitCode=5", StringComparison.Ordinal));
    }
}
