using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.Tests.Unit;

[TestClass]
public sealed class ContextualInvocationMarkerTests
{
    [TestMethod]
    public void InvocationArgumentsIdentifyContextualCommandsWithoutDependingOnPowerShellCommandText()
    {
        Assert.IsTrue(ContextualInvocationMarker.IsContextualInvocation(["last"]));
        Assert.IsTrue(ContextualInvocationMarker.IsContextualInvocation(["last", "unexpected"]));
        Assert.IsTrue(ContextualInvocationMarker.IsContextualInvocation(["ask", "last", "why"]));
        Assert.IsFalse(ContextualInvocationMarker.IsContextualInvocation(["ask", "why"]));
        Assert.IsFalse(ContextualInvocationMarker.IsContextualInvocation(["status"]));
    }

    [TestMethod]
    public async Task SessionBoundMarkerClassifiesVariableAndFullPathInvocationForms()
    {
        using TemporaryDirectory temporary = new();
        CaptureOwnerIdentity owner = new("S-1-5-21-contextual-marker", 9234, 638921111111111111);
        CaptureBootstrapResult bootstrap = await new CaptureSessionBootstrap(temporary.Path).InitializeAsync(
            CapturePreference.Enabled, owner, CaptureSessionBootstrap.CurrentIntegrationVersion, false);
        Dictionary<string, string> environment = new(bootstrap.Environment);

        Assert.IsTrue(await ContextualInvocationMarker.TryMarkCurrentAsync(
            temporary.Path,
            name => environment.GetValueOrDefault(name),
            () => (true, owner)));
        Assert.IsTrue(ContextualInvocationMarker.IsMarked(bootstrap.SessionDirectory!, 1));

        foreach (string commandText in new[] { "tt last", "& $tt last", $"& '{Path.Combine(temporary.Path, "tt.exe")}' last" })
        {
            CapturedCommand ordinaryTarget = Command(1, "Write-Output real", "real output", false);
            CapturedCommand contextual = Command(2, commandText, "[tt] Assistance provider request failed.",
                ContextualInvocationMarker.IsMarked(bootstrap.SessionDirectory!, 1));
            Assert.AreSame(ordinaryTarget, ContextualCommandSelection.SelectStrictTarget([ordinaryTarget, contextual]));
        }
    }

    [TestMethod]
    [DataRow("[translation] success")]
    [DataRow("[tt] Assistance provider request failed.")]
    [DataRow("[tt] Assistance provider request timed out.")]
    [DataRow("No translatable English content was found.")]
    [DataRow("Suspected sensitive content was not sent.")]
    [DataRow("Operation canceled.")]
    public void ContextualResultPathNeverChangesSelfExclusion(string contextualOutput)
    {
        CapturedCommand target = Command(1, "Write-Output real", "real output", false);
        CapturedCommand contextual = Command(2, "& $tt last", contextualOutput, true);
        Assert.AreSame(target, ContextualCommandSelection.SelectStrictTarget([target, contextual]));

        CapturedCommand nextReal = Command(3, "Write-Output next", "next output", false);
        Assert.AreSame(nextReal, ContextualCommandSelection.SelectStrictTarget([target, contextual, nextReal]));
    }

    private static CapturedCommand Command(long sequence, string text, string output, bool contextual)
    {
        CaptureSessionId session = new(Guid.Parse("99999999-9999-9999-9999-999999999999"), "nonce");
        CommandBoundary boundary = new(session, sequence, text, true, null, false);
        return new CapturedCommand(session, sequence, text, output, boundary,
            LocalCaptureCompleteness.Complete, System.Text.Encoding.UTF8.GetByteCount(output), contextual);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-contextual-marker-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
