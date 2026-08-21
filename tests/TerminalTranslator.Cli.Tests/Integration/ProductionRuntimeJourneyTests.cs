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
    public async Task HereStringEchoAndPrompt_DoNotHideOrContaminateThreeOutputLines()
    {
        Guid session = Guid.NewGuid();
        SlowRecordingProvider provider = new();
        RecordingEventSink sink = new();
        SubmittedCommandTracker submittedCommands = new();
        await using ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            session,
            provider,
            sink,
            TimeSpan.FromSeconds(2),
            analysisLineFilter: submittedCommands.ClassifyAnalysisLine);
        pipeline.Enable();

        const string input =
            "@\"\r\n" +
            "The server is running normally.\r\n" +
            "Three background tasks are currently active.\r\n" +
            "No critical errors were detected.\r\n" +
            "\"@\r\n";
        submittedCommands.Observe(Encoding.UTF8.GetBytes(input));

        const string conPtyOutput =
            "PS C:\\work> @\"\r\n" +
            ">> The server is running normally.\r\n" +
            ">> Three background tasks are currently active.\r\n" +
            ">> No critical errors were detected.\r\n" +
            ">> \"@\r\n" +
            ">> The server is running normally.\r\n" +
            ">> Three background tasks are currently active.\r\n" +
            ">> No critical errors were detected.\r\n" +
            ">> \"@\r\n" +
            "The server is running normally.\r\n" +
            "Three background tasks are currently active.\r\n" +
            "No critical errors were detected.\r\n" +
            "PS C:\\work>";
        byte[] rawBytes = Encoding.UTF8.GetBytes(conPtyOutput);
        await using MemoryStream programOutput = new();

        await new ConsoleOutputRelay(new MemoryStream(rawBytes), programOutput, pipeline)
            .CopyAsync(CancellationToken.None);
        Assert.IsTrue(await WaitUntilAsync(() => sink.Items.Count >= 1, TimeSpan.FromSeconds(3)));
        await Task.Delay(200);

        CollectionAssert.AreEqual(
            new[]
            {
                "The server is running normally.\n" +
                "Three background tasks are currently active.\n" +
                "No critical errors were detected.",
            },
            provider.Requests.Select(request => request.SourceText).ToArray(),
            string.Join(" || ", provider.Requests.Select(request => request.SourceText)));
        Assert.IsFalse(provider.Requests.Any(request => request.SourceText.Contains(">>", StringComparison.Ordinal)));
        Assert.IsFalse(provider.Requests.Any(request => request.SourceText.Contains("PS C:\\work>", StringComparison.Ordinal)));
        Assert.AreEqual(1, sink.Items.Count);
        Assert.AreEqual(3, sink.Items.Single().TranslatedText.Split('\n').Length);
        CollectionAssert.AreEqual(rawBytes, programOutput.ToArray());
    }

    [TestMethod]
    public async Task AnalysisSidePromptFiltering_HandlesPromptBoundariesAndKeepsGreaterThanOutput()
    {
        Guid session = Guid.NewGuid();
        RecordingProvider provider = new();
        RecordingEventSink sink = new();
        await using ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            session, provider, sink, TimeSpan.FromSeconds(2));
        pipeline.Enable();

        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes("PS C:\\Users\\alice>")));
        await Task.Delay(180);
        Assert.AreEqual(0, provider.Requests.Count);

        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(
            "Translation resumed.\r\nPS D:\\changed working directory>")));
        Assert.IsTrue(await WaitUntilAsync(() => provider.Requests.Count >= 1, TimeSpan.FromSeconds(2)));

        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(">> ")));
        await Task.Delay(180);
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(
            "The measured value > the configured threshold.\r\n")));
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(
            "The diagnostic contains PS C:\\work> as ordinary program output.\r\n")));
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(
            "PS C:\\> this is ordinary output\r\n" +
            "The report contains > and >> plus PS markers.\r\n")));
        Assert.IsTrue(await WaitUntilAsync(() => provider.Requests.Count >= 2, TimeSpan.FromSeconds(2)));

        CollectionAssert.AreEqual(
            new[]
            {
                "Translation resumed.",
                "The measured value > the configured threshold.\n" +
                "The diagnostic contains PS C:\\work> as ordinary program output.\n" +
                "PS C:\\> this is ordinary output\n" +
                "The report contains > and >> plus PS markers.",
            },
            provider.Requests.Select(request => request.SourceText).ToArray(),
            string.Join(" || ", provider.Requests.Select(request => request.SourceText)));
    }

    [TestMethod]
    [DataRow(30)]
    [DataRow(180)]
    public async Task MultipleSubmittedCommands_FilterEveryPromptTailEchoAcrossIdleBoundaries(int intervalMilliseconds)
    {
        Guid session = Guid.NewGuid();
        RecordingProvider provider = new();
        RecordingEventSink sink = new();
        SubmittedCommandTracker submittedCommands = new();
        await using ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            session,
            provider,
            sink,
            TimeSpan.FromSeconds(2),
            analysisLineFilter: submittedCommands.ClassifyAnalysisLine);
        pipeline.Enable();

        string[] commands =
        [
            "Write-Output \"The first operation completed successfully.\"",
            "Write-Output \"The second operation completed successfully.\"",
            "Write-Output \"The third operation completed successfully.\"",
        ];
        submittedCommands.Observe(Encoding.UTF8.GetBytes(string.Join("\r\n", commands) + "\r\n"));

        string[] outputs =
        [
            "The first operation completed successfully.",
            "The second operation completed successfully.",
            "The third operation completed successfully.",
        ];
        string[] directories = ["C:\\", "D:\\changed", "D:\\changed\\again"];
        await using MemoryStream programOutput = new();
        for (int index = 0; index < commands.Length; index++)
        {
            string raw =
                $"PS {directories[index]}> {commands[index]}\r\n" +
                $"PS {directories[index]}> {commands[index]}\r\n" +
                outputs[index] + "\r\n";
            await new ConsoleOutputRelay(
                new MemoryStream(Encoding.UTF8.GetBytes(raw)),
                programOutput,
                pipeline).CopyAsync(CancellationToken.None);
            await Task.Delay(intervalMilliseconds);
        }

        const string finalPrompt = "PS D:\\changed\\again>";
        await new ConsoleOutputRelay(
            new MemoryStream(Encoding.UTF8.GetBytes(finalPrompt)),
            programOutput,
            pipeline).CopyAsync(CancellationToken.None);

        Assert.IsTrue(await WaitUntilAsync(
            () => string.Join('\n', provider.Requests.Select(request => request.SourceText))
                .Contains(outputs[^1], StringComparison.Ordinal),
            TimeSpan.FromSeconds(2)));

        string allSources = string.Join('\n', provider.Requests.Select(request => request.SourceText));
        foreach (string output in outputs)
        {
            StringAssert.Contains(allSources, output);
        }

        Assert.IsFalse(allSources.Contains("PS ", StringComparison.Ordinal), allSources);
        Assert.IsFalse(allSources.Contains("Write-Output", StringComparison.Ordinal), allSources);
        string expectedRaw = string.Concat(commands.Select((command, index) =>
            $"PS {directories[index]}> {command}\r\n" +
            $"PS {directories[index]}> {command}\r\n" +
            outputs[index] + "\r\n")) + finalPrompt;
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expectedRaw), programOutput.ToArray());
    }

    [TestMethod]
    public async Task TerminalTranslatorControlCommands_DoNotFeedTheirOwnedOutputBackToProvider()
    {
        Guid session = Guid.NewGuid();
        RecordingProvider provider = new();
        RecordingEventSink sink = new();
        SubmittedCommandTracker submittedCommands = new();
        await using ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            session,
            provider,
            sink,
            TimeSpan.FromSeconds(2),
            analysisLineFilter: submittedCommands.ClassifyAnalysisLine);

        submittedCommands.Observe("tt on\r\n"u8.ToArray());
        pipeline.Enable();
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(
            "Translation resumed.\r\nPS C:\\work>")));
        await Task.Delay(180);

        submittedCommands.Observe("tt status\r\n"u8.ToArray());
        Assert.IsTrue(pipeline.TryOffer(Encoding.UTF8.GetBytes(
            "PS D:\\changed> tt status\r\n" +
            "session: active\r\n" +
            "translation: enabled\r\n" +
            "provider: fake.local, fake-model\r\n" +
            "queue: high=0 normal=0\r\n" +
            "privacy-skipped: 0\r\n" +
            "overload-dropped: 0\r\n" +
            "PS D:\\changed>")));
        await Task.Delay(180);

        submittedCommands.Observe("tt off\r\n"u8.ToArray());
        pipeline.Disable();
        Assert.IsFalse(pipeline.TryOffer(Encoding.UTF8.GetBytes("Translation disabled.\r\n")));
        Assert.AreEqual(0, provider.Requests.Count, string.Join(" || ", provider.Requests));
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
        SubmittedCommandTracker submittedCommands = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
        await using ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            session,
            provider,
            sink,
            TimeSpan.FromSeconds(2),
            viewportColumns: 60,
            analysisLineFilter: submittedCommands.ClassifyAnalysisLine);
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
        await RelayInputAsync(
            conPty.Input,
            submittedCommands,
            "Write-Output 'The build failed because a required configuration file is missing.'\r\n",
            cancellation.Token);
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
            await RelayInputAsync(conPty.Input, submittedCommands, acceptanceCommand, cancellation.Token);
            await Task.Delay(400, cancellation.Token);
        }

        await RelayInputAsync(
            conPty.Input,
            submittedCommands,
            "$value = Read-Host \"Paste Unicode text\"\r\n",
            cancellation.Token);
        await Task.Delay(250, cancellation.Token);
        await RelayInputAsync(conPty.Input, submittedCommands, "high-fidelity-paste\r\n", cancellation.Token);
        await Task.Delay(400, cancellation.Token);
        await RelayInputAsync(
            conPty.Input,
            submittedCommands,
            "Write-Output 'The captured value remains ordinary English output.'\r\n",
            cancellation.Token);
        await Task.Delay(400, cancellation.Token);

        await RelayInputAsync(conPty.Input, submittedCommands, "exit 0\r\n", cancellation.Token);
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
        StringAssert.Contains(allCandidates, "The captured value remains ordinary English output.");
        Assert.IsFalse(provider.Requests.Any(request => request.StartsWith('>')), allCandidates);
        Assert.IsFalse(provider.Requests.Any(request => request.Contains("$value = Read-Host", StringComparison.Ordinal)), allCandidates);
        Assert.IsFalse(provider.Requests.Any(IsPowerShellPrompt), allCandidates);
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

            int exitCode = await OnCommand.Create(
                store,
                input,
                output,
                error,
                (_, _) => new MinimalEnableRequestSender(SessionPipeNames.Control(sessionId, nonce)))
                .Parse([]).InvokeAsync();

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

    private sealed class SlowRecordingProvider : ITranslationProvider
    {
        public List<TranslationRequest> Requests { get; } = [];

        public async Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.Delay(TimeSpan.FromMilliseconds(1700), cancellationToken);
            return new TranslationResult(
                "Translated line one.\nTranslated line two.\nTranslated line three.",
                "fake-slow");
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

    private static async Task RelayInputAsync(
        Stream pseudoConsoleInput,
        SubmittedCommandTracker submittedCommands,
        string text,
        CancellationToken cancellationToken)
    {
        await new ConsoleInputRelay(
            new MemoryStream(Encoding.UTF8.GetBytes(text)),
            pseudoConsoleInput,
            submittedCommands.Observe).CopyAsync(cancellationToken);
    }

    private static bool IsPowerShellPrompt(string candidate)
    {
        string trimmed = candidate.Trim();
        return trimmed.StartsWith("PS ", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith('>');
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
