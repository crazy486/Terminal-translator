using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Core.Parsing;
using TerminalTranslator.Windows.Capture;
using TerminalTranslator.Windows.Console;
using TerminalTranslator.Windows.ConPty;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class InstalledProductNativeCaptureAcceptanceTests
{
    // ConPTY expresses the default prompt's trailing space as a cursor-right control sequence.
    private static readonly string DefaultPrompt = $"PS {Environment.CurrentDirectory}>";

    [TestMethod]
    public async Task FreshInstalledProfile_ProviderFreeMixedNativeStressRetainsCurrentSequenceOutputAndExit()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The installed-product acceptance is Windows-only.");
        if (!string.Equals(
                Environment.GetEnvironmentVariable("TT_RUN_INSTALLED_CAPTURE_ACCEPTANCE"),
                "1",
                StringComparison.Ordinal))
            Assert.Inconclusive("Set TT_RUN_INSTALLED_CAPTURE_ACCEPTANCE=1 after installing the acceptance artifact.");

        string installedLoader = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerminalTranslator",
            "PowerShell",
            "TerminalTranslator.Profile.ps1");
        Assert.IsTrue(File.Exists(installedLoader), $"Installed loader is missing: {installedLoader}");
        AssertInstalledLoaderMatchesRepository(installedLoader);

        string diagnosticPath = Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "diagnostics",
            $"t133-capture-stress-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        string? priorDiagnostics = Environment.GetEnvironmentVariable("TT_CAPTURE_COMPLETION_DIAGNOSTICS");
        string? priorDiagnosticPath = Environment.GetEnvironmentVariable("TT_CAPTURE_COMPLETION_DIAGNOSTIC_PATH");
        Environment.SetEnvironmentVariable("TT_CAPTURE_COMPLETION_DIAGNOSTICS", "1");
        Environment.SetEnvironmentVariable("TT_CAPTURE_COMPLETION_DIAGNOSTIC_PATH", diagnosticPath);

        ConPtySession? session = null;
        MemoryStream? console = null;
        Task? drain = null;
        try
        {
            session = ConPtySession.Start(
                "powershell.exe",
                "-NoLogo -NoExit -ExecutionPolicy Bypass",
                Environment.CurrentDirectory,
                new Coord(240, 500));
            console = new MemoryStream();
            drain = new ConsoleOutputRelay(session.Output, console).CopyAsync(CancellationToken.None);
            await WaitForTextAsync(console, DefaultPrompt, 1, TimeSpan.FromSeconds(20));

            string sessionDirectory = await WaitForSessionDirectoryAsync(session.ProcessId, TimeSpan.FromSeconds(20));
            CaptureOwnerManifest manifest = await WaitForHealthyManifestAsync(sessionDirectory, TimeSpan.FromSeconds(20));
            long expectedSequence = manifest.NextSequence;
            expectedSequence = await AssertRedirectedNativeCaseAsync(
                session, console, sessionDirectory, diagnosticPath, expectedSequence);
            int stressCount = 500;
            string? configuredStressCount = Environment.GetEnvironmentVariable("TT_INSTALLED_CAPTURE_STRESS_COUNT");
            if (!string.IsNullOrWhiteSpace(configuredStressCount) &&
                (!int.TryParse(configuredStressCount, out stressCount) || stressCount is < 1 or > 500))
                Assert.Fail("TT_INSTALLED_CAPTURE_STRESS_COUNT must be an integer from 1 through 500.");
            List<NativeCase> cases = BuildNativeCases(stressCount);
            for (int index = 0; index < cases.Count; index++)
            {
                NativeCase item = cases[index];
                int visibleCount = Count(ConsoleText(console), item.VisibleMarker) + 1;
                int promptCount = Count(ConsoleText(console), DefaultPrompt) + 1;
                await SendAsync(session, item.Command);
                await WaitForTextAsync(console, item.VisibleMarker, visibleCount, TimeSpan.FromSeconds(15));
                await WaitForTextAsync(console, DefaultPrompt, promptCount, TimeSpan.FromSeconds(15));
                CapturedCommand record = await WaitForRecordAsync(
                    sessionDirectory,
                    expectedSequence,
                    item.Command,
                    TimeSpan.FromSeconds(15));
                AppendSelectionDiagnostic(diagnosticPath, record);
                Assert.AreEqual(expectedSequence, record.Sequence, $"Diagnostic: {diagnosticPath}");
                StringAssert.Contains(record.Output, item.RetainedMarker);
                Assert.AreEqual(item.ExitCode, record.Boundary.NativeExitCode, $"{item.Command}; diagnostic: {diagnosticPath}");
                expectedSequence++;

                if (index % 17 == 0)
                    await Task.Delay(index % 34 == 0 ? 35 : 8);
            }

            expectedSequence = await AssertNoOutputTransitionAsync(
                session, console, sessionDirectory, diagnosticPath,
                expectedSequence, "# T133 diagnostic comment transition");
            expectedSequence = await AssertNativeCaseAsync(
                session, console, sessionDirectory, diagnosticPath, expectedSequence,
                new NativeCase("T133_AFTER_COMMENT", "cmd /d /c \"echo T133_AFTER_COMMENT & exit 5\"", 5));
            expectedSequence = await AssertNoOutputTransitionAsync(
                session, console, sessionDirectory, diagnosticPath,
                expectedSequence, "Set-Location .");
            _ = await AssertNativeCaseAsync(
                session, console, sessionDirectory, diagnosticPath, expectedSequence,
                new NativeCase("T133_AFTER_SET_LOCATION", "cmd /d /c \"echo T133_AFTER_SET_LOCATION & exit 5\"", 5));
        }
        catch (Exception exception)
        {
            Assert.Fail($"Installed capture stress failed. Structural diagnostic: {diagnosticPath}{Environment.NewLine}{exception}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TT_CAPTURE_COMPLETION_DIAGNOSTICS", priorDiagnostics);
            Environment.SetEnvironmentVariable("TT_CAPTURE_COMPLETION_DIAGNOSTIC_PATH", priorDiagnosticPath);
            if (session is not null)
            {
                await SendAsync(session, "exit");
                Assert.AreEqual(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)));
                await session.CompleteInputAsync();
                session.ClosePseudoConsole();
                if (drain is not null)
                    await drain.WaitAsync(TimeSpan.FromSeconds(5));
                await session.DisposeAsync();
            }
            if (console is not null)
                await console.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task FreshInstalledProfile_T135DirectEnglishOutputsReachProviderSelectionAndNoiseDoesNot()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The installed-product acceptance is Windows-only.");
        if (!string.Equals(
                Environment.GetEnvironmentVariable("TT_RUN_INSTALLED_CAPTURE_ACCEPTANCE"),
                "1",
                StringComparison.Ordinal))
            Assert.Inconclusive("Set TT_RUN_INSTALLED_CAPTURE_ACCEPTANCE=1 after installing the acceptance artifact.");

        string installedLoader = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerminalTranslator",
            "PowerShell",
            "TerminalTranslator.Profile.ps1");
        Assert.IsTrue(File.Exists(installedLoader));
        AssertInstalledLoaderMatchesRepository(installedLoader);

        await using ConPtySession session = ConPtySession.Start(
            "powershell.exe",
            "-NoLogo -NoExit -ExecutionPolicy Bypass",
            Environment.CurrentDirectory,
            new Coord(240, 80));
        await using MemoryStream console = new();
        Task drain = new ConsoleOutputRelay(session.Output, console).CopyAsync(CancellationToken.None);
        await WaitForTextAsync(console, DefaultPrompt, 1, TimeSpan.FromSeconds(20));
        string sessionDirectory = await WaitForSessionDirectoryAsync(session.ProcessId, TimeSpan.FromSeconds(20));
        long sequence = (await WaitForHealthyManifestAsync(sessionDirectory, TimeSpan.FromSeconds(20))).NextSequence;

        foreach ((string command, string expectedText, int? exitCode) in new[]
        {
            (
                "cmd /d /c \"echo The package installation completed successfully. & exit 5\"",
                "The package installation completed successfully.",
                (int?)5),
            (
                "cmd /d /c \"echo The application failed because the configuration file is missing. 1>&2 & exit 7\"",
                "The application failed because the configuration file is missing.",
                (int?)7),
            (
                "Write-Output \"The package installation completed successfully.\"",
                "The package installation completed successfully.",
                null),
        })
        {
            int promptCount = Count(ConsoleText(console), DefaultPrompt) + 1;
            await SendAsync(session, command);
            await WaitForTextAsync(console, DefaultPrompt, promptCount, TimeSpan.FromSeconds(20));
            CapturedCommand record = await WaitForRecordAsync(
                sessionDirectory, sequence++, command, TimeSpan.FromSeconds(20));
            StringAssert.Contains(record.Output, expectedText);
            Assert.AreEqual(exitCode, record.Boundary.NativeExitCode);
            Assert.IsTrue(WholeOutputEligibility.HasTranslatableEnglish(record.Output));

            RecordingAssistanceProvider provider = new();
            LastAssistanceOutcome outcome = await new LastAssistanceCoordinator(
                () => PreviousCommandResult.Success(ToSnapshot(record)),
                provider,
                new ApprovedAuthorizer()).ExecuteAsync(CancellationToken.None);
            Assert.AreEqual(AssistanceFailureKind.None, outcome.Failure);
            StringAssert.Contains(provider.Requests.Single().Request.SelectedOutput, expectedText);
        }

        const string noiseCommand = "Write-Output 12345";
        int noisePromptCount = Count(ConsoleText(console), DefaultPrompt) + 1;
        await SendAsync(session, noiseCommand);
        await WaitForTextAsync(console, DefaultPrompt, noisePromptCount, TimeSpan.FromSeconds(20));
        CapturedCommand noise = await WaitForRecordAsync(
            sessionDirectory, sequence, noiseCommand, TimeSpan.FromSeconds(20));
        Assert.IsFalse(WholeOutputEligibility.HasTranslatableEnglish(noise.Output));
        RecordingAssistanceProvider noiseProvider = new();
        LastAssistanceOutcome noiseOutcome = await new LastAssistanceCoordinator(
            () => PreviousCommandResult.Success(ToSnapshot(noise)),
            noiseProvider,
            new ApprovedAuthorizer()).ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(AssistanceFailureKind.NoTranslatableEnglish, noiseOutcome.Failure);
        Assert.IsEmpty(noiseProvider.Requests);

        await SendAsync(session, "exit");
        Assert.AreEqual(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task FreshInstalledProfile_CodexDoctorAndTuiReceiveConsoleHandles()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The installed Codex TTY acceptance is Windows-only.");
        if (!string.Equals(
                Environment.GetEnvironmentVariable("TT_RUN_CODEX_TTY_ACCEPTANCE"),
                "1",
                StringComparison.Ordinal))
            Assert.Inconclusive("Set TT_RUN_CODEX_TTY_ACCEPTANCE=1 after installing the acceptance artifact and Codex.");

        string installedLoader = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerminalTranslator", "PowerShell", "TerminalTranslator.Profile.ps1");
        Assert.IsTrue(File.Exists(installedLoader), $"Installed loader is missing: {installedLoader}");
        AssertInstalledLoaderMatchesRepository(installedLoader);

        await using ConPtySession session = ConPtySession.Start(
            "powershell.exe",
            "-NoLogo -NoExit -ExecutionPolicy Bypass",
            Environment.CurrentDirectory,
            new Coord(180, 60));
        await using MemoryStream console = new();
        Task drain = new ConsoleOutputRelay(session.Output, console).CopyAsync(CancellationToken.None);

        await WaitForTextAsync(console, DefaultPrompt, 1, TimeSpan.FromSeconds(20));
        await SendAsync(session, "codex doctor --json");
        await WaitForTextAsync(console, "\"stdin is terminal\": \"true\"", 1, TimeSpan.FromSeconds(30));
        await WaitForTextAsync(console, "\"stdout is terminal\": \"true\"", 1, TimeSpan.FromSeconds(30));
        await WaitForTextAsync(console, "\"stderr is terminal\": \"true\"", 1, TimeSpan.FromSeconds(30));
        await WaitForTextAsync(console, DefaultPrompt, 2, TimeSpan.FromSeconds(30));

        await SendAsync(session, "codex");
        await WaitForTextAsync(console, "OpenAI Codex", 1, TimeSpan.FromSeconds(30));
        await Task.Delay(1000);
        Assert.AreEqual(2, Count(ConsoleText(console), DefaultPrompt), Tail(console));
        Assert.IsFalse(ConsoleText(console).Contains("stdout is not a terminal", StringComparison.OrdinalIgnoreCase), Tail(console));

        for (int attempt = 0; attempt < 3 && Count(ConsoleText(console), DefaultPrompt) < 3; attempt++)
        {
            await session.Input.WriteAsync(new byte[] { 0x03 });
            await session.Input.FlushAsync();
            await Task.Delay(300);
        }
        await WaitForTextAsync(console, DefaultPrompt, 3, TimeSpan.FromSeconds(15));
        await SendAsync(session, "exit");
        Assert.AreEqual(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        await session.CompleteInputAsync();
        session.ClosePseudoConsole();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static List<NativeCase> BuildNativeCases(int count) => Enumerable.Range(1, count)
        .Select(index =>
        {
            string marker = $"T133_STRESS_{index:D4}";
            return (index % 5) switch
            {
                0 => new NativeCase(marker, $"cmd /d /c \"echo {marker} & exit 5\"", 5),
                1 => new NativeCase(marker, $"cmd /d /c \"echo {marker} & exit 0\"", 0),
                2 => new NativeCase(marker, $"cmd /d /c \"echo {marker} 1>&2 & exit 7\"", 7),
                3 => new NativeCase(marker, $"cmd /d /c \"echo {marker}_{new string('L', 4096)} & exit 5\"", 5),
                _ => new NativeCase(marker, $"cmd /q /d /c \"<nul set /p ={marker} & echo. & exit 5\"", 5),
            };
        })
        .ToList();

    private static PreviousCommandSnapshot ToSnapshot(CapturedCommand record) => new(
        record.Session,
        record.Sequence,
        record.CommandText,
        record.Output,
        record.Boundary.NativeExitCode,
        record.Boundary.WasInterrupted,
        record.LocalCompleteness,
        record.OriginalOutputBytes)
    {
        PowerShellSucceeded = record.Boundary.PowerShellSucceeded,
    };

    private static void AssertInstalledLoaderMatchesRepository(string installedLoaderPath)
    {
        string repository = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TerminalTranslator.Profile.ps1"));
        string installed = File.ReadAllText(installedLoaderPath);
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            installed,
            @"(?m)^\$script:TtExecutablePath = '((?:''|[^'])*)'\s*$");
        Assert.IsTrue(match.Success);
        string target = match.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal);
        string expected = repository.Replace(
            "__TT_EXECUTABLE_PATH__",
            target.Replace("'", "''", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.AreEqual(expected, installed);
        foreach (string required in new[]
        {
            "TranscribePipelineComplete", "FlushContentToDisk", "OutputToLog", "OutputBeingLogged",
            "AlwaysCaptureApplicationIO", "Complete-TtTranscriptProducer", "producer-pass-two", "transcript-drained=",
            "native-file-redirection=", "FileRedirectionAst",
            "TT_CAPTURE_COMPLETION_DIAGNOSTICS", "native-completion-observed", "stop-transcript-start",
        })
            StringAssert.Contains(installed, required);
    }

    private static async Task<string> WaitForSessionDirectoryAsync(int processId, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (Directory.Exists(ProtectedCaptureStorage.DefaultRoot))
            {
                foreach (string directory in Directory.EnumerateDirectories(ProtectedCaptureStorage.DefaultRoot))
                {
                    try
                    {
                        CaptureOwnerManifest manifest = await CaptureSessionBootstrap.ReadOwnerManifestAsync(directory);
                        if (manifest.Proof.Owner.ProcessId == processId)
                            return directory;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                    {
                    }
                }
            }
            await Task.Delay(20);
        }
        Assert.Fail($"Timed out locating installed capture session for PowerShell PID {processId}.");
        return null!;
    }

    private static async Task<CaptureOwnerManifest> WaitForHealthyManifestAsync(string sessionDirectory, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                CaptureOwnerManifest manifest = await CaptureSessionBootstrap.ReadOwnerManifestAsync(sessionDirectory);
                if (manifest.HealthState == CaptureHealthState.Healthy)
                    return manifest;
            }
            catch (IOException)
            {
            }
            await Task.Delay(20);
        }
        Assert.Fail("Timed out waiting for the installed capture session to become healthy.");
        return null!;
    }

    private static async Task<CapturedCommand> WaitForRecordAsync(
        string sessionDirectory,
        long expectedSequence,
        string expectedCommand,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        RetainedCaptureStore store = new(sessionDirectory);
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                RetainedGeneration? generation = await store.LoadCommittedAsync();
                RetainedRecordDescriptor? descriptor = generation?.Records.SingleOrDefault(value => value.Sequence == expectedSequence);
                if (descriptor is not null)
                {
                    CapturedCommand record = RetainedCommandRecordCodec.Deserialize(
                        await store.ReadContentAsync(descriptor),
                        await store.ReadMetadataAsync(descriptor));
                    Assert.AreEqual(expectedCommand, record.CommandText);
                    return record;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
            }
            await Task.Delay(15);
        }
        Assert.Fail($"Timed out waiting for retained sequence {expectedSequence}: {expectedCommand}");
        return null!;
    }

    private static async Task<long> AssertNativeCaseAsync(
        ConPtySession session,
        MemoryStream console,
        string sessionDirectory,
        string diagnosticPath,
        long expectedSequence,
        NativeCase item)
    {
        int visibleCount = Count(ConsoleText(console), item.VisibleMarker) + 1;
        int promptCount = Count(ConsoleText(console), DefaultPrompt) + 1;
        await SendAsync(session, item.Command);
        await WaitForTextAsync(console, item.VisibleMarker, visibleCount, TimeSpan.FromSeconds(15));
        await WaitForTextAsync(console, DefaultPrompt, promptCount, TimeSpan.FromSeconds(15));
        CapturedCommand record = await WaitForRecordAsync(sessionDirectory, expectedSequence, item.Command, TimeSpan.FromSeconds(15));
        AppendSelectionDiagnostic(diagnosticPath, record);
        StringAssert.Contains(record.Output, item.RetainedMarker);
        Assert.AreEqual(item.ExitCode, record.Boundary.NativeExitCode);
        return expectedSequence + 1;
    }

    private static async Task<long> AssertRedirectedNativeCaseAsync(
        ConPtySession session,
        MemoryStream console,
        string sessionDirectory,
        string diagnosticPath,
        long expectedSequence)
    {
        string redirectPath = Path.Combine(Path.GetTempPath(), $"tt-t133-redirect-{Guid.NewGuid():N}.txt");
        try
        {
            string command = $"cmd /d /c \"echo T133_REDIRECTED & exit 5\" > '{redirectPath}'";
            int promptCount = Count(ConsoleText(console), DefaultPrompt) + 1;
            await SendAsync(session, command);
            await WaitForTextAsync(console, DefaultPrompt, promptCount, TimeSpan.FromSeconds(15));
            CapturedCommand record = await WaitForRecordAsync(
                sessionDirectory, expectedSequence, command, TimeSpan.FromSeconds(15));
            AppendSelectionDiagnostic(diagnosticPath, record);
            Assert.AreEqual(string.Empty, record.Output);
            Assert.AreEqual(5, record.Boundary.NativeExitCode);
            StringAssert.Contains(await File.ReadAllTextAsync(redirectPath), "T133_REDIRECTED");
            return expectedSequence + 1;
        }
        finally
        {
            if (File.Exists(redirectPath))
                File.Delete(redirectPath);
        }
    }

    private static async Task<long> AssertNoOutputTransitionAsync(
        ConPtySession session,
        MemoryStream console,
        string sessionDirectory,
        string diagnosticPath,
        long expectedSequence,
        string command)
    {
        int promptCount = Count(ConsoleText(console), DefaultPrompt) + 1;
        await SendAsync(session, command);
        await WaitForTextAsync(console, DefaultPrompt, promptCount, TimeSpan.FromSeconds(15));
        CapturedCommand record = await WaitForRecordAsync(sessionDirectory, expectedSequence, command, TimeSpan.FromSeconds(15));
        AppendSelectionDiagnostic(diagnosticPath, record);
        Assert.AreEqual(string.Empty, record.Output);
        Assert.IsNull(record.Boundary.NativeExitCode, "A non-native no-output transition must not inherit stale LASTEXITCODE.");
        return expectedSequence + 1;
    }

    private static void AppendSelectionDiagnostic(string path, CapturedCommand record)
    {
        string line = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTime.UtcNow,
            processId = Environment.ProcessId,
            sessionId = record.Session.Value,
            sequence = record.Sequence,
            stage = "selected-record",
            structuralState = new
            {
                selectedRecordSequence = record.Sequence,
                retainedOutputLength = Encoding.UTF8.GetByteCount(record.Output),
                nativeExitCode = record.Boundary.NativeExitCode,
            },
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, line + Environment.NewLine);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TerminalTranslator.sln")))
            current = current.Parent;
        return current?.FullName ?? Environment.CurrentDirectory;
    }

    private static async Task SendAsync(ConPtySession session, string command)
    {
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes(command + "\r"));
        await session.Input.FlushAsync();
    }

    private static async Task WaitForTextAsync(
        MemoryStream console,
        string value,
        int expected,
        TimeSpan? timeout = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(8);
        while (Count(ConsoleText(console), value) < expected)
        {
            if (stopwatch.Elapsed >= limit)
                Assert.Fail($"Timed out waiting for '{value}' occurrence {expected}. Tail: {Tail(console)}");
            await Task.Delay(20);
        }
    }

    private static string ConsoleText(MemoryStream console) => Encoding.UTF8.GetString(console.ToArray());

    private static string Tail(MemoryStream console)
    {
        string text = ConsoleText(console);
        return text.Length <= 4000 ? text : text[^4000..];
    }

    private static int Count(string value, string needle) => value.Split(needle, StringSplitOptions.None).Length - 1;

    private sealed record NativeCase(
        string VisibleMarker,
        string Command,
        int ExitCode,
        string? OutputMarker = null)
    {
        public string RetainedMarker => OutputMarker ?? VisibleMarker;
    }

    private sealed class RecordingAssistanceProvider : IAssistanceProvider
    {
        public List<AuthorizedAssistanceRequest> Requests { get; } = [];

        public Task<AssistanceResult> CompleteAsync(
            AuthorizedAssistanceRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new AssistanceResult("translation", "recommendation"));
        }
    }

    private sealed class ApprovedAuthorizer : IAssistanceRequestAuthorizer
    {
        public Task<AssistanceAuthorizationDecision> AuthorizeAsync(
            AssistanceRequest request,
            CancellationToken cancellationToken)
        {
            string scope = AssistanceConsentScopes.For(request.Kind);
            MatchingConsentAssertion consent = MatchingConsentAssertion.TryCreate("test", "test", scope)!;
            AuthorizedAssistanceRequest authorized = new TerminalTranslator.Core.Assistance.AssistancePrivacyGate(
                new TerminalTranslator.Core.Privacy.SecretDetector()).Authorize(request, consent)!;
            return Task.FromResult(AssistanceAuthorizationDecision.Authorized(authorized));
        }
    }
}
