using System.Diagnostics;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Windows.Ipc;
using TerminalTranslator.Windows.Terminal;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class SessionStartupTests
{
    [TestMethod]
    public void Launcher_UsesIdenticalSessionAndNonceForBothPanes()
    {
        const string sessionId = "bd21ea6cea604b9a9c3a8b97c8b19821";
        const string nonce = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        ProcessStartInfo startInfo = WindowsTerminalLauncher.CreateStartInfo(
            @"C:\tools\tt.exe",
            sessionId,
            nonce,
            @"C:\work");
        string[] arguments = startInfo.ArgumentList.ToArray();

        Assert.AreEqual(sessionId, startInfo.Environment["TT_SESSION_ID"]);
        Assert.AreEqual(nonce, startInfo.Environment["TT_SESSION_NONCE"]);
        CollectionAssert.AreEqual(
            new[] { sessionId, sessionId },
            arguments
                .Select((value, index) => (value, index))
                .Where(item => item.value == "--session")
                .Select(item => arguments[item.index + 1])
                .ToArray());
        Assert.AreEqual(
            SessionPipeNames.Event(sessionId, nonce),
            SessionPipeNames.Event(startInfo.Environment["TT_SESSION_ID"]!, startInfo.Environment["TT_SESSION_NONCE"]!));
    }

    [TestMethod]
    [DataRow(false, DisplayName = "host starts before companion")]
    [DataRow(true, DisplayName = "companion starts before host")]
    public async Task HostAndCompanion_HandshakeRegardlessOfPaneStartupOrder(bool companionStartsFirst)
    {
        using TemporaryDirectory temporary = new();
        string sessionId = Guid.NewGuid().ToString("N");
        string nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        string pipeName = SessionPipeNames.Event(sessionId, nonce);
        using CancellationTokenSource sessionCancellation = new(TimeSpan.FromSeconds(5));
        using StringWriter hostError = new();

        Task<EventPipeClient>? clientTask = null;
        if (companionStartsFirst)
        {
            clientTask = EventPipeClient.ConnectAsync(
                pipeName,
                sessionId,
                nonce,
                TimeSpan.FromSeconds(2),
                sessionCancellation.Token);
            Assert.IsFalse(clientTask.IsCompleted);
        }

        Task<int> hostTask = HostCommand.RunEventServerAsync(
            sessionId,
            sessionId,
            nonce,
            temporary.Path,
            hostError,
            sessionCancellation.Token);
        Assert.IsFalse(hostTask.IsCompleted);

        clientTask ??= EventPipeClient.ConnectAsync(
            pipeName,
            sessionId,
            nonce,
            TimeSpan.FromSeconds(2),
            sessionCancellation.Token);
        await using EventPipeClient client = await clientTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.AreEqual("disabled", client.InitialState);
        Assert.IsFalse(hostTask.IsCompleted);
        Assert.AreEqual(string.Empty, hostError.ToString());

        sessionCancellation.Cancel();
        Assert.AreEqual(0, await hostTask.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    public async Task Companion_WhenHostNeverListens_ReturnsActionableErrorWithoutStackTrace()
    {
        string sessionId = Guid.NewGuid().ToString("N");
        string nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await CompanionCommand.RunAsync(
            sessionId,
            sessionId,
            nonce,
            output,
            error,
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.AreEqual(6, exitCode);
        StringAssert.Contains(error.ToString(), "could not connect to the session host");
        Assert.IsFalse(error.ToString().Contains("System.IO", StringComparison.Ordinal));
        Assert.IsFalse(error.ToString().Contains(" at ", StringComparison.Ordinal));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-startup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
