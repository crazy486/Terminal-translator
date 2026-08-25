using TerminalTranslator.Core.Capture;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Core.Assistance;

public sealed class AskLastAssistanceCoordinator
{
    private readonly Func<CancellationToken, Task<PreviousCommandResult>> _retrieve;
    private readonly IAssistanceProvider _provider;
    private readonly IAssistanceRequestAuthorizer _authorizer;
    private readonly AiInputBudgetPolicy? _inputBudget;
    private readonly int? _adapterCapabilityBytes;

    public AskLastAssistanceCoordinator(
        Func<PreviousCommandResult> retrieve,
        IAssistanceProvider provider,
        IAssistanceRequestAuthorizer authorizer,
        AiInputBudgetPolicy? inputBudget = null,
        int? adapterCapabilityBytes = null)
        : this(_ => Task.FromResult(retrieve()), provider, authorizer, inputBudget, adapterCapabilityBytes)
    {
    }

    public AskLastAssistanceCoordinator(
        Func<CancellationToken, Task<PreviousCommandResult>> retrieve,
        IAssistanceProvider provider,
        IAssistanceRequestAuthorizer authorizer,
        AiInputBudgetPolicy? inputBudget = null,
        int? adapterCapabilityBytes = null)
    {
        _retrieve = retrieve ?? throw new ArgumentNullException(nameof(retrieve));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _inputBudget = inputBudget;
        _adapterCapabilityBytes = adapterCapabilityBytes;
    }

    public async Task<AskAssistanceOutcome> ExecuteAsync(string question, CancellationToken cancellationToken)
    {
        PreviousCommandResult retrieval = await _retrieve(cancellationToken).ConfigureAwait(false);
        AssistanceFailureKind failure = retrieval.Kind switch
        {
            PreviousCommandResultKind.NoPreviousCommand => AssistanceFailureKind.NoPreviousCommand,
            PreviousCommandResultKind.NoOutput => AssistanceFailureKind.NoOutput,
            PreviousCommandResultKind.CaptureDisabled => AssistanceFailureKind.CaptureDisabled,
            PreviousCommandResultKind.CaptureUnavailable => AssistanceFailureKind.CaptureUnavailable,
            PreviousCommandResultKind.UnreliableOrCorrupt => AssistanceFailureKind.UnreliableOrCorrupt,
            _ => AssistanceFailureKind.None,
        };
        if (failure != AssistanceFailureKind.None) return new(failure);

        PreviousCommandSnapshot snapshot = retrieval.Snapshot!;
        AssistanceRequest request = AssistanceRequest.CreateQuestionWithPreviousCommand(
            question,
            ResponseLanguagePolicy.Select(question),
            snapshot.CommandText,
            snapshot.Output,
            new TerminationFacts(snapshot.ExitCode, snapshot.WasInterrupted),
            snapshot.LocalCompleteness,
            snapshot.OriginalOutputBytes);
        AiSelectionResult selection = AiRequestSelector.Select(
            request,
            _inputBudget ?? AiInputBudgetPolicy.V1Default,
            _adapterCapabilityBytes);
        if (!selection.Supported) return new(AssistanceFailureKind.RequiredContextTooLarge);
        request = selection.Request!;

        try
        {
            AssistanceAuthorizationDecision authorization =
                await _authorizer.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
            if (authorization.Failure != AssistanceAuthorizationFailure.None)
                return new(MapAuthorizationFailure(authorization.Failure), request);
            AssistanceResult result = await _provider.CompleteAsync(
                authorization.Request!, cancellationToken).ConfigureAwait(false);
            return AskAssistanceOutcome.Success(request, result);
        }
        catch (TranslationProviderException exception)
        {
            return new(exception.Code == TranslationErrorCode.Timeout
                ? AssistanceFailureKind.ProviderTimeout
                : AssistanceFailureKind.ProviderError, request);
        }
    }

    private static AssistanceFailureKind MapAuthorizationFailure(AssistanceAuthorizationFailure failure) => failure switch
    {
        AssistanceAuthorizationFailure.SuspectedSecret => AssistanceFailureKind.SuspectedSecret,
        _ => AssistanceFailureKind.ConsentMissingOrDeclined,
    };
}
