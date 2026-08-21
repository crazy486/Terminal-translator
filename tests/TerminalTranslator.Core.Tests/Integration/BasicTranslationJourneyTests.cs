using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Tests.TestDoubles;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Core.Tests.Integration;

[TestClass]
public sealed class BasicTranslationJourneyTests
{
    [TestMethod]
    public async Task TranslateAsync_PublishesOrderedTransientTranslation()
    {
        FakeClock clock = new();
        TranslationProviderSpy provider = new() { Result = new("\u64cd\u4f5c\u5df2\u5b8c\u6210\u3002", "request-1") };
        InMemoryTranslationEventSink sink = new();
        TranslationCoordinator coordinator = new(provider, sink, clock, TimeSpan.FromSeconds(2));
        OutputSegment segment = new(
            Guid.NewGuid(), 3, 9, "The operation completed.", new LayoutHints(1, [0]),
            SourceBoundary.Line, TranslationPriority.Normal, null, clock.MonotonicNow);

        TranslationItem? item = await coordinator.TranslateAsync(segment, CancellationToken.None);

        Assert.IsNotNull(item);
        Assert.AreEqual((ulong)9, item.SegmentSequence);
        Assert.AreEqual("\u64cd\u4f5c\u5df2\u5b8c\u6210\u3002", item.TranslatedText);
        Assert.AreEqual(1, provider.Requests.Count);
        Assert.AreSame(item, sink.Items.Single());
    }

    private sealed class InMemoryTranslationEventSink : ITranslationEventSink
    {
        public List<TranslationItem> Items { get; } = [];

        public ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken)
        {
            Items.Add(item);
            return ValueTask.CompletedTask;
        }
    }
}
