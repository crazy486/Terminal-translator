using System.IO.Pipes;
using System.Text;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class SessionPipeSecurityTests
{
    [TestMethod]
    public void CurrentUserFactory_RequiresCurrentUserAndRejectsRemoteClientsByPolicy()
    {
        Assert.IsTrue(CurrentUserPipeFactory.ServerOptions.HasFlag(PipeOptions.CurrentUserOnly));
        Assert.IsFalse(CurrentUserPipeFactory.AllowsRemoteClients);
    }

    [TestMethod]
    public async Task CurrentUserFactory_AllowsLocalSameUserConnection()
    {
        string pipeName = $"tt-security-{Guid.NewGuid():N}";
        await using NamedPipeServerStream server = CurrentUserPipeFactory.CreateServer(
            pipeName, PipeDirection.InOut, maxInstances: 1);
        await using NamedPipeClientStream client = new(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task wait = server.WaitForConnectionAsync();

        await client.ConnectAsync(1000);
        await wait.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(server.IsConnected);
        Assert.IsTrue(client.IsConnected);
    }

    [TestMethod]
    [DataRow(2, "expected-session", "expected-nonce", "companion")]
    [DataRow(1, "wrong-session", "expected-nonce", "companion")]
    [DataRow(1, "expected-session", "wrong-nonce", "companion")]
    [DataRow(1, "expected-session", "expected-nonce", "host")]
    public void HandshakeValidator_RejectsProtocolSessionNonceAndRoleMismatch(
        int protocol,
        string sessionId,
        string nonce,
        string role)
    {
        HelloMessage hello = new("hello", protocol, sessionId, nonce, role);

        Assert.IsFalse(SessionHandshakeValidator.IsValid(
            hello,
            "expected-session",
            "expected-nonce",
            ["companion"]));
    }

    [TestMethod]
    public void HandshakeValidator_AcceptsOnlyExplicitAllowedRole()
    {
        HelloMessage hello = new(
            "hello", SessionProtocol.Version, "session", "nonce-value", "control");

        Assert.IsTrue(SessionHandshakeValidator.IsValid(
            hello, "session", "nonce-value", ["control"]));
        Assert.IsFalse(SessionHandshakeValidator.IsValid(
            hello, "session", "nonce-value", ["companion"]));
    }

    [TestMethod]
    public async Task EventPipe_RejectsMalformedHandshakeWithoutEchoingInput()
    {
        string suppliedValue = "do-not-echo-this-value";
        Exception exception = await SendInvalidHandshakeAsync(
            Encoding.UTF8.GetBytes($"{{\"unexpected\":\"{suppliedValue}\"}}\n"));

        Assert.IsInstanceOfType<InvalidDataException>(exception);
        Assert.IsFalse(exception.Message.Contains(suppliedValue, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EventPipe_RejectsMessageLargerThan32KiB()
    {
        byte[] oversized = Encoding.UTF8.GetBytes(
            new string('x', SessionProtocol.MaximumMessageBytes + 1) + "\n");

        Exception exception = await SendInvalidHandshakeAsync(oversized);

        Assert.IsInstanceOfType<InvalidDataException>(exception);
        StringAssert.Contains(exception.Message, "maximum size");
    }

    private static async Task<Exception> SendInvalidHandshakeAsync(byte[] payload)
    {
        string sessionId = Guid.NewGuid().ToString("N");
        string nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray()) +
            Convert.ToHexString(Guid.NewGuid().ToByteArray());
        string pipeName = SessionPipeNames.Event(sessionId, nonce);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(3));
        await using EventPipeServer server = new(pipeName, sessionId, nonce);
        Task serverTask = server.WaitForClientAsync(cancellation.Token);
        await using NamedPipeClientStream client = new(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(1000, cancellation.Token);
        Task writeTask = Task.Run(async () =>
        {
            await client.WriteAsync(payload, cancellation.Token);
            await client.FlushAsync(cancellation.Token);
        }, cancellation.Token);

        try
        {
            await serverTask;
            throw new AssertFailedException("Invalid handshake unexpectedly succeeded.");
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            client.Dispose();
            try
            {
                await writeTask;
            }
            catch (Exception writeException) when (writeException is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }

            return exception;
        }
    }
}
