using System.Security.Cryptography;
using System.Text;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Privacy;

namespace TerminalTranslator.Core.Assistance;

public sealed class AssistancePrivacyGate(SecretDetector secretDetector)
{
    private readonly SecretDetector _secretDetector = secretDetector ?? throw new ArgumentNullException(nameof(secretDetector));

    public AuthorizedAssistanceRequest? Authorize(
        AssistanceRequest exactSelectedRequest,
        MatchingConsentAssertion? consent)
    {
        ArgumentNullException.ThrowIfNull(exactSelectedRequest);
        if (consent is null ||
            !string.Equals(consent.Scope, AssistanceConsentScopes.For(exactSelectedRequest.Kind), StringComparison.Ordinal))
            return null;

        string exactPayload = AssistanceRequestVariableInputSerializer.Serialize(exactSelectedRequest);
        PrivacyDecision decision = _secretDetector.Screen(0, exactPayload);
        if (decision.Outcome != PrivacyOutcome.Allow)
            return null;

        return new AuthorizedAssistanceRequest(
            exactSelectedRequest,
            consent,
            ComputePayloadFingerprint(exactSelectedRequest));
    }

    internal static string ComputePayloadFingerprint(AssistanceRequest request)
    {
        string exactPayload = AssistanceRequestVariableInputSerializer.Serialize(request);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exactPayload)));
    }
}
