using System.Text;
using System.Text.Json;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Windows.ConPty;
using TerminalTranslator.Windows.Console;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class RunnableUserStory1AcceptanceTests
{
    [TestMethod]
    public async Task RealConPtyProductionPipeline_ReachesCompanionThroughEventPipe()
    {
        Guid session = Guid.NewGuid();
        string sessionId = session.ToString("N");
        string nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        string pipeName = SessionPipeNames.Event(sessionId, nonce);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        FakeProvider provider = new();

        await using EventPipeServer server = new(pipeName, sessionId, nonce);
        Task serverReady = server.WaitForClientAsync(cancellation.Token);
        await using EventPipeClient companion = await EventPipeClient.ConnectAsync(
            pipeName, sessionId, nonce, TimeSpan.FromSeconds(2), cancellation.Token);
        await serverReady;
        await using ProductionTranslationPipeline pipeline = ProductionRuntimeComposition.CreateTranslationPipeline(
            session, provider, server, TimeSpan.FromSeconds(2));
        pipeline.Enable();
        await using ConPtySession conPty = ConPtySession.StartPowerShell(Environment.CurrentDirectory);
        await using MemoryStream programPane = new();
        Task outputTask = new ConsoleOutputRelay(conPty.Output, programPane, pipeline)
            .CopyAsync(cancellation.Token);

        string command = "Write-Output 'The production build failed with a useful error message.'; exit 0\r\n";
        await new ConsoleInputRelay(new MemoryStream(Encoding.UTF8.GetBytes(command)), conPty.Input)
            .CopyAsync(cancellation.Token);
        int exitCode = await conPty.WaitForExitAsync(cancellation.Token);
        await conPty.CompleteInputAsync();
        conPty.ClosePseudoConsole();
        await outputTask.WaitAsync(TimeSpan.FromSeconds(5));
        using JsonDocument translation = (await companion.ReadEventAsync(cancellation.Token))!;

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(Encoding.UTF8.GetString(programPane.ToArray()), "production build failed");
        Assert.AreEqual("translation", translation.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("生产构建失败，并显示了有用的错误消息。", translation.RootElement.GetProperty("translatedText").GetString());
        Assert.IsGreaterThanOrEqualTo(1, provider.RequestCount);
    }

    private sealed class FakeProvider : ITranslationProvider
    {
        public int RequestCount { get; private set; }

        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new TranslationResult("生产构建失败，并显示了有用的错误消息。", "fake-conpty"));
        }
    }
}
