using TerminalTranslator.Core.Models;

namespace TerminalTranslator.Core.Sessions;

public sealed class TranslationSession : IDisposable
{
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly List<OutputSegment> _segments = [];
    private readonly List<TranslationItem> _translations = [];
    private CancellationTokenSource _translationCancellation = new();
    private bool _disposed;
    private int _privacySkipped;
    private int _overloadDropped;

    public TranslationSession(Guid sessionId, IClock clock)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session identifier must not be empty.", nameof(sessionId));
        }

        SessionId = sessionId;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        StartedAt = clock.UtcNow;
        State = SessionState.Created;
    }

    public Guid SessionId { get; }

    public long Generation { get; private set; }

    public SessionState State { get; private set; }

    public ConsentGrant? Consent { get; private set; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? EndedAt { get; private set; }

    public int RetainedSegmentCount
    {
        get { lock (_gate) { return _segments.Count; } }
    }

    public int RetainedTranslationCount
    {
        get { lock (_gate) { return _translations.Count; } }
    }

    public int PrivacySkipped => Volatile.Read(ref _privacySkipped);

    public int OverloadDropped => Volatile.Read(ref _overloadDropped);

    public CancellationToken TranslationCancellationToken
    {
        get { lock (_gate) { return _translationCancellation.Token; } }
    }

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireState(SessionState.Created);
            State = SessionState.Starting;
            State = SessionState.Disabled;
            _translationCancellation.Cancel();
        }
    }

    public bool Enable(string providerFingerprint, bool consent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerFingerprint);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (State == SessionState.Enabled &&
                string.Equals(Consent?.ProviderFingerprint, providerFingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            if (State != SessionState.Disabled)
            {
                throw new InvalidOperationException($"Cannot enable a session from state {State}.");
            }

            if (!consent)
            {
                return false;
            }

            State = SessionState.Enabling;
            Generation++;
            ReplaceCancellation(active: true);
            Consent = new ConsentGrant(
                SessionId,
                Generation,
                providerFingerprint,
                ConsentScope.EligibleCurrentSessionSegments,
                _clock.UtcNow);
            State = SessionState.Enabled;
            return true;
        }
    }

    public bool Disable()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (State == SessionState.Disabled)
            {
                return false;
            }

            if (State is not (SessionState.Enabled or SessionState.Enabling))
            {
                throw new InvalidOperationException($"Cannot disable a session from state {State}.");
            }

            Generation++;
            State = SessionState.Disabled;
            Consent = null;
            CancelAndClear(createCanceledReplacement: true);
            return true;
        }
    }

    public bool IsAuthorized(long generation, string providerFingerprint)
    {
        lock (_gate)
        {
            return !_disposed &&
                State == SessionState.Enabled &&
                Generation == generation &&
                Consent is not null &&
                Consent.SessionId == SessionId &&
                Consent.Generation == generation &&
                string.Equals(Consent.ProviderFingerprint, providerFingerprint, StringComparison.Ordinal);
        }
    }

    public void TrackSegment(OutputSegment segment)
    {
        if (!TryTrackSegment(segment))
        {
            throw new InvalidOperationException("The segment does not belong to the active session generation.");
        }
    }

    public bool TryTrackSegment(OutputSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        lock (_gate)
        {
            if (_disposed || segment.SessionId != SessionId || segment.Generation != Generation ||
                State != SessionState.Enabled)
            {
                return false;
            }

            _segments.Add(segment);
            return true;
        }
    }

    public void TrackTranslation(TranslationItem item)
    {
        if (!TryTrackTranslation(item))
        {
            throw new InvalidOperationException("The translation does not belong to the active session generation.");
        }
    }

    public bool TryTrackTranslation(TranslationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            if (_disposed || State != SessionState.Enabled || item.SessionId != SessionId ||
                item.Generation != Generation)
            {
                return false;
            }

            _translations.Add(item);
            return true;
        }
    }

    public void RecordPrivacySkip() => Interlocked.Increment(ref _privacySkipped);

    public void RecordOverloadDrop() => Interlocked.Increment(ref _overloadDropped);

    public void BeginStopping()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (State is SessionState.Stopping or SessionState.Ended or SessionState.Faulted)
            {
                return;
            }

            Generation++;
            State = SessionState.Stopping;
            Consent = null;
            CancelAndClear(createCanceledReplacement: true);
        }
    }

    public void End()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireState(SessionState.Stopping);
            State = SessionState.Ended;
            EndedAt = _clock.UtcNow;
            CancelAndClear(createCanceledReplacement: true);
        }
    }

    public void Fault()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (State == SessionState.Ended)
            {
                throw new InvalidOperationException("An ended session cannot transition to faulted.");
            }

            State = SessionState.Faulted;
            EndedAt = _clock.UtcNow;
            Consent = null;
            CancelAndClear(createCanceledReplacement: true);
        }
    }

    private void CancelAndClear(bool createCanceledReplacement)
    {
        _translationCancellation.Cancel();
        _translationCancellation.Dispose();
        _translationCancellation = new CancellationTokenSource();
        if (createCanceledReplacement)
        {
            _translationCancellation.Cancel();
        }

        _segments.Clear();
        _translations.Clear();
    }

    private void ReplaceCancellation(bool active)
    {
        _translationCancellation.Dispose();
        _translationCancellation = new CancellationTokenSource();
        if (!active)
        {
            _translationCancellation.Cancel();
        }
    }

    private void RequireState(SessionState expected)
    {
        if (State != expected)
        {
            throw new InvalidOperationException($"Expected session state {expected}, but was {State}.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _translationCancellation.Cancel();
            _translationCancellation.Dispose();
            _segments.Clear();
            _translations.Clear();
            Consent = null;
            _disposed = true;
        }
    }
}
