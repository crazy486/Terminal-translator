using System.Text;

namespace TerminalTranslator.Core.Tests.Fixtures;

internal static class CaptureTranscriptCorpus
{
    public const string SessionId = "11111111-1111-1111-1111-111111111111";
    public const string NormalCommand = "git status";
    public const string NormalOutput = "On branch main\r\nnothing to commit\r\n";
    public const string EmptyCommand = "Set-Location ..";
    public const string MultilineCommand = "& {`n  Write-Output 'first'`n  Write-Output 'second'`n}";
    public const string MixedOutput = "OUT-1\r\nERR-1\r\nOUT-2\r\n";
    public const string CorruptBoundary = "TT-CAPTURE-CLOSE:wrong-session:99";

    public static byte[] OversizedUtf8(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        return Encoding.UTF8.GetBytes(new string('x', byteCount));
    }
}
