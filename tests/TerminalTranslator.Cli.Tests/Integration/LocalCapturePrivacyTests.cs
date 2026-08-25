using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class LocalCapturePrivacyTests
{
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void ProductionCaptureRoot_IsLocalAppDataAndNotUserContentOrRoaming()
    {
        string root = Path.GetFullPath(ProtectedCaptureStorage.DefaultRoot);
        string local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string documents = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        string desktop = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        string roaming = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

        StringAssert.StartsWith(root, local, StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(root.StartsWith(documents, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(root.StartsWith(desktop, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(root.StartsWith(roaming, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(root.Contains("OneDrive", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task RawSensitiveFixture_RemainsOnlyInsideInjectedProtectedCaptureRoot()
    {
        using TemporaryDirectory temporary = new();
        RetainedCaptureStore store = new(temporary.Path);
        byte[] sensitive = Encoding.UTF8.GetBytes("SYNTHETIC-RAW-SECRET-CAPTURE");
        await store.PublishAsync([new RetainedRecordWrite("record", 1, sensitive, Encoding.UTF8.GetBytes("metadata"))]);

        string[] files = Directory.GetFiles(temporary.Path, "*", SearchOption.AllDirectories);
        Assert.IsGreaterThan(0, files.Length);
        Assert.IsTrue(files.All(path => Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(temporary.Path) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(files.Any(path => File.ReadAllBytes(path).AsSpan().IndexOf(sensitive) >= 0));
    }

    [TestMethod]
    public void CaptureProductionHasNoNetworkTelemetryDiagnosticOrSyncDependency()
    {
        string repository = FindRepositoryRoot();
        string captureSource = Path.Combine(repository, "src", "TerminalTranslator.Windows", "Capture");
        string combined = string.Join('\n', Directory.GetFiles(captureSource, "*.cs")
            .Select(File.ReadAllText));

        foreach (string forbidden in new[]
                 {
                     "HttpClient", "System.Net.Http", "IDiagnosticSink", "Telemetry", "SupportBundle",
                     "OneDrive", "Environment.SpecialFolder.MyDocuments", "Environment.SpecialFolder.Desktop",
                     "Environment.SpecialFolder.ApplicationData",
                 })
        {
            Assert.IsFalse(combined.Contains(forbidden, StringComparison.Ordinal), forbidden);
        }
    }

    [TestMethod]
    public void CliExposesNoRawCaptureShowOrExportSurface()
    {
        string[] commandNames = CommandFactory.CreateRootCommand().Subcommands.Select(command => command.Name).ToArray();
        Assert.IsFalse(commandNames.Contains("capture", StringComparer.OrdinalIgnoreCase));
        Assert.IsFalse(commandNames.Contains("show-capture", StringComparer.OrdinalIgnoreCase));
        Assert.IsFalse(commandNames.Contains("export-capture", StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(CommandFactory.CreateRootCommand().Subcommands.Single(command => command.Name == "__capture").Hidden);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TerminalTranslator.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new AssertFailedException("Repository root was not found.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-local-privacy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
