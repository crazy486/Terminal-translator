using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Cli.Commands;

public sealed class InlineAssistanceRenderer(TextWriter output)
{
    public void RenderAnswer(AskAssistanceOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Failure != AssistanceFailureKind.None)
        {
            output.WriteLine(FailureMessage(outcome.Failure));
            return;
        }

        AssistanceRequest request = outcome.Request!;
        if (request.Kind == AssistanceRequestKind.QuestionWithPreviousCommand)
            WriteContextDisclosures(request);
        output.WriteLine(outcome.Result!.Answer);
    }

    public void Render(LastAssistanceOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Failure != AssistanceFailureKind.None)
        {
            output.WriteLine(FailureMessage(outcome.Failure));
            return;
        }

        AssistanceRequest request = outcome.Request!;
        WriteContextDisclosures(request);
        output.WriteLine("[翻译]");
        output.WriteLine(outcome.Result!.Translation);
        output.WriteLine();
        output.WriteLine("[建议]");
        output.WriteLine(outcome.Result.Recommendation);
    }

    private void WriteContextDisclosures(AssistanceRequest request)
    {
        if (request.LocalCompleteness == LocalCaptureCompleteness.LocalHeadTail)
            output.WriteLine("[tt] 上一条输出超过本地 Capture 保留上限，中间部分未被保留。");
        if (request.AiCompleteness == AiInputCompleteness.AiHeadTail)
            output.WriteLine("[tt] 输出超过本次 AI 输入上限，模型仅收到开头和结尾。");
        if (request.Termination.WasInterrupted)
            output.WriteLine("[tt] 上一条命令被中断；结果仅基于可靠捕获的部分输出。");
        if (request.LocalCompleteness != LocalCaptureCompleteness.Complete ||
            request.AiCompleteness != AiInputCompleteness.Complete || request.Termination.WasInterrupted)
            output.WriteLine();
    }

    private static string FailureMessage(AssistanceFailureKind failure) => failure switch
    {
        AssistanceFailureKind.NoPreviousCommand => "No previous command output is available.",
        AssistanceFailureKind.NoOutput => "Previous command has no translatable output.",
        AssistanceFailureKind.CaptureDisabled => "tt last capture is disabled.\nEnable it to capture future command output.",
        AssistanceFailureKind.CaptureUnavailable => "[tt] Capture is unavailable.",
        AssistanceFailureKind.UnreliableOrCorrupt => "[tt] Previous command output could not be recovered reliably.",
        AssistanceFailureKind.NoTranslatableEnglish => "No translatable English content was found.",
        AssistanceFailureKind.RequiredContextTooLarge => "[tt] Required context exceeds the AI input limit.",
        AssistanceFailureKind.ConsentMissingOrDeclined => "[tt] External transmission was not authorized; no content was sent.",
        AssistanceFailureKind.SuspectedSecret => "[tt] Suspected sensitive information was detected; no content was sent.",
        AssistanceFailureKind.ProviderTimeout => "[tt] Assistance provider timed out.",
        AssistanceFailureKind.ProviderNetworkFailure => "[tt] Assistance provider network request failed.",
        AssistanceFailureKind.ProviderHttpFailure => "[tt] Assistance provider returned an HTTP error.",
        AssistanceFailureKind.ProviderMalformedResponse => "[tt] Assistance provider returned an invalid response.",
        _ => "[tt] Assistance provider request failed.",
    };
}
