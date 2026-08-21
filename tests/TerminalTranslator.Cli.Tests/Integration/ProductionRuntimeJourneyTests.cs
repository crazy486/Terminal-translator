using System.CommandLine;
using System.Text;
using System.Text.Json;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Windows.Console;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class ProductionRuntimeJourneyTests
{
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
