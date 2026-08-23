using System.Collections.Concurrent;
using System.Text;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class ProductionClassifierOwnershipRegressionTests
{
    [TestMethod]
    [DataRow("Get-ChildItem -Force", "Get-ChildItem : Cannot find path 'X' because it does not exist.")]
    [DataRow("npm run build", "npm ERR! missing script: build")]
    [DataRow("npm run build", "npm ERR! code ENOENT")]
    [DataRow("git foo", "git: 'foo' is not a git command. See 'git --help'.")]
    [DataRow("git push", "git error: failed to push some refs to 'origin'")]
    [DataRow("python test.py", "python: can't open file 'test.py': [Errno 2] No such file or directory")]
    public async Task TrackerOwnedProgramError_ReachesProviderWithoutCommandEcho(
        string submittedCommand,
        string programError)
    {
        RecordingProvider provider = new();
        SubmittedCommandTracker tracker = new();
        await using ProductionTranslationPipeline pipeline =
            ProductionRuntimeComposition.CreateTranslationPipeline(
                Guid.NewGuid(),
                provider,
                new NullSink(),
                TimeSpan.FromSeconds(2),
                submittedCommandTracker: tracker);
        pipeline.Enable();

        Assert.IsTrue(pipeline.TryObserveSubmittedInput(Encoding.UTF8.GetBytes(submittedCommand + "\r")));
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(
            $"PS C:\\work> {submittedCommand}\r\n{programError}\r\nPS C:\\work>")));
        await Task.Delay(250);

        CollectionAssert.AreEqual(new[] { programError }, provider.Sources.ToArray());
        Assert.IsFalse(provider.Sources.Any(source => source.Contains(submittedCommand, StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("Get-ChildItem -Force")]
    [DataRow("npm install")]
    [DataRow("git status")]
    public async Task TrackerOwnedSubmittedCommandEcho_NeverReachesProvider(string submittedCommand)
    {
        RecordingProvider provider = new();
        SubmittedCommandTracker tracker = new();
        await using ProductionTranslationPipeline pipeline =
            ProductionRuntimeComposition.CreateTranslationPipeline(
                Guid.NewGuid(),
                provider,
                new NullSink(),
                TimeSpan.FromSeconds(2),
                submittedCommandTracker: tracker);
        pipeline.Enable();

        Assert.IsTrue(pipeline.TryObserveSubmittedInput(Encoding.UTF8.GetBytes(submittedCommand + "\r")));
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(
            $"PS C:\\work> {submittedCommand}\r\nPS C:\\work>")));
        await Task.Delay(250);

        Assert.AreEqual(0, provider.Sources.Count);
    }

    private sealed class RecordingProvider : ITranslationProvider
    {
        public ConcurrentQueue<string> Sources { get; } = new();

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            Sources.Enqueue(request.SourceText);
            return Task.FromResult(new TranslationResult("translation"));
        }
    }

    private sealed class NullSink : ITranslationEventSink
    {
        public ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
