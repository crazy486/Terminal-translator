using System.Diagnostics;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.Capture;
using TerminalTranslator.Windows.PowerShell;
using TerminalTranslator.Windows.Tests.TestDoubles;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class CaptureDisableAndUninstallTests
{
    [TestMethod]
    public async Task DisabledPreferenceAtBoundary_DeletesOnlyTheAuthorizedCurrentSession()
    {
        using TemporaryDirectory temporary = new();
        CaptureOwnerIdentity owner = new("S-1-5-21-1000", 7001, 638900000000000000);
        CaptureBootstrapResult current = await new CaptureSessionBootstrap(temporary.Path).InitializeAsync(
            CapturePreference.Enabled, owner, "2.0", false);
        CaptureBootstrapResult unrelated = await new CaptureSessionBootstrap(temporary.Path).InitializeAsync(
            CapturePreference.Enabled, owner with { ProcessId = 7002 }, "2.0", false);

        CaptureBoundaryProcessingResult result = await new CaptureBoundaryProcessor(temporary.Path).ProcessAsync(
            new CaptureBoundaryRequest(
                current.Proof!.SessionId,
                current.Proof.Nonce,
                owner,
                "2.0",
                current.StagingPath!,
                0,
                null,
                true,
                null),
            CapturePreference.Disabled);

        Assert.AreEqual(CaptureBoundaryStatus.Disabled, result.Status);
        Assert.IsFalse(Directory.Exists(current.SessionDirectory));
        Assert.IsTrue(Directory.Exists(unrelated.SessionDirectory));
    }

    [TestMethod]
    public async Task LoadedSession_DisablesAtNextPromptAndDoesNotStartFutureIntervals()
    {
        using TemporaryDirectory temporary = new();
        await using InstalledPowerShellLoader installedLoader = await InstalledPowerShellLoader.CreateAsync();
        string escapedLoader = installedLoader.LoaderPath.Replace("'", "''", StringComparison.Ordinal);
        string staging = Path.Combine(temporary.Path, "active-transcript.txt").Replace("'", "''", StringComparison.Ordinal);
        string command = $@"
$global:TtBoundaryCalls = 0
function global:tt {{
  param([Parameter(ValueFromRemainingArguments=$true)]$Remaining)
  if ($Remaining -contains 'initialize') {{
    'session=11111111111111111111111111111111'
    'nonce=' + ('A' * 64)
    'staging={staging}'
  }}
  elseif ($Remaining -contains 'boundary') {{
    $global:TtBoundaryCalls++
    'disabled=1'
  }}
}}
function global:prompt {{ 'CUSTOM> ' }}
. '{escapedLoader}'
Write-Output 'ordinary user output'
$null = prompt
$null = prompt
'BOUNDARY_CALLS=' + $global:TtBoundaryCalls
'CAPTURE_DISABLED=' + $script:TtCaptureDisabled
'CAPTURE_ENV=' + [string]$env:TT_CAPTURE_SESSION_ID
";
        ProcessResult result = await RunPowerShellAsync(command);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        StringAssert.Contains(result.Output, "ordinary user output");
        StringAssert.Contains(result.Output, "BOUNDARY_CALLS=1");
        StringAssert.Contains(result.Output, "CAPTURE_DISABLED=True");
        StringAssert.Contains(result.Output, "CAPTURE_ENV=");
    }

    [TestMethod]
    public async Task Uninstall_RemovesOnlyManagedBlockAndLoaderAndPreservesUnrelatedState()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        string integration = Path.Combine(temporary.Path, "integration");
        string unrelated = Path.Combine(integration, "user-owned.txt");
        const string userProfile = "function prompt { 'CUSTOM> ' }\r\n# unrelated profile tail\r\n";
        await File.WriteAllTextAsync(profile, userProfile);
        PowerShellIntegrationInstaller installer = new(integration);
        await installer.InstallAsync(profile);
        await File.WriteAllTextAsync(unrelated, "preserve");

        await installer.RemoveAsync(profile);

        Assert.AreEqual(userProfile, await File.ReadAllTextAsync(profile));
        Assert.IsFalse(File.Exists(installer.LoaderPath));
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(unrelated));
        Assert.AreEqual(0, Directory.GetFiles(temporary.Path, "*.tmp", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task FailedUninstall_RestoresLoaderAndLeavesProfileAuthoritative()
    {
        using TemporaryDirectory temporary = new();
        string profile = Path.Combine(temporary.Path, "profile.ps1");
        string integration = Path.Combine(temporary.Path, "integration");
        await File.WriteAllTextAsync(profile, "user\r\n");
        PowerShellIntegrationInstaller installed = new(integration);
        await installed.InstallAsync(profile);
        byte[] loader = await File.ReadAllBytesAsync(installed.LoaderPath);
        string profileA = await File.ReadAllTextAsync(profile);
        PowerShellProfileInstaller conflicting = new(phase =>
        {
            if (phase == ProfileEditPhase.BeforeCommit)
            {
                File.AppendAllText(profile, "external\r\n");
            }
        });

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            new PowerShellIntegrationInstaller(integration, conflicting).RemoveAsync(profile));

        CollectionAssert.AreEqual(loader, await File.ReadAllBytesAsync(installed.LoaderPath));
        Assert.AreEqual(profileA + "external\r\n", await File.ReadAllTextAsync(profile));
        Assert.AreEqual(0, Directory.GetFiles(temporary.Path, "*.tmp", SearchOption.AllDirectories).Length);
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string command)
    {
        ProcessStartInfo start = new("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-disable-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
