using Microsoft.Win32.SafeHandles;

namespace TerminalTranslator.Windows.ConPty;

public sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePseudoConsoleHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.ClosePseudoConsole(handle);
        return true;
    }
}
