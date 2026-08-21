using TerminalTranslator.Cli.Diagnostics;

namespace TerminalTranslator.Cli.Tests.Unit;

[TestClass]
public sealed class ContentFreeDiagnosticSinkTests
{
    [TestMethod]
    public void Record_WritesOnlyCodeCountAndDuration()
    {
        using StringWriter output = new();
        ContentFreeDiagnosticSink sink = new(output);

        sink.Record("privacy-skip", 3, TimeSpan.FromMilliseconds(120));

        Assert.AreEqual("diagnostic: privacy-skip count=3 duration-ms=120", output.ToString().Trim());
    }

    [TestMethod]
    public void Record_RejectsContentBearingCode()
    {
        using StringWriter output = new();
        ContentFreeDiagnosticSink sink = new(output);

        Assert.ThrowsExactly<ArgumentException>(() =>
            sink.Record("secret terminal source: abc123"));
        Assert.AreEqual(string.Empty, output.ToString());
    }
}
