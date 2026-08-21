namespace TerminalTranslator.Core.Sessions;

public interface IDiagnosticSink
{
    void Record(string code, int count = 1, TimeSpan? duration = null);
}

public sealed class NullDiagnosticSink : IDiagnosticSink
{
    public void Record(string code, int count = 1, TimeSpan? duration = null)
    {
    }
}
