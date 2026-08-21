using System.IO.Pipes;

namespace TerminalTranslator.Windows.Ipc;

public static class CurrentUserPipeFactory
{
    public const PipeOptions ServerOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    public static bool AllowsRemoteClients => false;

    public static NamedPipeServerStream CreateServer(
        string pipeName,
        PipeDirection direction,
        int maxInstances = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        return new NamedPipeServerStream(
            pipeName,
            direction,
            maxInstances,
            PipeTransmissionMode.Byte,
            ServerOptions);
    }

    public static NamedPipeClientStream CreateClient(string pipeName, PipeDirection direction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        return new NamedPipeClientStream(
            ".",
            pipeName,
            direction,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
}
