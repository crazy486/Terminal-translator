using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Core.Assistance;

public sealed class AskAssistanceCoordinator(
    IAssistanceProvider provider,
    IAssistanceRequestAuthorizer authorizer,
    AiInputBudgetPolicy? inputBudget = null,
    int? adapterCapabilityBytes = null)
{
    public async Task<AskAssistanceOutcome> ExecuteAsync(string question, CancellationToken cancellationToken)
    {
        AssistanceRequest request = AssistanceRequest.CreateQuestionOnly(
            question,
            ResponseLanguagePolicy.Select(question));
        AiSelectionResult selection = AiRequestSelector.Select(
            request,
            inputBudget ?? AiInputBudgetPolicy.V1Default,
            adapterCapabilityBytes);
        if (!selection.Supported) return new(AssistanceFailureKind.RequiredContextTooLarge);
        request = selection.Request!;

        try
        {
            AssistanceAuthorizationDecision authorization =
                await authorizer.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
            if (authorization.Failure != AssistanceAuthorizationFailure.None)
                return new(MapAuthorizationFailure(authorization.Failure), request);
            AssistanceResult result = await provider.CompleteAsync(
                authorization.Request!, cancellationToken).ConfigureAwait(false);
            return AskAssistanceOutcome.Success(request, result);
        }
        catch (TranslationProviderException exception)
        {
            return new(AssistanceProviderFailureMapper.Map(exception), request);
        }
    }

    private static AssistanceFailureKind MapAuthorizationFailure(AssistanceAuthorizationFailure failure) => failure switch
    {
        AssistanceAuthorizationFailure.SuspectedSecret => AssistanceFailureKind.SuspectedSecret,
        _ => AssistanceFailureKind.ConsentMissingOrDeclined,
    };
}
