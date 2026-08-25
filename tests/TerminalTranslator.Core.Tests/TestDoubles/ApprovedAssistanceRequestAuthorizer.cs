using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Privacy;

namespace TerminalTranslator.Core.Tests.TestDoubles;

internal sealed class ApprovedAssistanceRequestAuthorizer(SecretDetector? detector = null) : IAssistanceRequestAuthorizer
{
    private readonly AssistancePrivacyGate _gate = new(detector ?? new SecretDetector());

    public Task<AssistanceAuthorizationDecision> AuthorizeAsync(
        AssistanceRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string scope = AssistanceConsentScopes.For(request.Kind);
        MatchingConsentAssertion consent = MatchingConsentAssertion.TryCreate("TEST-GRANT", "TEST-GRANT", scope)!;
        AuthorizedAssistanceRequest? authorized = _gate.Authorize(request, consent);
        return Task.FromResult(authorized is null
            ? AssistanceAuthorizationDecision.PrivacyBlocked()
            : AssistanceAuthorizationDecision.Authorized(authorized));
    }
}
