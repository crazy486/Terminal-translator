using System.Reflection;
using System.Text;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Tests.TestDoubles;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class TranslationWorkQueueTests
{
    private const int HighCapacity = 16;
    private const int NormalCapacity = 48;
    private const int TextBudgetBytes = 256 * 1024;

    [TestMethod]
    public void HighPriorityLane_AcceptsSixteenAndDropsSeventeenth()
    {
        FakeClock clock = new();
        QueueContractSubject queue = QueueContractSubject.Create(clock);

        for (int index = 0; index < HighCapacity; index++)
        {
            Assert.AreEqual("Accepted", queue.Offer(CreateSegment(clock, (ulong)index, TranslationPriority.High)));
        }

        Assert.AreEqual(
            "DroppedCapacity",
            queue.Offer(CreateSegment(clock, HighCapacity, TranslationPriority.High)));
        Assert.AreEqual(HighCapacity, queue.HighCount);
        Assert.AreEqual(0, queue.NormalCount);
    }

    [TestMethod]
    public void NormalPriorityLane_AcceptsFortyEightAndDropsFortyNinth()
    {
        FakeClock clock = new();
        QueueContractSubject queue = QueueContractSubject.Create(clock);

        for (int index = 0; index < NormalCapacity; index++)
        {
            Assert.AreEqual("Accepted", queue.Offer(CreateSegment(clock, (ulong)index, TranslationPriority.Normal)));
        }

        Assert.AreEqual(
            "DroppedCapacity",
            queue.Offer(CreateSegment(clock, NormalCapacity, TranslationPriority.Normal)));
        Assert.AreEqual(NormalCapacity, queue.NormalCount);
        Assert.AreEqual(0, queue.HighCount);
    }

    [TestMethod]
    public void AggregateTextBudget_DoesNotExceedTwoHundredFiftySixKiB()
    {
        FakeClock clock = new();
        QueueContractSubject queue = QueueContractSubject.Create(clock);
        string eightKiB = new('a', 8 * 1024);

        for (int index = 0; index < 32; index++)
        {
            Assert.AreEqual(
                "Accepted",
                queue.Offer(CreateSegment(clock, (ulong)index, TranslationPriority.Normal, eightKiB)));
        }

        Assert.AreEqual(TextBudgetBytes, queue.RetainedTextBytes);
        Assert.AreEqual(
            "DroppedTextBudget",
            queue.Offer(CreateSegment(clock, 32, TranslationPriority.Normal, "b")));
        Assert.AreEqual(TextBudgetBytes, queue.RetainedTextBytes);
    }

    [TestMethod]
    public void MatchingRedrawKey_ReplacesQueuedItemWithLatestText()
    {
        FakeClock clock = new();
        QueueContractSubject queue = QueueContractSubject.Create(clock);
        OutputSegment first = CreateSegment(clock, 1, TranslationPriority.Normal, "Downloading 10%", "progress-1");
        OutputSegment latest = CreateSegment(clock, 2, TranslationPriority.Normal, "Downloading 90%", "progress-1");

        Assert.AreEqual("Accepted", queue.Offer(first));
        Assert.AreEqual("Replaced", queue.Offer(latest));

        Assert.AreEqual(1, queue.NormalCount);
        Assert.IsTrue(queue.TryTake(out OutputSegment? taken));
        Assert.AreEqual(latest.Sequence, taken!.Sequence);
        Assert.AreEqual(latest.NormalizedText, taken.NormalizedText);
    }

    [TestMethod]
    public void HighPriorityOffer_EvictsOldestNormalItemWhenTextBudgetIsFull()
    {
        FakeClock clock = new();
        QueueContractSubject queue = QueueContractSubject.Create(clock);
        string eightKiB = new('n', 8 * 1024);

        for (int index = 0; index < 15; index++)
        {
            Assert.AreEqual(
                "Accepted",
                queue.Offer(CreateSegment(clock, (ulong)index, TranslationPriority.High, "h")));
        }

        for (int index = 0; index < 31; index++)
        {
            Assert.AreEqual(
                "Accepted",
                queue.Offer(CreateSegment(clock, (ulong)(100 + index), TranslationPriority.Normal, eightKiB)));
        }

        OutputSegment urgent = CreateSegment(clock, 999, TranslationPriority.High, new string('u', 8 * 1024));
        Assert.AreEqual("AcceptedWithEviction", queue.Offer(urgent));
        Assert.AreEqual(16, queue.HighCount);
        Assert.AreEqual(30, queue.NormalCount);
        Assert.IsTrue(queue.RetainedTextBytes <= TextBudgetBytes);

        List<ulong> sequences = DrainSequences(queue);
        CollectionAssert.DoesNotContain(sequences, (ulong)100, "The oldest normal item should be evicted first.");
        CollectionAssert.Contains(sequences, urgent.Sequence);
    }

    [TestMethod]
    public void NormalPriorityOffer_NeverEvictsHighPriorityItem()
    {
        FakeClock clock = new();
        QueueContractSubject queue = QueueContractSubject.Create(clock);
        string eightKiB = new('x', 8 * 1024);

        for (int index = 0; index < HighCapacity; index++)
        {
            Assert.AreEqual(
                "Accepted",
                queue.Offer(CreateSegment(clock, (ulong)index, TranslationPriority.High, eightKiB)));
        }

        for (int index = 0; index < 16; index++)
        {
            Assert.AreEqual(
                "Accepted",
                queue.Offer(CreateSegment(clock, (ulong)(100 + index), TranslationPriority.Normal, eightKiB)));
        }

        Assert.AreEqual(
            "DroppedTextBudget",
            queue.Offer(CreateSegment(clock, 999, TranslationPriority.Normal, eightKiB)));
        Assert.AreEqual(HighCapacity, queue.HighCount);
        Assert.AreEqual(16, queue.NormalCount);
    }

    [TestMethod]
    public void ItemNotStartedWithinOnePointFiveSeconds_ExpiresUsingMonotonicClock()
    {
        FakeClock clock = new();
        QueueContractSubject queue = QueueContractSubject.Create(clock);
        Assert.AreEqual("Accepted", queue.Offer(CreateSegment(clock, 1, TranslationPriority.Normal)));

        clock.Advance(TimeSpan.FromMilliseconds(1501));

        Assert.IsFalse(queue.TryTake(out _));
        Assert.AreEqual(0, queue.NormalCount);
        Assert.AreEqual(0, queue.RetainedTextBytes);
    }

    [TestMethod]
    [Timeout(1000)]
    public void Offer_IsSynchronousAndReturnsDropResultWhenQueueIsFull()
    {
        FakeClock clock = new();
        QueueContractSubject queue = QueueContractSubject.Create(clock);

        for (int index = 0; index < NormalCapacity; index++)
        {
            Assert.AreEqual("Accepted", queue.Offer(CreateSegment(clock, (ulong)index, TranslationPriority.Normal)));
        }

        string result = queue.Offer(CreateSegment(clock, 999, TranslationPriority.Normal));

        Assert.AreEqual("DroppedCapacity", result);
    }

    private static OutputSegment CreateSegment(
        FakeClock clock,
        ulong sequence,
        TranslationPriority priority,
        string text = "Useful terminal output.",
        string? redrawKey = null) => new(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            1,
            sequence,
            text,
            new LayoutHints(1, [0]),
            redrawKey is null ? SourceBoundary.Line : SourceBoundary.IdlePrompt,
            priority,
            redrawKey,
            clock.MonotonicNow);

    private static List<ulong> DrainSequences(QueueContractSubject queue)
    {
        List<ulong> sequences = [];
        while (queue.TryTake(out OutputSegment? segment))
        {
            sequences.Add(segment!.Sequence);
        }

        return sequences;
    }

    /// <summary>
    /// Test-only reflection seam that keeps the tests compilable until T060 creates the production type.
    /// It contains no queue behavior and binds every operation to the future production implementation.
    /// </summary>
    private sealed class QueueContractSubject
    {
        private const string ProductionTypeName =
            "TerminalTranslator.Core.Translation.TranslationWorkQueue";

        private readonly object _instance;
        private readonly MethodInfo _offer;
        private readonly MethodInfo _tryTake;
        private readonly PropertyInfo _highCount;
        private readonly PropertyInfo _normalCount;
        private readonly PropertyInfo _retainedTextBytes;

        private QueueContractSubject(Type type, object instance)
        {
            _instance = instance;
            _offer = RequireMethod(type, "Offer", typeof(OutputSegment));
            _tryTake = RequireMethod(type, "TryTake", typeof(OutputSegment).MakeByRefType());
            _highCount = RequireProperty(type, "HighCount");
            _normalCount = RequireProperty(type, "NormalCount");
            _retainedTextBytes = RequireProperty(type, "RetainedTextBytes");
        }

        public int HighCount => ReadInt32(_highCount);

        public int NormalCount => ReadInt32(_normalCount);

        public int RetainedTextBytes => ReadInt32(_retainedTextBytes);

        public static QueueContractSubject Create(IClock clock)
        {
            Type? type = typeof(OutputSegment).Assembly.GetType(ProductionTypeName, throwOnError: false);
            if (type is null)
            {
                throw new AssertFailedException(
                    $"T060 production type '{ProductionTypeName}' has not been implemented.");
            }

            ConstructorInfo? constructor = type.GetConstructor([typeof(IClock)]);
            if (constructor is null)
            {
                throw new AssertFailedException(
                    $"{ProductionTypeName} must expose a constructor accepting IClock.");
            }

            return new QueueContractSubject(type, constructor.Invoke([clock]));
        }

        public string Offer(OutputSegment segment)
        {
            object? result = _offer.Invoke(_instance, [segment]);
            return result?.ToString() ?? throw new AssertFailedException("Offer must return a named result.");
        }

        public bool TryTake(out OutputSegment? segment)
        {
            object?[] arguments = [null];
            object? result = _tryTake.Invoke(_instance, arguments);
            segment = arguments[0] as OutputSegment;
            return result is bool taken
                ? taken
                : throw new AssertFailedException("TryTake must return bool.");
        }

        private int ReadInt32(PropertyInfo property)
        {
            object? value = property.GetValue(_instance);
            return value is int count
                ? count
                : throw new AssertFailedException($"{property.Name} must return Int32.");
        }

        private static MethodInfo RequireMethod(Type type, string name, params Type[] parameterTypes) =>
            type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, parameterTypes, null)
            ?? throw new AssertFailedException(
                $"{ProductionTypeName} must expose {name}({string.Join(", ", parameterTypes.Select(static value => value.Name))}).");

        private static PropertyInfo RequireProperty(Type type, string name) =>
            type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new AssertFailedException($"{ProductionTypeName} must expose {name}.");
    }
}
