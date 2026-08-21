using TerminalTranslator.Windows.ConPty;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class InteractiveConsoleTests
{
    [TestMethod]
    public void ConsoleEncodingScope_UsesUtf8AndRestoresBothCodePages()
    {
        System.Text.Encoding originalInput = System.Console.InputEncoding;
        System.Text.Encoding originalOutput = System.Console.OutputEncoding;

        using (TerminalTranslator.Windows.Console.ConsoleEncodingScope.EnterUtf8())
        {
            Assert.AreEqual(System.Text.Encoding.UTF8.CodePage, System.Console.InputEncoding.CodePage);
            Assert.AreEqual(System.Text.Encoding.UTF8.CodePage, System.Console.OutputEncoding.CodePage);
        }

        Assert.AreEqual(originalInput.CodePage, System.Console.InputEncoding.CodePage);
        Assert.AreEqual(originalOutput.CodePage, System.Console.OutputEncoding.CodePage);
    }

    [TestMethod]
    public async Task CtrlCUnicodePasteAndCursorReplyBytes_AreForwardedUnchanged()
    {
        byte[] input = [3, .. System.Text.Encoding.UTF8.GetBytes("粘贴内容\u001b[12;34R\r\n")];
        await using MemoryStream destination = new();
        await new TerminalTranslator.Windows.Console.ConsoleInputRelay(
            new MemoryStream(input), destination).CopyAsync(CancellationToken.None);
        CollectionAssert.AreEqual(input, destination.ToArray());
    }

    [TestMethod]
    public async Task ResizeMonitor_PropagatesChangedCharacterCellSize()
    {
        await using ConPtySession session = ConPtySession.StartPowerShell(Environment.CurrentDirectory);
        Queue<(short Columns, short Rows)> sizes = new([(80, 24), (100, 40), (100, 40)]);
        List<(short Columns, short Rows)> observed = [];
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));
        PseudoConsoleResizeMonitor monitor = new(
            session,
            () => sizes.Count > 0 ? sizes.Dequeue() : ((short)100, (short)40),
            TimeSpan.FromMilliseconds(5),
            (columns, rows) => observed.Add((columns, rows)));
        await Assert.ThrowsAsync<OperationCanceledException>(() => monitor.RunAsync(cancellation.Token));
        CollectionAssert.Contains(observed, ((short)100, (short)40));
    }

    [TestMethod]
    public void ConsoleModeScope_WhenNoAttachedConsole_IsANoOp()
    {
        using IDisposable? scope = TerminalTranslator.Windows.Console.ConsoleModeScope.TryEnterRawInput();
    }
}
