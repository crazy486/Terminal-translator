using System.Text;

namespace TerminalTranslator.Windows.Console;

public sealed class ConsoleEncodingScope : IDisposable
{
    private readonly Encoding _originalInputEncoding;
    private readonly Encoding _originalOutputEncoding;
    private bool _disposed;

    private ConsoleEncodingScope(Encoding originalInputEncoding, Encoding originalOutputEncoding)
    {
        _originalInputEncoding = originalInputEncoding;
        _originalOutputEncoding = originalOutputEncoding;
    }

    public static ConsoleEncodingScope EnterUtf8()
    {
        Encoding originalInputEncoding = global::System.Console.InputEncoding;
        Encoding originalOutputEncoding = global::System.Console.OutputEncoding;
        Encoding utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            global::System.Console.InputEncoding = utf8;
            global::System.Console.OutputEncoding = utf8;
            return new ConsoleEncodingScope(originalInputEncoding, originalOutputEncoding);
        }
        catch
        {
            global::System.Console.InputEncoding = originalInputEncoding;
            global::System.Console.OutputEncoding = originalOutputEncoding;
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        global::System.Console.InputEncoding = _originalInputEncoding;
        global::System.Console.OutputEncoding = _originalOutputEncoding;
    }
}
