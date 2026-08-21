using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Windows.ConPty;
using TerminalTranslator.Windows.Console;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class RunnableUserStory1AcceptanceTests
{
    [TestMethod]
    public async Task RealConPtyControlAndProductionProvider_ReachesCompanionThroughEventPipe()
    {
        using TemporaryDirectory temporary = new();
        ProviderSettingsStore store = new(temporary.Path);
        using StringWriter configureOutput = new();
        using StringWriter configureError = new();
        int configureExitCode = await ConfigureCommand.Create(store, configureOutput, configureError).Parse(
            [
                "--endpoint", "https://fake.local/chat/completions",
                "--model", "fake-delayed-model",
                "--api-key-env", "TT_FAKE_PROVIDER_KEY",
            ]).InvokeAsync();
        ProviderSettings settings = (await store.LoadAsync())!;

        Guid session = Guid.NewGuid();
        string sessionId = session.ToString("N");
        string nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        string eventPipeName = SessionPipeNames.Event(sessionId, nonce);
        string controlPipeName = SessionPipeNames.Control(sessionId, nonce);
        const string sourceText = "The build failed because a required configuration file is missing.";
        const string translatedText = "构建失败，因为缺少必需的配置文件。";
        TestHttpMessageHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1700), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(
                    $"{{\"id\":\"fake-e2e\",\"choices\":[{{\"message\":{{\"content\":\"{translatedText}\"}}}}]}}")),
            };
        });
        using HttpClient httpClient = new(handler);
        ChatCompletionTranslationProvider provider = new(settings, httpClient, _ => "fake-secret");
        SubmittedCommandTracker submittedCommands = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(25));

        await using EventPipeServer eventServer = new(eventPipeName, sessionId, nonce);
        Task eventReady = eventServer.WaitForClientAsync(cancellation.Token);
        await using EventPipeClient companion = await EventPipeClient.ConnectAsync(
            eventPipeName, sessionId, nonce, TimeSpan.FromSeconds(2), cancellation.Token);
        await eventReady;
        await using ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            session,
            provider,
            eventServer,
            settings.RequestTimeout,
            commandEchoFilter: submittedCommands.IsEcho);
        await using MinimalControlPipeServer controlServer = new(
            controlPipeName,
            sessionId,
            settings.Fingerprint,
            async token =>
            {
                pipeline.Enable();
                await eventServer.PublishAsync(
                    new StateEventMessage("state", SessionProtocol.Version, "enabled", settings.Endpoint.Host, settings.Model),
                    token);
            });
        Task controlTask = controlServer.RunAsync(cancellation.Token);
        await using ConPtySession conPty = ConPtySession.StartPowerShell(Environment.CurrentDirectory);
        await using MemoryStream programPane = new();
        Task outputTask = new ConsoleOutputRelay(conPty.Output, programPane, pipeline)
            .CopyAsync(cancellation.Token);

        bool promptObserved = false;
        bool enabled = false;
        int? childExitCode = null;
        JsonDocument? state = null;
        JsonDocument? translation = null;
        try
        {
            promptObserved = await WaitUntilAsync(
                () => Encoding.UTF8.GetString(programPane.ToArray()).Contains("PS ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            Task<JsonDocument?> stateTask = companion.ReadEventAsync(cancellation.Token);
            enabled = await new MinimalEnableRequestSender(controlPipeName).SendAsync(
                new ControlRequestMessage(
                    "enable", SessionProtocol.Version, sessionId, settings.Fingerprint, true),
                cancellation.Token);
            state = await stateTask;

            await RelayInputAsync(
                conPty.Input,
                submittedCommands,
                $"Write-Output '{sourceText}'\r\n",
                cancellation.Token);
            translation = await ReadTranslationForSourceAsync(
                companion, sourceText, TimeSpan.FromSeconds(7), cancellation.Token);
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            try
            {
                await RelayInputAsync(conPty.Input, submittedCommands, "exit 0\r\n", cleanup.Token);
                childExitCode = await conPty.WaitForExitAsync(cleanup.Token);
                await conPty.CompleteInputAsync();
                conPty.ClosePseudoConsole();
                await outputTask.WaitAsync(cleanup.Token);
            }
            finally
            {
                cancellation.Cancel();
                try
                {
                    await controlTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        using (state)
        using (translation)
        {
            string rawProgramOutput = Encoding.UTF8.GetString(programPane.ToArray());
            Assert.AreEqual(0, configureExitCode, configureError.ToString());
            Assert.AreEqual(ProviderSettings.DefaultRequestTimeout, settings.RequestTimeout);
            Assert.IsTrue(promptObserved, Escape(programPane.ToArray()));
            Assert.IsTrue(enabled);
            Assert.AreEqual(0, childExitCode);
            Assert.IsNotNull(state);
            Assert.AreEqual("state", state.RootElement.GetProperty("type").GetString());
            Assert.IsNotNull(translation);
            Assert.AreEqual("translation", translation.RootElement.GetProperty("type").GetString());
            Assert.AreEqual(sourceText, translation.RootElement.GetProperty("sourceText").GetString());
            Assert.AreEqual(translatedText, translation.RootElement.GetProperty("translatedText").GetString());
            StringAssert.Contains(rawProgramOutput, sourceText);
            Assert.IsGreaterThanOrEqualTo(1, handler.Requests.Count);
        }
    }

    private static async Task<JsonDocument?> ReadTranslationForSourceAsync(
        EventPipeClient companion,
        string sourceText,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (await companion.ReadEventAsync(deadline.Token) is JsonDocument document)
        {
            if (document.RootElement.TryGetProperty("type", out JsonElement type) &&
                type.GetString() == "translation" &&
                document.RootElement.TryGetProperty("sourceText", out JsonElement source) &&
                source.GetString() == sourceText)
            {
                return document;
            }

            document.Dispose();
        }

        return null;
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

    private static string Escape(byte[] bytes) => Encoding.UTF8.GetString(bytes)
        .Replace("\u001b", "<ESC>", StringComparison.Ordinal)
        .Replace("\r", "<CR>", StringComparison.Ordinal)
        .Replace("\n", "<LF>", StringComparison.Ordinal);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-e2e-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
