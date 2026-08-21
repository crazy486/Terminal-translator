using System.Diagnostics;
using System.Text;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Privacy;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class UserStory3AcceptanceTests
{
    [TestMethod]
    public async Task RealControlPipe_ConsentDisableReEnablePrivacyAndTeardownMeetUs3Outcomes()
    {
        Guid sessionId = Guid.NewGuid();
        string sessionText = sessionId.ToString("N");
        string nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray()) +
            Convert.ToHexString(Guid.NewGuid().ToByteArray());
        string pipeName = SessionPipeNames.Control(sessionText, nonce);
        ProviderSettings settings = ProviderSettings.Create(
            new Uri("https://fake.local/chat/completions"),
            "fake-model",
            "TT_FAKE_KEY",
            TimeSpan.FromSeconds(1));
        SystemClock clock = new();
        using TranslationSession session = new(sessionId, clock);
        session.Start();
        RecordingProvider provider = new();
        RecordingEventSink translationSink = new();
        List<PrivacyDecision> privacyDecisions = [];
        List<StateEventMessage> states = [];
        SecretDetector detector = new();
        await using ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            sessionId,
            provider,
            translationSink,
            TimeSpan.FromSeconds(1),
            secretDetector: detector,
            session: session,
            providerFingerprint: settings.Fingerprint,
            privacyDecisionSink: (decision, _) =>
            {
                privacyDecisions.Add(decision);
                return ValueTask.CompletedTask;
            });
        SessionControlHandler handler = new(
            session,
            pipeline,
            settings,
            (state, _) =>
            {
                states.Add(state);
                return Task.CompletedTask;
            });
        using CancellationTokenSource runtime = new(TimeSpan.FromSeconds(10));
        await using ControlPipeServer server = new(pipeName, sessionText, nonce, handler);
        Task serverTask = server.RunAsync(runtime.Token);
        IControlPipeClient client = new ControlPipeClient(pipeName, sessionText, nonce);

        Assert.IsFalse(pipeline.TryOffer(Encoding.UTF8.GetBytes("Disabled output must remain local.\r\n")));
        ControlResultMessage declined = await client.EnableAsync(
            settings.Fingerprint,
            consent: false,
            runtime.Token);
        Assert.IsFalse(declined.Ok);
        Assert.AreEqual(0, provider.Requests.Count);

        ControlResultMessage enabled = await client.EnableAsync(
            settings.Fingerprint,
            consent: true,
            runtime.Token);
        Assert.IsTrue(enabled.Ok);
        Assert.AreEqual("enabled", enabled.State);
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(
            "The first operation completed successfully.\r\n")));
        Assert.IsTrue(await WaitUntilAsync(() => provider.Requests.Count == 1, TimeSpan.FromSeconds(2)));

        Stopwatch disableLatency = Stopwatch.StartNew();
        ControlResultMessage disabled = await client.DisableAsync(runtime.Token);
        disableLatency.Stop();
        Assert.IsTrue(disabled.Ok);
        Assert.AreEqual("disabled", disabled.State);
        Assert.IsLessThan(TimeSpan.FromSeconds(1), disableLatency.Elapsed, "SC-005");
        Assert.IsFalse(pipeline.TryOffer(Encoding.UTF8.GetBytes(
            "The disabled operation must never be transmitted.\r\n")));
        await Task.Delay(200, runtime.Token);
        Assert.AreEqual(1, provider.Requests.Count, "SC-008");

        ControlResultMessage reEnabled = await client.EnableAsync(
            settings.Fingerprint,
            consent: true,
            runtime.Token);
        Assert.IsTrue(reEnabled.Ok);
        Assert.IsGreaterThan(enabled.Generation!.Value, reEnabled.Generation!.Value);
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(
            "The second operation completed after re-enable.\r\n")));
        Assert.IsTrue(await WaitUntilAsync(() => provider.Requests.Count == 2, TimeSpan.FromSeconds(2)));

        const string secretSegment = "API_KEY=sk-test-1234567890abcdefghijklmnop";
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(secretSegment + "\r\n")));
        Assert.IsTrue(await WaitUntilAsync(() => privacyDecisions.Count == 1, TimeSpan.FromSeconds(2)));
        Assert.AreEqual(2, provider.Requests.Count, "SC-010");
        Assert.AreEqual(PrivacyOutcome.Skip, privacyDecisions.Single().Outcome);
        Assert.IsFalse(privacyDecisions.Single().ToString().Contains(secretSegment, StringComparison.Ordinal));

        StatusResultMessage status = await client.StatusAsync(runtime.Token);
        Assert.AreEqual("enabled", status.State);
        Assert.AreEqual(1, status.PrivacySkipped);
        Assert.AreEqual("fake.local", status.ProviderHost);

        List<StatusEvent> ended = [];
        SessionTeardown teardown = new(session, clock);
        await teardown.ExecuteAsync(
            "shell-exit",
            0,
            abnormal: false,
            _ => Task.CompletedTask,
            (value, _) =>
            {
                ended.Add(value);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
        Assert.AreEqual(SessionState.Ended, session.State);
        Assert.AreEqual(0, session.RetainedSegmentCount);
        Assert.AreEqual(0, session.RetainedTranslationCount);
        Assert.AreEqual(StatusKind.SessionEnded, ended.Single().Kind, "SC-009");
        Assert.IsTrue(states.Any(state => state.State == "enabled"));
        Assert.IsTrue(states.Any(state => state.State == "disabled"));

        runtime.Cancel();
        await serverTask;
    }

    [TestMethod]
    public void PublicCommandSurface_ContainsStartOnOffAndStatusForOneMinuteControlJourney()
    {
        string[] commandNames = CommandFactory.CreateRootCommand().Subcommands
            .Where(command => !command.Hidden)
            .Select(command => command.Name)
            .ToArray();

        CollectionAssert.IsSubsetOf(new[] { "start", "on", "off", "status" }, commandNames);
    }

    [TestMethod]
    public async Task Status_ReportsActualBoundedQueueDepthInsteadOfPlaceholderZeros()
    {
        Guid sessionId = Guid.NewGuid();
        SystemClock clock = new();
        using TranslationSession session = new(sessionId, clock);
        session.Start();
        ProviderSettings settings = ProviderSettings.Create(
            new Uri("https://fake.local/chat/completions"),
            "fake-model",
            "TT_FAKE_KEY",
            TimeSpan.FromSeconds(1));
        BlockingProvider provider = new();
        await using ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            sessionId,
            provider,
            new RecordingEventSink(),
            TimeSpan.FromSeconds(1),
            session: session,
            providerFingerprint: settings.Fingerprint);
        SessionControlHandler handler = new(session, pipeline, settings);
        Assert.IsTrue(pipeline.Enable(settings.Fingerprint, consent: true));

        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(
            "The first operation is waiting for the provider.\r\n")));
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (int index = 0; index < 8; index++)
        {
            Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(
                $"The queued operation number {index} completed successfully.\r\n")));
        }

        StatusResultMessage? status = null;
        Assert.IsTrue(await WaitUntilAsync(() =>
        {
            status = handler.StatusAsync(CancellationToken.None).GetAwaiter().GetResult();
            return status.NormalQueued > 0;
        }, TimeSpan.FromSeconds(2)));
        Assert.IsNotNull(status);
        Assert.IsTrue(status.NormalQueued is > 0 and <= 48);
        Assert.IsTrue(status.HighQueued is >= 0 and <= 16);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(20);
        }

        return true;
    }

    private sealed class RecordingProvider : ITranslationProvider
    {
        public List<TranslationRequest> Requests { get; } = [];

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new TranslationResult("测试翻译。"));
        }
    }

    private sealed class RecordingEventSink : ITranslationEventSink
    {
        public List<TranslationItem> Items { get; } = [];

        public ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken)
        {
            Items.Add(item);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingProvider : ITranslationProvider
    {
        private readonly TaskCompletionSource<TranslationResult> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return _result.Task;
        }
    }
}
