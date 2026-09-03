using TerminalTranslator.Core.Capture;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Core.Parsing;

namespace TerminalTranslator.Core.Assistance;

public sealed class LastAssistanceCoordinator
{
    private readonly Func<CancellationToken, Task<PreviousCommandResult>> _retrieve;
    private readonly IAssistanceProvider _provider;
    private readonly IAssistanceRequestAuthorizer _authorizer;
    private readonly AiInputBudgetPolicy? _inputBudget;
    private readonly int? _adapterCapabilityBytes;
    private readonly Func<string, bool>? _isEligible;
    private readonly IAssistancePipelineDiagnosticSink _diagnostics;

    public LastAssistanceCoordinator(
        Func<PreviousCommandResult> retrieve,
        IAssistanceProvider provider,
        IAssistanceRequestAuthorizer authorizer,
        AiInputBudgetPolicy? inputBudget = null,
        int? adapterCapabilityBytes = null,
        Func<string, bool>? isEligible = null,
        IAssistancePipelineDiagnosticSink? diagnostics = null)
        : this(_ => Task.FromResult(retrieve()), provider, authorizer, inputBudget, adapterCapabilityBytes, isEligible, diagnostics)
    {
    }

    public LastAssistanceCoordinator(
        Func<CancellationToken, Task<PreviousCommandResult>> retrieve,
        IAssistanceProvider provider,
        IAssistanceRequestAuthorizer authorizer,
        AiInputBudgetPolicy? inputBudget = null,
        int? adapterCapabilityBytes = null,
        Func<string, bool>? isEligible = null,
        IAssistancePipelineDiagnosticSink? diagnostics = null)
    {
        _retrieve = retrieve ?? throw new ArgumentNullException(nameof(retrieve));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _inputBudget = inputBudget;
        _adapterCapabilityBytes = adapterCapabilityBytes;
        _isEligible = isEligible;
        _diagnostics = diagnostics ?? NullAssistancePipelineDiagnosticSink.Instance;
    }

    public async Task<LastAssistanceOutcome> ExecuteAsync(CancellationToken cancellationToken)
    {
        PreviousCommandResult retrieval = await _retrieve(cancellationToken).ConfigureAwait(false);
        WriteDiagnostic(CreateRetrievalDiagnostic(retrieval));
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
        bool eligible = (_isEligible ?? WholeOutputEligibility.HasTranslatableEnglish)(snapshot.Output);
        WriteDiagnostic(new(
            AssistancePipelineStage.Eligibility,
            retrieval.Kind,
            RetrievedOutputUtf8Bytes: System.Text.Encoding.UTF8.GetByteCount(snapshot.Output),
            EnglishEligible: eligible));
        if (!eligible)
            return new(AssistanceFailureKind.NoTranslatableEnglish);

        AssistanceRequest request = AssistanceRequest.CreateLastTranslation(
            snapshot.CommandText,
            snapshot.Output,
            new TerminationFacts(snapshot.PowerShellSucceeded, snapshot.NativeExitCode, snapshot.WasInterrupted),
            snapshot.LocalCompleteness,
            snapshot.OriginalOutputBytes,
            AiInputCompleteness.Complete);
        AiSelectionResult selection = AiRequestSelector.Select(request, _inputBudget ?? AiInputBudgetPolicy.V1Default, _adapterCapabilityBytes);
        WriteDiagnostic(new(
            AssistancePipelineStage.Selection,
            retrieval.Kind,
            RetrievedOutputUtf8Bytes: System.Text.Encoding.UTF8.GetByteCount(snapshot.Output),
            SelectionSupported: selection.Supported,
            SelectedOutputUtf8Bytes: selection.Request is null
                ? null
                : System.Text.Encoding.UTF8.GetByteCount(selection.Request.SelectedOutput)));
        if (!selection.Supported) return new(AssistanceFailureKind.RequiredContextTooLarge);
        request = selection.Request!;
        try
        {
            AssistanceAuthorizationDecision authorization =
                await _authorizer.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
            if (authorization.Failure != AssistanceAuthorizationFailure.None)
                return new(MapAuthorizationFailure(authorization.Failure), request);
            AssistanceResult result = await _provider.CompleteAsync(authorization.Request!, cancellationToken).ConfigureAwait(false);
            return new(AssistanceFailureKind.None, request, result);
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

    private static AssistancePipelineDiagnostic CreateRetrievalDiagnostic(PreviousCommandResult retrieval)
    {
        string? output = retrieval.Snapshot?.Output;
        return new(
            AssistancePipelineStage.Retrieval,
            retrieval.Kind,
            RetrievedOutputUtf8Bytes: output is null ? null : System.Text.Encoding.UTF8.GetByteCount(output),
            AsciiLetterCount: output?.Count(character => character <= 0x7f && char.IsLetter(character)),
            ControlCharacterCount: output?.Count(character =>
                char.IsControl(character) && character is not '\r' and not '\n' and not '\t'));
    }

    private void WriteDiagnostic(AssistancePipelineDiagnostic diagnostic)
    {
        try
        {
            _diagnostics.Write(diagnostic);
        }
        catch (Exception)
        {
            // Optional content-free diagnostics must never alter assistance behavior.
        }
    }
}
