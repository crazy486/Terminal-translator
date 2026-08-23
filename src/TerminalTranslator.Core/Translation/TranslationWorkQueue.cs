using System.Text;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Sessions;

namespace TerminalTranslator.Core.Translation;

public enum TranslationWorkOfferResult
{
    Accepted,
    Replaced,
    AcceptedWithEviction,
    DroppedCapacity,
    DroppedTextBudget,
}

public sealed class TranslationWorkQueue
{
    private const int HighCapacity = 16;
    private const int NormalCapacity = 48;
    private const int MaximumTextBytes = 256 * 1024;
    private static readonly TimeSpan MaximumQueueAge = TimeSpan.FromSeconds(1.5);

    private readonly object _gate = new();
    private readonly LinkedList<Entry> _high = [];
    private readonly LinkedList<Entry> _normal = [];
    private readonly IClock _clock;
    private readonly ITranslationRuntimeObserver? _runtimeObserver;
    private int _retainedTextBytes;

    public TranslationWorkQueue(IClock clock)
        : this(clock, runtimeObserver: null)
    {
    }

    public TranslationWorkQueue(
        IClock clock,
        ITranslationRuntimeObserver? runtimeObserver)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _runtimeObserver = runtimeObserver;
    }

    public int HighCount
    {
        get
        {
            lock (_gate)
            {
                PurgeExpired();
                return _high.Count;
            }
        }
    }

    public int NormalCount
    {
        get
        {
            lock (_gate)
            {
                PurgeExpired();
                return _normal.Count;
            }
        }
    }

    public int RetainedTextBytes
    {
        get
        {
            lock (_gate)
            {
                PurgeExpired();
                return _retainedTextBytes;
            }
        }
    }

    public TranslationWorkOfferResult Offer(OutputSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        int textBytes = Encoding.UTF8.GetByteCount(segment.NormalizedText);

        lock (_gate)
        {
            PurgeExpired();
            TimeSpan enqueuedAt = _clock.MonotonicNow;
            Record(new TranslationRuntimeEvent(
                TranslationRuntimeStage.QueueOffer,
                enqueuedAt,
                segment.Sequence,
                segment.Generation,
                CreatedAt: segment.CapturedAt,
                EnqueuedAt: enqueuedAt,
                QueueAge: enqueuedAt - segment.CapturedAt,
                TotalAge: enqueuedAt - segment.CapturedAt));

            LinkedListNode<Entry>? replaced = FindRedraw(segment.RedrawKey);
            LinkedList<Entry>? replacedLane = replaced?.List;
            if (replaced is not null)
            {
                Remove(replaced);
            }

            TranslationWorkOfferResult result = TryAdd(
                segment,
                textBytes,
                replaced is not null,
                enqueuedAt);
            if (result is TranslationWorkOfferResult.DroppedCapacity or TranslationWorkOfferResult.DroppedTextBudget &&
                replaced is not null)
            {
                Restore(replacedLane!, replaced.Value);
            }

            TranslationRuntimeStage stage = result is
                TranslationWorkOfferResult.Accepted or
                TranslationWorkOfferResult.Replaced or
                TranslationWorkOfferResult.AcceptedWithEviction
                ? TranslationRuntimeStage.QueueEnqueued
                : TranslationRuntimeStage.QueueRejected;
            Record(new TranslationRuntimeEvent(
                stage,
                enqueuedAt,
                segment.Sequence,
                segment.Generation,
                CreatedAt: segment.CapturedAt,
                EnqueuedAt: enqueuedAt,
                QueueAge: enqueuedAt - segment.CapturedAt,
                TotalAge: enqueuedAt - segment.CapturedAt));
            return result;
        }
    }

    public bool TryTake(out OutputSegment? segment)
    {
        lock (_gate)
        {
            PurgeExpired();
            LinkedListNode<Entry>? node = _high.First ?? _normal.First;
            if (node is null)
            {
                segment = null;
                return false;
            }

            segment = node.Value.Segment;
            TimeSpan dequeuedAt = _clock.MonotonicNow;
            Record(new TranslationRuntimeEvent(
                TranslationRuntimeStage.QueueDequeued,
                dequeuedAt,
                segment.Sequence,
                segment.Generation,
                CreatedAt: segment.CapturedAt,
                EnqueuedAt: node.Value.EnqueuedAt,
                DequeuedAt: dequeuedAt,
                QueueAge: dequeuedAt - node.Value.EnqueuedAt,
                TotalAge: dequeuedAt - segment.CapturedAt));
            Remove(node);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _high.Clear();
            _normal.Clear();
            _retainedTextBytes = 0;
        }
    }

    private TranslationWorkOfferResult TryAdd(
        OutputSegment segment,
        int textBytes,
        bool isReplacement,
        TimeSpan enqueuedAt)
    {
        LinkedList<Entry> lane = segment.Priority == TranslationPriority.High ? _high : _normal;
        int capacity = segment.Priority == TranslationPriority.High ? HighCapacity : NormalCapacity;
        if (lane.Count >= capacity)
        {
            return TranslationWorkOfferResult.DroppedCapacity;
        }

        if (textBytes > MaximumTextBytes)
        {
            return TranslationWorkOfferResult.DroppedTextBudget;
        }

        bool evicted = false;
        if (_retainedTextBytes + textBytes > MaximumTextBytes)
        {
            if (segment.Priority != TranslationPriority.High ||
                _retainedTextBytes - NormalTextBytes() + textBytes > MaximumTextBytes)
            {
                return TranslationWorkOfferResult.DroppedTextBudget;
            }

            while (_retainedTextBytes + textBytes > MaximumTextBytes && _normal.First is not null)
            {
                Remove(_normal.First);
                evicted = true;
            }
        }

        lane.AddLast(new Entry(segment, textBytes, enqueuedAt));
        _retainedTextBytes += textBytes;
        if (isReplacement)
        {
            return TranslationWorkOfferResult.Replaced;
        }

        return evicted
            ? TranslationWorkOfferResult.AcceptedWithEviction
            : TranslationWorkOfferResult.Accepted;
    }

    private LinkedListNode<Entry>? FindRedraw(string? redrawKey)
    {
        if (string.IsNullOrEmpty(redrawKey))
        {
            return null;
        }

        return FindRedraw(_high, redrawKey) ?? FindRedraw(_normal, redrawKey);
    }

    private static LinkedListNode<Entry>? FindRedraw(LinkedList<Entry> lane, string redrawKey)
    {
        for (LinkedListNode<Entry>? node = lane.First; node is not null; node = node.Next)
        {
            if (string.Equals(node.Value.Segment.RedrawKey, redrawKey, StringComparison.Ordinal))
            {
                return node;
            }
        }

        return null;
    }

    private void PurgeExpired()
    {
        TimeSpan now = _clock.MonotonicNow;
        PurgeExpired(_high, now);
        PurgeExpired(_normal, now);
    }

    private void PurgeExpired(LinkedList<Entry> lane, TimeSpan now)
    {
        LinkedListNode<Entry>? node = lane.First;
        while (node is not null)
        {
            LinkedListNode<Entry>? next = node.Next;
            if (now - node.Value.Segment.CapturedAt > MaximumQueueAge)
            {
                Record(new TranslationRuntimeEvent(
                    TranslationRuntimeStage.QueueExpired,
                    now,
                    node.Value.Segment.Sequence,
                    node.Value.Segment.Generation,
                    CreatedAt: node.Value.Segment.CapturedAt,
                    EnqueuedAt: node.Value.EnqueuedAt,
                    QueueAge: now - node.Value.EnqueuedAt,
                    TotalAge: now - node.Value.Segment.CapturedAt,
                    CancelReason: TranslationCancellationReason.QueueExpired));
                Remove(node);
            }

            node = next;
        }
    }

    private int NormalTextBytes()
    {
        int total = 0;
        foreach (Entry entry in _normal)
        {
            total += entry.TextBytes;
        }

        return total;
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _retainedTextBytes -= node.Value.TextBytes;
        node.List!.Remove(node);
    }

    private void Restore(LinkedList<Entry> lane, Entry entry)
    {
        lane.AddLast(entry);
        _retainedTextBytes += entry.TextBytes;
    }

    private void Record(TranslationRuntimeEvent runtimeEvent)
    {
        try
        {
            _runtimeObserver?.Record(runtimeEvent);
        }
        catch
        {
            // Diagnostics are an optional, content-free side path.
        }
    }

    private sealed record Entry(OutputSegment Segment, int TextBytes, TimeSpan EnqueuedAt);
}
