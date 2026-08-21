using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Core.Sessions;

public sealed class StatusAggregator
{
    private static readonly TimeSpan NoticeWindow = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly Guid _sessionId;
    private readonly IClock _clock;
    private readonly Bucket _provider = new();
    private readonly Bucket _privacy = new();
    private readonly Bucket _overload = new();

    public StatusAggregator(Guid sessionId, IClock clock)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session identifier must not be empty.", nameof(sessionId));
        }

        _sessionId = sessionId;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public StatusEvent? RecordProviderFailure(
        TranslationErrorCode reason,
        int count,
        TimeSpan? duration,
        Exception? rawException)
    {
        _ = duration;
        _ = rawException;
        return Record(_provider, StatusKind.ProviderError, ProviderCode(reason), count);
    }

    public StatusEvent? RecordPrivacySkip(PrivacyReasonCode reason, int count) =>
        Record(_privacy, StatusKind.PrivacySkip, PrivacyCode(reason), count);

    public StatusEvent? RecordOverload(int count) =>
        Record(_overload, StatusKind.Degraded, "overload", count);

    private StatusEvent? Record(Bucket bucket, StatusKind kind, string code, int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        lock (_gate)
        {
            TimeSpan now = _clock.MonotonicNow;
            if (!bucket.HasEmitted)
            {
                bucket.HasEmitted = true;
                bucket.LastEmittedAt = now;
                return Create(kind, code, count);
            }

            if (now - bucket.LastEmittedAt < NoticeWindow)
            {
                bucket.PendingCount = checked(bucket.PendingCount + count);
                return null;
            }

            int aggregateCount = checked(bucket.PendingCount + count);
            bucket.PendingCount = 0;
            bucket.LastEmittedAt = now;
            return Create(kind, code, aggregateCount);
        }
    }

    private StatusEvent Create(StatusKind kind, string code, int count) =>
        new(_sessionId, kind, code, count, _clock.UtcNow);

    private static string ProviderCode(TranslationErrorCode reason) => reason switch
    {
        TranslationErrorCode.Canceled => "canceled",
        TranslationErrorCode.Timeout => "timeout",
        TranslationErrorCode.Authentication => "authentication",
        TranslationErrorCode.RateLimited => "rate-limited",
        TranslationErrorCode.Unavailable => "unavailable",
        TranslationErrorCode.InvalidResponse => "invalid-response",
        _ => "provider-error",
    };

    private static string PrivacyCode(PrivacyReasonCode reason) => reason switch
    {
        PrivacyReasonCode.None => "privacy-skip",
        PrivacyReasonCode.CredentialAssignment => "credential-assignment",
        PrivacyReasonCode.AuthorizationValue => "authorization-value",
        PrivacyReasonCode.PrivateKey => "private-key",
        PrivacyReasonCode.TokenShape => "token-shape",
        PrivacyReasonCode.CredentialUri => "credential-uri",
        PrivacyReasonCode.MalformedSensitiveBlock => "malformed-sensitive-block",
        PrivacyReasonCode.DetectorFailure => "detector-failure",
        _ => "privacy-skip",
    };

    private sealed class Bucket
    {
        public bool HasEmitted { get; set; }

        public TimeSpan LastEmittedAt { get; set; }

        public int PendingCount { get; set; }
    }
}
