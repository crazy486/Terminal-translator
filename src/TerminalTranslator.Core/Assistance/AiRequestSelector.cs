using System.Text;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Assistance;

public static class AssistanceRequestVariableInputSerializer
{
    public static string Serialize(AssistanceRequest request) => request.Kind switch
    {
        AssistanceRequestKind.LastTranslation =>
            $"Command context (do not translate):\n{request.CommandText}\n\nTermination facts:\n{request.Termination.ToProviderText()}\n\n" +
            $"Selected ordered output:\n{request.SelectedOutput}\n\nInput completeness: {request.AiCompleteness}; local completeness: {request.LocalCompleteness}.",
        AssistanceRequestKind.QuestionOnly =>
            $"User question:\n{request.Question}\n\nResponse language instruction:\n{request.ResponseLanguageInstruction}",
        AssistanceRequestKind.QuestionWithPreviousCommand =>
            $"User question:\n{request.Question}\n\nResponse language instruction:\n{request.ResponseLanguageInstruction}\n\n" +
            $"Previous command context:\n{request.CommandText}\n\nTermination facts:\n{request.Termination.ToProviderText()}\n\n" +
            $"Selected ordered output:\n{request.SelectedOutput}\n\nInput completeness: {request.AiCompleteness}; local completeness: {request.LocalCompleteness}.",
        _ => throw new ArgumentOutOfRangeException(nameof(request)),
    };

    public static int GetRequiredBytes(AssistanceRequest request)
    {
        AssistanceRequest empty = request.WithSelectedOutput(string.Empty, AiInputCompleteness.AiHeadTail);
        return Encoding.UTF8.GetByteCount(Serialize(empty));
    }
}

public sealed record AiSelectionResult(bool Supported, AssistanceRequest? Request, int TotalVariableBytes);

public static class AiRequestSelector
{
    public static AiSelectionResult Select(
        AssistanceRequest request,
        AiInputBudgetPolicy policy,
        int? adapterCapabilityBytes = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        int budget = policy.EffectiveBytes(adapterCapabilityBytes);
        AssistanceRequest complete = request.WithSelectedOutput(request.SelectedOutput, AiInputCompleteness.Complete);
        int completeBytes = Encoding.UTF8.GetByteCount(AssistanceRequestVariableInputSerializer.Serialize(complete));
        if (completeBytes <= budget) return new(true, complete, completeBytes);

        AssistanceRequest empty = request.WithSelectedOutput(string.Empty, AiInputCompleteness.AiHeadTail);
        int required = Encoding.UTF8.GetByteCount(AssistanceRequestVariableInputSerializer.Serialize(empty));
        if (required > budget) return new(false, null, required);

        Utf8HeadTailSelection selected = Utf8HeadTailSelector.Select(request.SelectedOutput, budget - required);
        AssistanceRequest truncated = request.WithSelectedOutput(selected.Combined, AiInputCompleteness.AiHeadTail);
        int total = Encoding.UTF8.GetByteCount(AssistanceRequestVariableInputSerializer.Serialize(truncated));
        return total <= budget ? new(true, truncated, total) : new(false, null, total);
    }
}
