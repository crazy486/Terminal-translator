using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Assistance;

namespace TerminalTranslator.Cli.Commands;

public sealed class AssistanceConsentPrompt(
    ProviderSettings settings,
    ProviderConsentGrantStore grantStore,
    AssistancePrivacyGate privacyGate,
    TextReader input,
    TextWriter output,
    string policyVersion = ProviderConsentContext.CurrentPolicyVersion) : IAssistanceRequestAuthorizer
{
    public async Task<AssistanceAuthorizationDecision> AuthorizeAsync(
        AssistanceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string scope = AssistanceConsentScopes.For(request.Kind);
        ProviderConsentContext context = ProviderConsentContext.Create(settings, scope, policyVersion);
        ProviderConsentGrant? grant = await grantStore.FindMatchingAsync(context, cancellationToken).ConfigureAwait(false);

        if (grant is null)
        {
            await WriteDisclosureAsync(scope).ConfigureAwait(false);
            string? response = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!IsAccepted(response)) return AssistanceAuthorizationDecision.Denied();
            grant = await grantStore.GrantAsync(context, cancellationToken).ConfigureAwait(false);
        }

        MatchingConsentAssertion? consent = MatchingConsentAssertion.TryCreate(
            context.Fingerprint, grant.Fingerprint, grant.Scope);
        AuthorizedAssistanceRequest? authorized = privacyGate.Authorize(request, consent);
        return authorized is null
            ? AssistanceAuthorizationDecision.PrivacyBlocked()
            : AssistanceAuthorizationDecision.Authorized(authorized);
    }

    private async Task WriteDisclosureAsync(string scope)
    {
        Uri destination = settings.Endpoint;
        string safeDestination = destination.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.SafeUnescaped);
        await output.WriteLineAsync($"External AI provider: {settings.Adapter}; destination: {safeDestination}; model: {settings.Model}.").ConfigureAwait(false);
        if (scope == AssistanceConsentScopes.QuestionOnly)
        {
            await output.WriteLineAsync("Scope: your explicit one-time question will be sent. No terminal Capture or conversation history is included.").ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync("Scope: the selected strict previous command text, selected captured output, relevant exit/interruption metadata, and your explicit question when using `tt ask last` may be sent.").ConfigureAwait(false);
            await output.WriteLineAsync("This is selected previous-command context, not complete terminal history.").ConfigureAwait(false);
        }
        await output.WriteLineAsync("Local Capture permission is separate and does not authorize this external transmission.").ConfigureAwait(false);
        await output.WriteAsync("Allow this external transmission scope for the unchanged provider configuration? [y/N] ").ConfigureAwait(false);
    }

    private static bool IsAccepted(string? response) => response?.Trim() is string value &&
        (value.Equals("y", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
