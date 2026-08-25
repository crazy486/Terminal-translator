namespace TerminalTranslator.Core.Capture;

public enum CaptureFailureReason
{
    None,
    TranscriptStart,
    TranscriptStop,
    Metadata,
    Boundary,
    Snapshot,
    Retention,
    Cleanup,
    Storage,
    Unknown,
}

public enum CaptureHealthNotification
{
    None,
    CaptureUnavailable,
    CaptureRestored,
}

public sealed class CaptureHealth
{
    private bool _unavailableNoticeEmitted;

    public CaptureHealthState State { get; private set; } = CaptureHealthState.Starting;

    public CaptureFailureReason FailureReason { get; private set; } = CaptureFailureReason.None;

    public string? Detail => null;

    public CaptureHealthNotification MarkUnavailable(CaptureFailureReason reason)
    {
        if (State == CaptureHealthState.Closing)
        {
            return CaptureHealthNotification.None;
        }

        FailureReason = reason == CaptureFailureReason.None ? CaptureFailureReason.Unknown : reason;
        State = CaptureHealthState.Unavailable;
        if (_unavailableNoticeEmitted)
        {
            return CaptureHealthNotification.None;
        }

        _unavailableNoticeEmitted = true;
        return CaptureHealthNotification.CaptureUnavailable;
    }

    public CaptureHealthNotification BeginRecovery()
    {
        if (State == CaptureHealthState.Unavailable)
        {
            State = CaptureHealthState.Recovering;
        }

        return CaptureHealthNotification.None;
    }

    public CaptureHealthNotification MarkHealthy()
    {
        if (State == CaptureHealthState.Closing)
        {
            return CaptureHealthNotification.None;
        }

        bool restored = _unavailableNoticeEmitted && State is CaptureHealthState.Unavailable or CaptureHealthState.Recovering;
        State = CaptureHealthState.Healthy;
        FailureReason = CaptureFailureReason.None;
        _unavailableNoticeEmitted = false;
        return restored ? CaptureHealthNotification.CaptureRestored : CaptureHealthNotification.None;
    }

    public CaptureHealthNotification Close()
    {
        State = CaptureHealthState.Closing;
        FailureReason = CaptureFailureReason.None;
        return CaptureHealthNotification.None;
    }
}
