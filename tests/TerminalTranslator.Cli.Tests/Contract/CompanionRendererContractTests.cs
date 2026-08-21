using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class CompanionRendererContractTests
{
    [TestMethod]
    public void Render_AssociatesSourceTranslationAndPreservesIndentation()
    {
        using StringWriter output = new();
        CompanionRenderer renderer = new(output);
        renderer.Render(new TranslationEventMessage(
            "translation", 1, "session", 1, 7, "Failure:\n  reason", "\u5931\u8d25\uff1a\n  \u539f\u56e0", new LayoutHints(2, [0, 2])));

        Assert.AreEqual("[7] Failure:\n    \u5931\u8d25\uff1a\n      \u539f\u56e0\n\n", output.ToString().ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void Render_CompleteTranslationItemsHaveExactlyOneBlockSeparator()
    {
        using StringWriter output = new();
        CompanionRenderer renderer = new(output);

        renderer.Render(new TranslationEventMessage(
            "translation", 1, "session", 1, 4, "Source A", "Translation A\n", new LayoutHints(1, [0])));
        renderer.Render(new TranslationEventMessage(
            "translation", 1, "session", 1, 5, "Source B", "Translation B", new LayoutHints(1, [0])));

        Assert.AreEqual(
            "[4] Source A\n    Translation A\n\n" +
            "[5] Source B\n    Translation B\n\n",
            output.ToString().ReplaceLineEndings("\n"));
        Assert.IsFalse(output.ToString().ReplaceLineEndings("\n").Contains("\n\n\n", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Render_StatusIsContentFree()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tt-renderer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using StringWriter output = new();
            CompanionRenderer renderer = new(output);
            renderer.Render(new StatusEventMessage("provider-error", 1, "timeout"));

            Assert.AreEqual("status: provider-error (timeout)\n", output.ToString().ReplaceLineEndings("\n"));
            Assert.AreEqual(0, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
