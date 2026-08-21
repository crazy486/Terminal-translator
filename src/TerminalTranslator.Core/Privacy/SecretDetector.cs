using System.Text.RegularExpressions;
using TerminalTranslator.Core.Models;

namespace TerminalTranslator.Core.Privacy;

public sealed partial class SecretDetector
{
    private readonly Func<string, PrivacyReasonCode?> _detect;

    public SecretDetector(Func<string, PrivacyReasonCode?>? detector = null) =>
        _detect = detector ?? DetectReason;

    public PrivacyDecision Screen(ulong segmentSequence, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            PrivacyReasonCode reason = _detect(text) ?? PrivacyReasonCode.None;
            return new PrivacyDecision(
                segmentSequence,
                reason == PrivacyReasonCode.None ? PrivacyOutcome.Allow : PrivacyOutcome.Skip,
                reason);
        }
        catch
        {
            return new PrivacyDecision(
                segmentSequence,
                PrivacyOutcome.Skip,
                PrivacyReasonCode.DetectorFailure);
        }
    }

    private static PrivacyReasonCode? DetectReason(string text)
    {
        bool privateKeyBegin = PrivateKeyBeginRegex().IsMatch(text);
        bool privateKeyEnd = PrivateKeyEndRegex().IsMatch(text);
        if (privateKeyBegin && privateKeyEnd)
        {
            return PrivacyReasonCode.PrivateKey;
        }

        if (privateKeyBegin || privateKeyEnd)
        {
            return PrivacyReasonCode.MalformedSensitiveBlock;
        }

        if (AuthorizationRegex().IsMatch(text))
        {
            return PrivacyReasonCode.AuthorizationValue;
        }

        if (CredentialAssignmentRegex().IsMatch(text))
        {
            return PrivacyReasonCode.CredentialAssignment;
        }

        if (CredentialUriRegex().IsMatch(text))
        {
            return PrivacyReasonCode.CredentialUri;
        }

        if (JwtRegex().IsMatch(text) || KnownTokenRegex().IsMatch(text))
        {
            return PrivacyReasonCode.TokenShape;
        }

        return null;
    }

    [GeneratedRegex(
        @"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBeginRegex();

    [GeneratedRegex(
        @"-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyEndRegex();

    [GeneratedRegex(
        @"(?im)^\s*(?:proxy-)?authorization\s*:\s*(?:bearer|basic|token)\s+\S+",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(
        @"(?im)(?:^|\s)(?:api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret|password|passwd|secret|token)\s*[:=]\s*[^\s'""]{6,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(
        @"(?i)\b[a-z][a-z0-9+.-]*://[^\s/@:]+:[^\s/@]+@[^\s/]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialUriRegex();

    [GeneratedRegex(
        @"\beyJ[A-Za-z0-9_-]{7,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|(?:sk|rk|pk)-(?:[A-Za-z0-9_-]{20,})|AKIA[0-9A-Z]{16})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenRegex();
}
