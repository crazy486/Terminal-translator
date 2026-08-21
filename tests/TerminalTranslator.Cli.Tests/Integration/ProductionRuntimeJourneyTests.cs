using System.CommandLine;
using System.Text;
using System.Text.Json;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Windows.ConPty;
using TerminalTranslator.Windows.Console;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class ProductionRuntimeJourneyTests
{
    [TestMethod]
    public async Task AnalysisChunksInsideIdleWindow_AreOneLogicalCandidate()
    {
        Guid session = Guid.NewGuid();
        RecordingProvider provider = new();
        RecordingEventSink sink = new();
        await using ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            session, provider, sink, TimeSpan.FromSeconds(2));
        pipeline.Enable();

        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes("The production runtime candidate must remain one log")));
        await Task.Delay(50);
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes("ical line after a visual wrap.\r\n")));
        await WaitUntilAsync(() => provider.Requests.Count > 0, TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, provider.Requests.Count);
        Assert.AreEqual(
            "The production runtime candidate must remain one logical line after a visual wrap.",
            provider.Requests.Single().SourceText);
    }

    [TestMethod]
    public async Task RealInteractiveConPty_AfterControlEnable_ReachesProductionProvider()
    {
        Guid session = Guid.NewGuid();
        string sessionId = session.ToString("N");
        string nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        string controlPipe = SessionPipeNames.Control(sessionId, nonce);
        const string fingerprint = "interactive-fake-provider";
        AwaitableProvider provider = new();
        RecordingEventSink sink = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
        await using ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            session, provider, sink, TimeSpan.FromSeconds(2), viewportColumns: 60);
        await using MinimalControlPipeServer controlServer = new(
            controlPipe, sessionId, fingerprint,
            _ =>
            {
                pipeline.Enable();
                return Task.CompletedTask;
            });
        Task controlTask = controlServer.RunAsync(cancellation.Token);
        await using ConPtySession conPty = ConPtySession.StartPowerShell(
            Environment.CurrentDirectory,
            new Coord(60, 20));
        await using MemoryStream programOutput = new();
        Task outputTask = new ConsoleOutputRelay(conPty.Output, programOutput, pipeline)
            .CopyAsync(cancellation.Token);

        bool prompt = await WaitUntilAsync(
            () => Encoding.UTF8.GetString(programOutput.ToArray()).Contains("PS ", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        bool enabled = await new MinimalEnableRequestSender(controlPipe).SendAsync(
            new ControlRequestMessage("enable", SessionProtocol.Version, sessionId, fingerprint, true),
            cancellation.Token);
        await conPty.Input.WriteAsync(
            Encoding.UTF8.GetBytes("Write-Output 'The build failed because a required configuration file is missing.'\r\n"),
            cancellation.Token);
        await conPty.Input.FlushAsync(cancellation.Token);
        Task providerOrTimeout = await Task.WhenAny(
            provider.ExpectedRequest.Task,
            Task.Delay(TimeSpan.FromSeconds(5)));

        string[] acceptanceCommands =
        [
            "Write-Error 'The configuration file is missing and the application cannot start.'\r\n",
            "Write-Warning 'The build may fail because the configuration is incomplete.'\r\n",
            "Write-Output 'ERROR: The application failed to start because the required configuration file could not be found.'\r\n",
            "Write-Output 'Unicode round trip: 你好，世界'\r\n",
            "Write-Output 'modified: ProductionRuntimeJourneyTests.cs RunnableUserStory1AcceptanceTests.cs'\r\n",
        ];
        foreach (string acceptanceCommand in acceptanceCommands)
        {
            await conPty.Input.WriteAsync(Encoding.UTF8.GetBytes(acceptanceCommand), cancellation.Token);
            await conPty.Input.FlushAsync(cancellation.Token);
            await Task.Delay(400, cancellation.Token);
        }

        await conPty.Input.WriteAsync("exit 0\r\n"u8.ToArray(), cancellation.Token);
        await conPty.Input.FlushAsync(cancellation.Token);
        int exitCode = await conPty.WaitForExitAsync(cancellation.Token);
        await conPty.CompleteInputAsync();
        conPty.ClosePseudoConsole();
        await outputTask.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await controlTask;

        string escapedOutput = Escape(programOutput.ToArray());
        Assert.IsTrue(prompt, escapedOutput);
        Assert.IsTrue(enabled);
        Assert.AreEqual(0, exitCode);
        Assert.AreSame(provider.ExpectedRequest.Task, providerOrTimeout, escapedOutput);
        Assert.AreEqual(
            "The build failed because a required configuration file is missing.",
            (await provider.ExpectedRequest.Task).SourceText);
        Assert.IsGreaterThanOrEqualTo(1, sink.Items.Count);
        string allCandidates = string.Join(" || ", provider.Requests);
        StringAssert.Contains(allCandidates, "The build failed because a required configuration file is missing.");
        StringAssert.Contains(allCandidates, "The configuration file is missing and the application cannot start.");
        StringAssert.Contains(allCandidates, "The build may fail because the configuration is incomplete.");
        StringAssert.Contains(allCandidates, "ERROR: The application failed to start because the required configuration file could not be found.");
        StringAssert.Contains(allCandidates, "Unicode round trip: 你好，世界");
        StringAssert.Contains(allCandidates, "modified: ProductionRuntimeJourneyTests.cs RunnableUserStory1AcceptanceTests.cs");
        Assert.IsFalse(provider.Requests.Any(request => request.StartsWith('>')), allCandidates);
        CollectionAssert.Contains(provider.Requests, "Unicode round trip: 你好，世界");
        CollectionAssert.Contains(
            provider.Requests,
            "modified: ProductionRuntimeJourneyTests.cs RunnableUserStory1AcceptanceTests.cs");
    }

    [TestMethod]
    public async Task ProviderTimeout_AfterControlEnable_PublishesSafeStatusAndContinues()
    {
        Guid session = Guid.NewGuid();
        string sessionId = session.ToString("N");
        string nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        string eventPipe = SessionPipeNames.Event(sessionId, nonce);
        string controlPipe = SessionPipeNames.Control(sessionId, nonce);
        const string fingerprint = "timeout-then-success";
        TimeoutThenSuccessProvider provider = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        await using EventPipeServer eventServer = new(eventPipe, sessionId, nonce);
        Task eventReady = eventServer.WaitForClientAsync(cancellation.Token);
        await using EventPipeClient companion = await EventPipeClient.ConnectAsync(
            eventPipe, sessionId, nonce, TimeSpan.FromSeconds(2), cancellation.Token);
        await eventReady;
        await using ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            session,
            provider,
            eventServer,
            TimeSpan.FromSeconds(2),
            (code, token) => new ValueTask(eventServer.PublishAsync(
                new StatusEventMessage("provider-error", SessionProtocol.Version, code == TranslationErrorCode.Timeout ? "timeout" : "unexpected"),
                token)));
        await using MinimalControlPipeServer controlServer = new(
            controlPipe,
            sessionId,
            fingerprint,
            async token =>
            {
                pipeline.Enable();
                await eventServer.PublishAsync(
                    new StateEventMessage("state", SessionProtocol.Version, "enabled", "fake.local", "fake"),
                    token);
            });
        Task controlTask = controlServer.RunAsync(cancellation.Token);
        Task<JsonDocument?> stateTask = companion.ReadEventAsync(cancellation.Token);
        bool enabled = await new MinimalEnableRequestSender(controlPipe).SendAsync(
            new ControlRequestMessage("enable", SessionProtocol.Version, sessionId, fingerprint, true),
            cancellation.Token);
        using JsonDocument state = (await stateTask)!;

        byte[] first = Encoding.UTF8.GetBytes("The first build failed because configuration is missing.\r\n");
        await new ConsoleOutputRelay(new MemoryStream(first), Stream.Null, pipeline).CopyAsync(cancellation.Token);
        using JsonDocument status = (await companion.ReadEventAsync(cancellation.Token))!;
        byte[] second = Encoding.UTF8.GetBytes("The second build failed because a package is missing.\r\n");
        await new ConsoleOutputRelay(new MemoryStream(second), Stream.Null, pipeline).CopyAsync(cancellation.Token);
        using JsonDocument translation = (await companion.ReadEventAsync(cancellation.Token))!;

        Assert.IsTrue(enabled);
        Assert.AreEqual("state", state.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("provider-error", status.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("timeout", status.RootElement.GetProperty("code").GetString());
        Assert.AreEqual("translation", translation.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(2, provider.RequestCount);

        cancellation.Cancel();
        await controlTask;
    }

    [TestMethod]
    public async Task ProductionComposition_DisabledThenAuthenticatedEnable_PublishesChineseEvent()
    {
        Guid session = Guid.NewGuid();
        string sessionId = session.ToString("N");
        string nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        string eventPipe = SessionPipeNames.Event(sessionId, nonce);
        string controlPipe = SessionPipeNames.Control(sessionId, nonce);
        const string fingerprint = "test-provider-fingerprint";
        RecordingProvider provider = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        await using EventPipeServer eventServer = new(eventPipe, sessionId, nonce);
        Task serverReady = eventServer.WaitForClientAsync(cancellation.Token);
        await using EventPipeClient companion = await EventPipeClient.ConnectAsync(
            eventPipe, sessionId, nonce, TimeSpan.FromSeconds(2), cancellation.Token);
        await serverReady;
        await using ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            session, provider, eventServer, TimeSpan.FromSeconds(2));
        await using MinimalControlPipeServer controlServer = new(
            controlPipe,
            sessionId,
            fingerprint,
            async token =>
            {
                pipeline.Enable();
                _ = eventServer.PublishAsync(
                    new StateEventMessage("state", SessionProtocol.Version, "enabled", "fake.local", "fake"), token);
                await Task.CompletedTask;
            });
        Task controlTask = controlServer.RunAsync(cancellation.Token);

        byte[] disabled = Encoding.UTF8.GetBytes("This disabled output must remain local.\r\n");
        await using MemoryStream disabledProgramOutput = new();
        await new ConsoleOutputRelay(new MemoryStream(disabled), disabledProgramOutput, pipeline)
            .CopyAsync(cancellation.Token);
        await Task.Delay(200, cancellation.Token);
        Assert.AreEqual(0, provider.Requests.Count);
        CollectionAssert.AreEqual(disabled, disabledProgramOutput.ToArray());

        bool enabled = await new MinimalEnableRequestSender(controlPipe).SendAsync(
            new ControlRequestMessage("enable", SessionProtocol.Version, sessionId, fingerprint, true),
            cancellation.Token);
        Assert.IsTrue(enabled);
        using JsonDocument state = (await companion.ReadEventAsync(cancellation.Token))!;
        Assert.AreEqual("state", state.RootElement.GetProperty("type").GetString());

        byte[] source = Encoding.UTF8.GetBytes("The build failed because a required file is missing.\r\n");
        await using MemoryStream programOutput = new();
        await new ConsoleOutputRelay(new MemoryStream(source), programOutput, pipeline)
            .CopyAsync(cancellation.Token);
        using JsonDocument translation = (await companion.ReadEventAsync(cancellation.Token))!;

        Assert.AreEqual(1, provider.Requests.Count);
        Assert.AreEqual("translation", translation.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("构建失败，因为缺少必需文件。", translation.RootElement.GetProperty("translatedText").GetString());
        CollectionAssert.AreEqual(source, programOutput.ToArray());

        cancellation.Cancel();
        await controlTask;
    }

    [TestMethod]
    public async Task OnCommand_ConsentSendsEnableToCurrentHost()
    {
        using TemporaryDirectory temporary = new();
        ProviderSettings settings = ProviderSettings.Create(
            new Uri("https://fake.local/chat/completions"),
            "fake",
            "TT_TEST_PROVIDER_KEY",
            TimeSpan.FromSeconds(1));
        ProviderSettingsStore store = new(temporary.Path);
        await store.SaveAsync(settings);
        string sessionId = Guid.NewGuid().ToString("N");
        string nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        string? oldSession = Environment.GetEnvironmentVariable("TT_SESSION_ID");
        string? oldNonce = Environment.GetEnvironmentVariable("TT_SESSION_NONCE");
        string? oldKey = Environment.GetEnvironmentVariable("TT_TEST_PROVIDER_KEY");
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        bool hostEnabled = false;
        await using MinimalControlPipeServer server = new(
            SessionPipeNames.Control(sessionId, nonce),
            sessionId,
            settings.Fingerprint,
            _ =>
            {
                hostEnabled = true;
                return Task.CompletedTask;
            });
        Task serverTask = server.RunAsync(cancellation.Token);
        try
        {
            Environment.SetEnvironmentVariable("TT_SESSION_ID", sessionId);
            Environment.SetEnvironmentVariable("TT_SESSION_NONCE", nonce);
            Environment.SetEnvironmentVariable("TT_TEST_PROVIDER_KEY", "not-a-real-secret");
            using StringReader input = new("y\n");
            using StringWriter output = new();
            using StringWriter error = new();

            int exitCode = await OnCommand.Create(store, input, output, error).Parse([]).InvokeAsync();

            Assert.AreEqual(0, exitCode, error.ToString());
            Assert.IsTrue(hostEnabled);
            StringAssert.Contains(output.ToString(), "Translation enabled.");
        }
        finally
        {
            cancellation.Cancel();
            Environment.SetEnvironmentVariable("TT_SESSION_ID", oldSession);
            Environment.SetEnvironmentVariable("TT_SESSION_NONCE", oldNonce);
            Environment.SetEnvironmentVariable("TT_TEST_PROVIDER_KEY", oldKey);
            try { await serverTask; } catch (OperationCanceledException) { }
        }
    }

    private sealed class RecordingProvider : ITranslationProvider
    {
        public List<TranslationRequest> Requests { get; } = [];

        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new TranslationResult("构建失败，因为缺少必需文件。", "fake-1"));
        }
    }

    private sealed class AwaitableProvider : ITranslationProvider
    {
        private const string ExpectedText = "The build failed because a required configuration file is missing.";

        public TaskCompletionSource<TranslationRequest> ExpectedRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Requests { get; } = [];

        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request.SourceText);
            if (request.SourceText == ExpectedText)
            {
                ExpectedRequest.TrySetResult(request);
            }

            return Task.FromResult(new TranslationResult("构建失败，因为缺少必需的配置文件。", "fake-interactive"));
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

    private sealed class TimeoutThenSuccessProvider : ITranslationProvider
    {
        public int RequestCount { get; private set; }

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
            {
                throw new TranslationProviderException(TranslationErrorCode.Timeout);
            }

            return Task.FromResult(new TranslationResult("第二次构建失败，因为缺少包。", "fake-recovered"));
        }
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

    private static string Escape(byte[] bytes) => Encoding.UTF8.GetString(bytes)
        .Replace("\u001b", "<ESC>", StringComparison.Ordinal)
        .Replace("\r", "<CR>", StringComparison.Ordinal)
        .Replace("\n", "<LF>", StringComparison.Ordinal);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-runtime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
