using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Parsing;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Windows.Terminal;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class UserStory1AcceptanceTests
{
    [TestMethod]
    public async Task FakeProviderJourney_MeetsMvpQualityLatencyAndIsolationTargets()
    {
        string corpusPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TranslationCorpus.json");
        IReadOnlyList<CorpusEntry> corpus = JsonSerializer.Deserialize<List<CorpusEntry>>(
            await File.ReadAllTextAsync(corpusPath), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new AssertFailedException("Corpus is empty.");
        Dictionary<string, CorpusEntry> bySource = corpus.ToDictionary(entry => entry.Source, StringComparer.Ordinal);
        RecordingProvider provider = new(async (request, cancellationToken) =>
        {
            CorpusEntry entry = bySource[request.SourceText];
            await Task.Delay(entry.DelayMilliseconds, cancellationToken);
            return new TranslationResult(entry.Translation, $"fake-{request.SegmentSequence}");
        });
        RecordingSink sink = new();
        TranslationCoordinator coordinator = new(provider, sink, new SystemClock(), TimeSpan.FromSeconds(2));
        BasicHostPipeline pipeline = new(
            Guid.NewGuid(),
            new VtTextExtractor(),
            new EnglishCandidateClassifier(),
            coordinator);

        byte[] disabledBytes = Encoding.UTF8.GetBytes("This content must remain local while disabled.\n");
        byte[] disabledSnapshot = disabledBytes.ToArray();
        Assert.AreEqual(0, await pipeline.ProcessAsync(disabledBytes, CancellationToken.None));
        CollectionAssert.AreEqual(disabledSnapshot, disabledBytes);
        Assert.AreEqual(0, provider.Requests.Count);

        pipeline.Enable();
        List<TimeSpan> valuableLatencies = [];
        foreach (CorpusEntry entry in corpus)
        {
            byte[] programPaneBytes = Encoding.UTF8.GetBytes(entry.Source + "\n");
            byte[] unchangedSnapshot = programPaneBytes.ToArray();
            Stopwatch stopwatch = Stopwatch.StartNew();
            int translated = await pipeline.ProcessAsync(programPaneBytes, CancellationToken.None);
            stopwatch.Stop();

            CollectionAssert.AreEqual(unchangedSnapshot, programPaneBytes);
            Assert.AreEqual(entry.Valuable ? 1 : 0, translated, entry.Source);
            if (entry.Valuable)
            {
                valuableLatencies.Add(stopwatch.Elapsed);
            }
        }

        int valuableCount = corpus.Count(entry => entry.Valuable);
        double quality = (double)sink.Items.Count / valuableCount;
        double withinTarget = (double)valuableLatencies.Count(latency => latency < TimeSpan.FromSeconds(2)) / valuableCount;
        Assert.IsGreaterThanOrEqualTo(0.90, quality, "SC-001");
        Assert.IsGreaterThanOrEqualTo(0.95, withinTarget, "SC-002");
        Assert.AreEqual(valuableCount, provider.Requests.Count);
        CollectionAssert.AreEquivalent(
            corpus.Where(entry => entry.Valuable).Select(entry => entry.Translation).ToArray(),
            sink.Items.Select(item => item.TranslatedText).ToArray());
    }

    [TestMethod]
    public void WindowsTerminalArguments_CreateExactlyOneProgramAndOneCompanionPane()
    {
        IReadOnlyList<string> arguments = WindowsTerminalLauncher.BuildArguments(
            @"C:\tools\tt.exe",
            "0123456789abcdef",
            @"C:\work");
        Assert.AreEqual(1, arguments.Count(value => value == "new-tab"));
        Assert.AreEqual(1, arguments.Count(value => value == "split-pane"));
        Assert.AreEqual(1, arguments.Count(value => value == "__host"));
        Assert.AreEqual(1, arguments.Count(value => value == "__companion"));
        Assert.IsTrue(arguments.Contains("focus-pane"));
    }

    private sealed record CorpusEntry(
        string Source,
        string Translation,
        bool Valuable,
        int DelayMilliseconds);

    private sealed class RecordingProvider(
        Func<TranslationRequest, CancellationToken, Task<TranslationResult>> handler) : ITranslationProvider
    {
        public List<TranslationRequest> Requests { get; } = [];

        public async Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await handler(request, cancellationToken);
        }
    }

    private sealed class RecordingSink : ITranslationEventSink
    {
        public List<TranslationItem> Items { get; } = [];

        public ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken)
        {
            Items.Add(item);
            return ValueTask.CompletedTask;
        }
    }
}
