using System.Diagnostics;
using System.Text;
using TerminalTranslator.Windows.Console;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class UserStory2AcceptanceTests
{
    [TestMethod]
    [DataRow("codex", "\u001b[?25lAnalyzing repository...\r\u001b[2KReady for input\r\n\u001b[?25h")]
    [DataRow("git", "On branch main\r\nChanges not staged for commit:\r\n  modified: file.cs\r\n")]
    [DataRow("npm", "\u001b]0;npm install\aadded 42 packages in 1s\r\n")]
    [DataRow("python", ">>> print('hello')\r\nhello\r\n>>> ")]
    public async Task ProgramPaneOutput_HasExactRawByteParity(string _, string terminalOutput)
    {
        byte[] expected = Encoding.UTF8.GetBytes(terminalOutput);
        await using MemoryStream destination = new();
        QueueOnlySink analysis = new();
        Stopwatch stopwatch = Stopwatch.StartNew();

        await new ConsoleOutputRelay(new MemoryStream(expected), destination, analysis)
            .CopyAsync(CancellationToken.None);

        stopwatch.Stop();
        CollectionAssert.AreEqual(expected, destination.ToArray(), "SC-003");
        CollectionAssert.AreEqual(expected, analysis.Copies.Single());
        Assert.IsLessThan(TimeSpan.FromMilliseconds(100), stopwatch.Elapsed);
    }

    [TestMethod]
    public async Task CursorReplyPasteAndImeInput_AreForwardedOnlyToChild()
    {
        byte[] input = Encoding.UTF8.GetBytes("\u001b[12;34R粘贴的命令 --flag=value\r\n");
        await using MemoryStream childInput = new();
        await new ConsoleInputRelay(new MemoryStream(input), childInput).CopyAsync(CancellationToken.None);
        CollectionAssert.AreEqual(input, childInput.ToArray());
    }

    private sealed class QueueOnlySink : INonBlockingAnalysisSink
    {
        public bool IsEnabled => true;

        public List<byte[]> Copies { get; } = [];

        public bool TryOffer(ReadOnlyMemory<byte> bytes)
        {
            Copies.Add(bytes.ToArray());
            return true;
        }
    }
}
