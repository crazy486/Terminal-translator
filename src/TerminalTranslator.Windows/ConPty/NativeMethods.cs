using System.Runtime.InteropServices;

namespace TerminalTranslator.Windows.ConPty;

[StructLayout(LayoutKind.Sequential)]
public readonly struct Coord(short x, short y)
{
    public short X { get; } = x;

    public short Y { get; } = y;
}

internal static partial class NativeMethods
{
    internal const int ProcThreadAttributePseudoConsole = 0x00020016;

    [LibraryImport("kernel32.dll")]
    internal static partial int CreatePseudoConsole(
        Coord size,
        nint input,
        nint output,
        uint flags,
        out SafePseudoConsoleHandle pseudoConsole);

    [LibraryImport("kernel32.dll")]
    internal static partial int ResizePseudoConsole(SafePseudoConsoleHandle pseudoConsole, Coord size);

    [LibraryImport("kernel32.dll")]
    internal static partial void ClosePseudoConsole(nint pseudoConsole);
}
