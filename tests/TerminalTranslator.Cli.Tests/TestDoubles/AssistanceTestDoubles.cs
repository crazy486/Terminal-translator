using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Privacy;

namespace TerminalTranslator.Cli.Tests.TestDoubles;

internal sealed class RecordingCall<TRequest, TResult>(Func<TRequest, TResult> handler)
{
    public List<TRequest> Requests { get; } = [];

    public TResult Invoke(TRequest request)
    {
        Requests.Add(request);
        return handler(request);
    }
}

internal sealed class RejectIfCalled(string boundary)
{
    public void Invoke() => throw new AssertFailedException($"{boundary} must not be accessed.");
}

public sealed class ApprovedAssistanceRequestAuthorizer(SecretDetector? detector = null) : IAssistanceRequestAuthorizer
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

public sealed class RecordingAssistanceProvider : IAssistanceProvider
{
    public List<AuthorizedAssistanceRequest> Requests { get; } = [];
    public AssistanceResult Result { get; set; } = new("翻译", "建议");

    public Task<AssistanceResult> CompleteAsync(AuthorizedAssistanceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}

public sealed class RejectIfCalledAssistanceProvider : IAssistanceProvider
{
    public Task<AssistanceResult> CompleteAsync(AuthorizedAssistanceRequest request, CancellationToken cancellationToken) =>
        throw new AssertFailedException("Provider must not be called.");
}
