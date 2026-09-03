using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class InlineAssistanceRendererTests
{
    [TestMethod]
    public void RenderAnswer_IsDirectAndDisclosesContextReductionsWithoutLastHeadings()
    {
        using StringWriter output = new();
        AssistanceRequest request = AssistanceRequest.CreateQuestionWithPreviousCommand(
            "why?", ResponseLanguagePolicy.Select("why?"), "build", "headtail",
            new TerminationFacts(1, false), LocalCaptureCompleteness.LocalHeadTail, 20_000_000,
            AiInputCompleteness.AiHeadTail);

        new InlineAssistanceRenderer(output).RenderAnswer(AskAssistanceOutcome.Success(
            request, AssistanceResult.CreateAnswer("直接回答；git status 仅显示。")));

        string rendered = output.ToString();
        StringAssert.Contains(rendered, "本地 Capture 保留上限");
        StringAssert.Contains(rendered, "AI 输入上限");
        StringAssert.Contains(rendered, "直接回答；git status 仅显示。");
        Assert.IsFalse(rendered.Contains("[翻译]", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("[建议]", StringComparison.Ordinal));
    }
    [TestMethod]
    public void Render_SuccessIsInlineDoesNotReplaySourceAndLeavesSnippetInert()
    {
        using StringWriter output = new();
        InlineAssistanceRenderer renderer = new(output);
        AssistanceRequest request = AssistanceRequest.CreateLastTranslation("build", "SECRET SOURCE TEXT", new TerminationFacts(1, false));

        renderer.Render(new LastAssistanceOutcome(
            AssistanceFailureKind.None,
            request,
            new AssistanceResult("构建失败。", "运行 rm -rf sample 之前先检查路径。")));

        string text = output.ToString();
        StringAssert.Contains(text, "[翻译]");
        StringAssert.Contains(text, "[建议]");
        StringAssert.Contains(text, "rm -rf sample");
        Assert.IsFalse(text.Contains("SECRET SOURCE TEXT", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Render_UsesStableContentSafeFailureMessages()
    {
        Dictionary<AssistanceFailureKind, string> expected = new()
        {
            [AssistanceFailureKind.NoPreviousCommand] = "No previous command output is available.",
            [AssistanceFailureKind.NoOutput] = "Previous command has no translatable output.",
            [AssistanceFailureKind.CaptureDisabled] = "tt last capture is disabled.",
            [AssistanceFailureKind.CaptureUnavailable] = "[tt] Capture is unavailable.",
            [AssistanceFailureKind.UnreliableOrCorrupt] = "[tt] Previous command output could not be recovered reliably.",
            [AssistanceFailureKind.ProviderTimeout] = "[tt] Assistance provider timed out.",
            [AssistanceFailureKind.ProviderNetworkFailure] = "[tt] Assistance provider network request failed.",
            [AssistanceFailureKind.ProviderHttpFailure] = "[tt] Assistance provider returned an HTTP error.",
            [AssistanceFailureKind.ProviderMalformedResponse] = "[tt] Assistance provider returned an invalid response.",
            [AssistanceFailureKind.ProviderError] = "[tt] Assistance provider request failed.",
            [AssistanceFailureKind.NoTranslatableEnglish] = "No translatable English content was found.",
        };
        foreach ((AssistanceFailureKind kind, string message) in expected)
        {
            using StringWriter output = new();
            new InlineAssistanceRenderer(output).Render(new LastAssistanceOutcome(kind));
            StringAssert.Contains(output.ToString(), message);
        }
    }

    [TestMethod]
    public void Render_DisclosesLocalAiBothAndInterruptedStatesIndependently()
    {
        AssertNotice(LocalCaptureCompleteness.LocalHeadTail, AiInputCompleteness.Complete, false,
            "超过本地 Capture 保留上限", absent: "AI 输入上限");
        AssertNotice(LocalCaptureCompleteness.Complete, AiInputCompleteness.AiHeadTail, false,
            "超过本次 AI 输入上限", absent: "本地 Capture 保留上限");

        using StringWriter bothOutput = new();
        AssistanceRequest both = AssistanceRequest.CreateLastTranslation(
            "build", "headtail", new TerminationFacts(1, true),
            LocalCaptureCompleteness.LocalHeadTail, 20_000_000, AiInputCompleteness.AiHeadTail);
        new InlineAssistanceRenderer(bothOutput).Render(new(
            AssistanceFailureKind.None, both, new AssistanceResult("摘要翻译", "建议")));
        string rendered = bothOutput.ToString();
        StringAssert.Contains(rendered, "超过本地 Capture 保留上限");
        StringAssert.Contains(rendered, "超过本次 AI 输入上限");
        StringAssert.Contains(rendered, "上一条命令被中断");
        StringAssert.Contains(rendered, "摘要");
    }

    private static void AssertNotice(
        LocalCaptureCompleteness local,
        AiInputCompleteness ai,
        bool interrupted,
        string expected,
        string absent)
    {
        using StringWriter output = new();
        AssistanceRequest request = AssistanceRequest.CreateLastTranslation(
            "build", "selected", new TerminationFacts(1, interrupted), local, 100, ai);
        new InlineAssistanceRenderer(output).Render(new(
            AssistanceFailureKind.None, request, new AssistanceResult("翻译", "建议")));
        StringAssert.Contains(output.ToString(), expected);
        Assert.IsFalse(output.ToString().Contains(absent, StringComparison.Ordinal));
    }
}
