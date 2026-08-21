using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TerminalTranslator.Windows.Console;

public sealed partial class ConsoleModeScope : IDisposable
{
    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableVirtualTerminalInput = 0x0200;
    private readonly SafeFileHandle _inputHandle;
    private readonly uint _originalMode;
    private bool _disposed;

    private ConsoleModeScope(SafeFileHandle inputHandle, uint originalMode)
    {
        _inputHandle = inputHandle;
        _originalMode = originalMode;
    }

    public static ConsoleModeScope? TryEnterRawInput()
    {
        SafeFileHandle handle = new(GetStdHandle(-10), ownsHandle: false);
        if (handle.IsInvalid || !GetConsoleMode(handle, out uint originalMode))
        {
            handle.Dispose();
            return null;
        }

        uint rawMode = (originalMode | EnableVirtualTerminalInput) &
            ~(EnableProcessedInput | EnableLineInput | EnableEchoInput);
        if (!SetConsoleMode(handle, rawMode))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Unable to configure terminal input mode.");
        }

        return new ConsoleModeScope(handle, originalMode);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = SetConsoleMode(_inputHandle, _originalMode);
        _inputHandle.Dispose();
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(SafeFileHandle consoleHandle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(SafeFileHandle consoleHandle, uint mode);
}
