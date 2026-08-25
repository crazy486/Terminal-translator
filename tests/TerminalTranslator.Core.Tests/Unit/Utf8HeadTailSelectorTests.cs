using System.Text;
using TerminalTranslator.Core.Assistance;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class Utf8HeadTailSelectorTests
{
    [TestMethod]
    public void Select_UsesEqualSharesAndOddByteGoesToTail()
    {
        Utf8HeadTailSelection selection = Utf8HeadTailSelector.Select("abcdefghijklmno", 10, preferNewline: false);
        Assert.AreEqual(5, selection.HeadBytes);
        Assert.AreEqual(5, selection.TailBytes);
        Assert.AreEqual("abcde", selection.Head);
        Assert.AreEqual("klmno", selection.Tail);

        selection = Utf8HeadTailSelector.Select("abcdefghijklmnop", 11, preferNewline: false);
        Assert.AreEqual(5, selection.HeadBytes);
        Assert.AreEqual(6, selection.TailBytes);
    }

    [TestMethod]
    public void Select_PrefersNewlinesAndNeverSplitsMultibyteRunes()
    {
        Utf8HeadTailSelection lines = Utf8HeadTailSelector.Select("head one\nhead two\nmiddle\ntail one\ntail two", 25, preferNewline: true);
        Assert.IsTrue(lines.Head.EndsWith('\n'));
        Assert.IsFalse(lines.Tail.StartsWith("iddle", StringComparison.Ordinal));

        Utf8HeadTailSelection unicode = Utf8HeadTailSelector.Select("甲😀乙ABC丙😀丁", 13, preferNewline: false);
        Assert.IsFalse(unicode.Head.Contains('\uFFFD'));
        Assert.IsFalse(unicode.Tail.Contains('\uFFFD'));
        Assert.IsLessThanOrEqualTo(13, Encoding.UTF8.GetByteCount(unicode.Head + unicode.Tail));
    }

    [TestMethod]
    public void Select_NoNewlineStillUsesDeterministicRuneSafeCuts()
    {
        Utf8HeadTailSelection first = Utf8HeadTailSelector.Select("😀abcdefghij界", 12, preferNewline: true);
        Utf8HeadTailSelection second = Utf8HeadTailSelector.Select("😀abcdefghij界", 12, preferNewline: true);
        Assert.AreEqual(first, second);
    }
}
