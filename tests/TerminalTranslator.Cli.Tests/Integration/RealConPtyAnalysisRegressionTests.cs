using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Windows.ConPty;
using TerminalTranslator.Windows.Console;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class RealConPtyAnalysisRegressionTests
{
    private static readonly TimeSpan JourneyTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task PowerShell51SingleCommand_ProviderReceivesOnlyRealStdout()
    {
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit("Write-Output \"The server is running normally.\"\r\n"),
        ]);

        CollectionAssert.AreEqual(
            new[] { "The server is running normally." },
            result.Sources,
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51DisabledEpoch_OrdinaryCommandsOnlyTranslateReenabledOutput()
    {
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Disable(),
            InputStep.Submit("Write-Output \"Disabled one.\"\r\n", delayMilliseconds: 250),
            InputStep.Submit("Write-Output \"Disabled two.\"\r\n", delayMilliseconds: 250),
            InputStep.Enable(),
            InputStep.Submit("Write-Output \"Translation resumed.\"\r\n", delayMilliseconds: 500),
        ]);

        CollectionAssert.AreEqual(
            new[] { "Translation resumed." },
            result.Sources,
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51DisabledEpoch_MultilineContinuationLeavesNoOwnership()
    {
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Disable(),
            InputStep.Submit("$items = @(\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("\"First item\"\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("\"Second item\"\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("\"Third item\"\r\n", waitForContinuationPrompt: true),
            InputStep.Submit(")\r\n", delayMilliseconds: 250),
            InputStep.Enable(),
            InputStep.Submit("Write-Output \"Translation resumed.\"\r\n", delayMilliseconds: 500),
        ]);

        CollectionAssert.AreEqual(
            new[] { "Translation resumed." },
            result.Sources,
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51DisabledEpoch_MoreThanHistoryLimitCannotPoisonReenable()
    {
        string disabledInput = string.Concat(
            Enumerable.Range(1, 70)
                .Select(index => $"$disabled{index:00} = 'history line {index:00}'\r\n"));
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Disable(),
            InputStep.Submit(disabledInput, delayMilliseconds: 500),
            InputStep.Enable(),
            InputStep.Submit("Write-Output \"Translation resumed.\"\r\n", delayMilliseconds: 500),
        ]);

        CollectionAssert.AreEqual(
            new[] { "Translation resumed." },
            result.Sources,
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51DisabledEpoch_ActiveControlCannotCrossEpochBoundary()
    {
        const string installControlCommand =
            "function global:tt { param([string]$operation) if ($operation -eq 'status') { " +
            "'arbitrary control output' } }\r\n";
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit(installControlCommand, delayMilliseconds: 250),
            InputStep.Submit("tt status\r\n"),
            InputStep.Disable(),
            InputStep.Submit(
                "Write-Output \"Disabled while control ownership was active.\"\r\n",
                delayMilliseconds: 750),
            InputStep.Enable(),
            InputStep.Submit("Write-Output \"Translation resumed.\"\r\n", delayMilliseconds: 500),
        ]);

        CollectionAssert.AreEqual(
            new[] { "Translation resumed." },
            result.Sources,
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51AutomaticVisualWrap_ReconstructsOneLogicalSourceLine()
    {
        const string source =
            "The configuration file is missing and the application cannot start.";
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit($"Write-Output \"{source}\"\r\n", delayMilliseconds: 500),
        ], viewportColumns: 60);

        CollectionAssert.AreEqual(new[] { source }, result.Sources, result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51ErrorRecordVisualWrap_PreservesLogicalErrorMessage()
    {
        const string source =
            "The configuration file is missing and the application cannot start.";
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit($"Write-Error '{source}'\r\n", delayMilliseconds: 500),
        ], viewportColumns: 60);

        Assert.IsTrue(
            result.Sources.Any(candidate => candidate.Contains(source, StringComparison.Ordinal)),
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51RapidWrappedCommandBeforeError_DoesNotLeakSubmittedTail()
    {
        const string first =
            "The build failed because a required configuration file is missing.";
        const string error =
            "The configuration file is missing and the application cannot start.";
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit($"Write-Output '{first}'\r\n"),
            InputStep.Submit($"Write-Error '{error}'\r\n", delayMilliseconds: 500),
        ], viewportColumns: 60);

        Assert.IsTrue(
            result.Sources.Any(candidate => candidate.Contains(error, StringComparison.Ordinal)),
            result.Diagnostic);
        Assert.IsFalse(
            result.Sources.Any(candidate => candidate.StartsWith(
                "ication cannot start.'",
                StringComparison.Ordinal)),
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51RapidMixedWrappedOutput_PreservesFollowingRealOutput()
    {
        const string error =
            "The configuration file is missing and the application cannot start.";
        const string warning =
            "The build may fail because the configuration is incomplete.";
        const string output =
            "ERROR: The application failed to start because the required configuration file could not be found.";
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit(
                "Write-Output 'The build failed because a required configuration file is missing.'\r\n",
                delayMilliseconds: 400),
            InputStep.Submit($"Write-Error '{error}'\r\n", delayMilliseconds: 400),
            InputStep.Submit($"Write-Warning '{warning}'\r\n", delayMilliseconds: 400),
            InputStep.Submit($"Write-Output '{output}'\r\n", delayMilliseconds: 500),
        ], viewportColumns: 60);

        CollectionAssert.Contains(result.Sources, output, result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51HereString_ProviderReceivesExactlyOneRealStdoutBlock()
    {
        const string source =
            "The server is running normally.\n" +
            "Three background tasks are currently active.\n" +
            "No critical errors were detected.";
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit("@\"\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("The server is running normally.\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("Three background tasks are currently active.\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("No critical errors were detected.\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("\"@\r\n", delayMilliseconds: 500),
        ]);

        CollectionAssert.AreEqual(new[] { source }, result.Sources, result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51HereString_KeyByKey_ProviderReceivesExactlyOneRealStdoutBlock()
    {
        const string source =
            "The server is running normally.\n" +
            "Three background tasks are currently active.\n" +
            "No critical errors were detected.";
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Type("@\"\r\n", waitForContinuationPrompt: true),
            InputStep.Type("The server is running normally.\r\n", waitForContinuationPrompt: true),
            InputStep.Type("Three background tasks are currently active.\r\n", waitForContinuationPrompt: true),
            InputStep.Type("No critical errors were detected.\r\n", waitForContinuationPrompt: true),
            InputStep.Type("\"@\r\n", delayMilliseconds: 500),
        ], viewportColumns: 60);

        CollectionAssert.AreEqual(new[] { source }, result.Sources, result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51TerminalTranslatorControls_ProviderReceivesNothing()
    {
        const string installControlCommand =
            "function global:tt { param([string]$operation) switch ($operation) { " +
            "'status' { 'session: active'; 'translation: enabled'; 'provider: api.deepseek.com'; " +
            "'model: deepseek-v4-flash'; 'queue: high=0 normal=0'; 'privacy-skipped: 0'; " +
            "'overload-dropped: 0' } 'off' { 'Translation disabled.' } " +
            "'on' { 'Translation enabled.' } } }\r\n";

        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit(installControlCommand, delayMilliseconds: 250),
            InputStep.Submit("tt status\r\n", delayMilliseconds: 250),
            InputStep.Submit("tt off\r\n", delayMilliseconds: 250),
            InputStep.Submit("tt on\r\n", delayMilliseconds: 500),
        ]);

        Assert.AreEqual(0, result.Sources.Length, result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51RapidControlThenRealOutput_ProviderReceivesOnlyRealOutput()
    {
        const string installControlCommand =
            "function global:tt { param([string]$operation) switch ($operation) { " +
            "'status' { 'arbitrary control output one'; 'arbitrary control output two' } " +
            "'off' { 'arbitrary disabled control output' } " +
            "'on' { 'arbitrary enabled control output' } } }\r\n";
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit(installControlCommand, delayMilliseconds: 250),
            InputStep.Submit("tt status\r\n"),
            InputStep.Submit("Write-Output \"The real application output starts here.\"\r\n"),
        ], viewportColumns: 60);

        CollectionAssert.AreEqual(
            new[] { "The real application output starts here." },
            result.Sources,
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51RapidControlCommands_ProviderReceivesNothing()
    {
        const string installControlCommand =
            "function global:tt { param([string]$operation) switch ($operation) { " +
            "'status' { 'arbitrary control output one'; 'arbitrary control output two' } " +
            "'off' { 'arbitrary disabled control output' } " +
            "'on' { 'arbitrary enabled control output' } } }\r\n";
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit(installControlCommand, delayMilliseconds: 250),
            InputStep.Submit("tt status\r\n"),
            InputStep.Submit("tt off\r\n"),
            InputStep.Submit("tt on\r\n"),
        ], viewportColumns: 60);

        Assert.AreEqual(0, result.Sources.Length, result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51UnrecognizedPromptAfterControl_FailsOpenForNextRealOutput()
    {
        const string installControlCommand =
            "function global:tt { param([string]$operation) if ($operation -eq 'status') { " +
            "'arbitrary control output one'; 'arbitrary control output two' } }\r\n";
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit(
                "Set-PSReadLineOption -HistorySaveStyle SaveNothing\r\n",
                delayMilliseconds: 250),
            InputStep.Submit(installControlCommand, delayMilliseconds: 250),
            InputStep.Submit("function global:prompt { 'λ ' }\r\n", delayMilliseconds: 250),
            InputStep.Submit("tt status\r\n", delayMilliseconds: 250),
            InputStep.Submit("Write-Output \"Real output.\"\r\n", delayMilliseconds: 500),
        ]);

        CollectionAssert.AreEqual(
            new[] { "Real output." },
            result.Sources,
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51GreaterThanPendingControlCollision_DoesNotCorruptOwnership()
    {
        const string installControlCommand =
            "function global:tt { param([string]$operation) if ($operation -eq 'status') { " +
            "'arbitrary control output one'; 'arbitrary control output two' } }\r\n";
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit(
                "Set-PSReadLineOption -HistorySaveStyle SaveNothing\r\n",
                delayMilliseconds: 250),
            InputStep.Submit(installControlCommand, delayMilliseconds: 250),
            InputStep.Submit(
                "Write-Output '> tt status'; Write-Output 'Collision survived.'\r\n" +
                "tt status\r\n",
                delayMilliseconds: 500),
        ]);

        CollectionAssert.AreEqual(
            new[] { "Collision survived." },
            result.Sources,
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51GreaterThanMarkers_PreserveRawBytesAndFollowingTranslation()
    {
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit(
                "Set-PSReadLineOption -HistorySaveStyle SaveNothing\r\n",
                delayMilliseconds: 250),
            InputStep.Submit("Write-Output '>'\r\n", delayMilliseconds: 250),
            InputStep.Submit("Write-Output 'value > threshold'\r\n", delayMilliseconds: 250),
            InputStep.Submit("Write-Output 'PS > something'\r\n", delayMilliseconds: 250),
            InputStep.Submit(
                "Write-Output 'The marker regression completed successfully.'\r\n",
                delayMilliseconds: 500),
        ]);

        CollectionAssert.AreEqual(result.RawConPtyBytes, result.TerminalBytes, result.Diagnostic);
        string terminal = Encoding.UTF8.GetString(result.TerminalBytes);
        StringAssert.Contains(terminal, ">", StringComparison.Ordinal, result.Diagnostic);
        StringAssert.Contains(terminal, "value > threshold", StringComparison.Ordinal, result.Diagnostic);
        StringAssert.Contains(terminal, "PS > something", StringComparison.Ordinal, result.Diagnostic);
        CollectionAssert.AreEqual(
            new[]
            {
                "value > threshold",
                "PS > something",
                "The marker regression completed successfully.",
            },
            result.Sources,
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51ActualTtExecutable_StatusAndOffOutputNeverReachProvider()
    {
        string sessionId = Guid.NewGuid().ToString("N");
        string nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray()) +
            Convert.ToHexString(Guid.NewGuid().ToByteArray());
        string? previousSession = Environment.GetEnvironmentVariable("TT_SESSION_ID");
        string? previousNonce = Environment.GetEnvironmentVariable("TT_SESSION_NONCE");
        using CancellationTokenSource serverCancellation = new(JourneyTimeout);
        await using ControlPipeServer server = new(
            SessionPipeNames.Control(sessionId, nonce),
            sessionId,
            nonce,
            new ActualControlHandler());
        Task serverTask = server.RunAsync(serverCancellation.Token);
        try
        {
            Environment.SetEnvironmentVariable("TT_SESSION_ID", sessionId);
            Environment.SetEnvironmentVariable("TT_SESSION_NONCE", nonce);
            string executable = Path.Combine(AppContext.BaseDirectory, "tt.exe")
                .Replace("'", "''", StringComparison.Ordinal);

            JourneyResult result = await RunPowerShell51Async(
            [
                InputStep.Submit(
                    $"Set-Alias -Name tt -Value '{executable}'\r\n",
                    delayMilliseconds: 250),
                InputStep.Submit("tt status\r\n"),
                InputStep.Submit("tt off\r\n", delayMilliseconds: 500),
            ]);

            Assert.AreEqual(0, result.Sources.Length, result.Diagnostic);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TT_SESSION_ID", previousSession);
            Environment.SetEnvironmentVariable("TT_SESSION_NONCE", previousNonce);
            serverCancellation.Cancel();
            await serverTask;
        }
    }

    [TestMethod]
    public async Task PowerShell51ActualTtExecutable_StatusThenRealOutput_OnlyOutputReachesProvider()
    {
        string sessionId = Guid.NewGuid().ToString("N");
        string nonce = Convert.ToHexString(Guid.NewGuid().ToByteArray()) +
            Convert.ToHexString(Guid.NewGuid().ToByteArray());
        string? previousSession = Environment.GetEnvironmentVariable("TT_SESSION_ID");
        string? previousNonce = Environment.GetEnvironmentVariable("TT_SESSION_NONCE");
        using CancellationTokenSource serverCancellation = new(JourneyTimeout);
        await using ControlPipeServer server = new(
            SessionPipeNames.Control(sessionId, nonce),
            sessionId,
            nonce,
            new ActualControlHandler());
        Task serverTask = server.RunAsync(serverCancellation.Token);
        try
        {
            Environment.SetEnvironmentVariable("TT_SESSION_ID", sessionId);
            Environment.SetEnvironmentVariable("TT_SESSION_NONCE", nonce);
            string executable = Path.Combine(AppContext.BaseDirectory, "tt.exe")
                .Replace("'", "''", StringComparison.Ordinal);

            JourneyResult result = await RunPowerShell51Async(
            [
                InputStep.Submit(
                    $"Set-Alias -Name tt -Value '{executable}'\r\n",
                    delayMilliseconds: 250),
                InputStep.Submit("tt status\r\n"),
                InputStep.Submit("Write-Output \"The real application output starts here.\"\r\n"),
            ]);

            CollectionAssert.AreEqual(
                new[] { "The real application output starts here." },
                result.Sources,
                result.Diagnostic);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TT_SESSION_ID", previousSession);
            Environment.SetEnvironmentVariable("TT_SESSION_NONCE", previousNonce);
            serverCancellation.Cancel();
            await serverTask;
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(10)]
    [DataRow(50)]
    [DataRow(180)]
    public async Task PowerShell51RapidFiveCommands_EachPromptIsHardBoundary(int intervalMilliseconds)
    {
        string[] expected =
        [
            "Alpha completed.",
            "Bravo completed.",
            "Charlie completed.",
            "Delta completed.",
            "Echo completed.",
        ];
        string[] commands =
        [
            "Write-Output \"Alpha completed.\"\r\n",
            "Write-Output \"Bravo completed.\"\r\n",
            "Write-Output \"Charlie completed.\"\r\n",
            "Write-Output \"Delta completed.\"\r\n",
            "Write-Output \"Echo completed.\"\r\n",
        ];

        JourneyResult result = await RunPowerShell51Async(
            commands.Select(command => InputStep.Submit(command, intervalMilliseconds)).ToArray());

        CollectionAssert.AreEqual(expected, result.Sources, result.Diagnostic);
    }

    [TestMethod]
    [DataRow(10)]
    [DataRow(180)]
    public async Task PowerShell51RapidThreeCommands_TimingNeverReplacesSemanticBoundaries(
        int intervalMilliseconds)
    {
        string[] expected =
        [
            "The first operation completed successfully.",
            "The second operation completed successfully.",
            "The third operation completed successfully.",
        ];
        JourneyResult result = await RunPowerShell51Async(
            expected.Select(source => InputStep.Submit(
                $"Write-Output \"{source}\"\r\n",
                intervalMilliseconds)).ToArray());

        CollectionAssert.AreEqual(expected, result.Sources, result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51NarrowPaneCommands_WrappedPrimaryPromptNeverBecomesSource()
    {
        string[] expected =
        [
            "Alpha completed successfully.",
            "Bravo completed successfully.",
            "Charlie completed successfully.",
        ];
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit("Write-Output 'Alpha completed successfully.'\r\n", delayMilliseconds: 400),
            InputStep.Submit("Write-Output 'Bravo completed successfully.'\r\n", delayMilliseconds: 400),
            InputStep.Submit("Write-Output 'Charlie completed successfully.'\r\n", delayMilliseconds: 500),
        ], viewportColumns: 60);

        CollectionAssert.AreEqual(expected, result.Sources, result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51NarrowPaneLongCommands_DoNotCorruptFollowingPromptOwnership()
    {
        string[] expected =
        [
            "The first long operation completed successfully without any critical errors.",
            "The second long operation completed successfully without any critical errors.",
            "The third long operation completed successfully without any critical errors.",
        ];
        JourneyResult result = await RunPowerShell51Async(
            expected.Select(source => InputStep.Submit(
                $"Write-Output '{source}'\r\n",
                delayMilliseconds: 400)).ToArray(),
            viewportColumns: 60);

        CollectionAssert.AreEqual(expected, result.Sources, result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51EnableAfterInitialPrompt_StartsFreshObservableEpoch()
    {
        string[] expected =
        [
            "The first long operation completed successfully without any critical errors.",
            "The second long operation completed successfully without any critical errors.",
            "The third long operation completed successfully without any critical errors.",
        ];
        JourneyResult result = await RunPowerShell51Async(
            expected.Select(source => InputStep.Submit(
                $"Write-Output '{source}'\r\n",
                delayMilliseconds: 400)).ToArray(),
            viewportColumns: 60,
            enableAfterInitialPrompt: true);

        CollectionAssert.AreEqual(expected, result.Sources, result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51BurstWithSlowProvider_RemainsResponsiveAndNeverCorruptsSources()
    {
        string[] expected = Enumerable.Range(1, 15)
            .Select(index => $"Burst operation {index} completed successfully.")
            .ToArray();
        JourneyResult result = await RunPowerShell51Async(
            expected.Select(source => InputStep.Submit(
                $"Write-Output '{source}'\r\n"))
                .ToArray(),
            holdProviderUntilOutput: expected[^1]);

        Assert.IsGreaterThanOrEqualTo(1, result.Sources.Length, result.Diagnostic);
        Assert.IsTrue(result.Sources.All(expected.Contains), result.Diagnostic);
        Assert.AreEqual(result.Sources.Length, result.Sources.Distinct().Count(), result.Diagnostic);
        Assert.IsFalse(
            result.Trace.Contains("logical-unit", "ProgramOutput: PS "),
            result.Diagnostic);
        StringAssert.Contains(
            Encoding.UTF8.GetString(result.TerminalBytes),
            expected[^1],
            StringComparison.Ordinal,
            result.Diagnostic);
        Assert.IsTrue(
            result.Trace.OccurredBefore(
                "raw-pane-write",
                expected[^1],
                "provider-complete",
                string.Empty),
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51ContinuationExpression_ProviderReceivesOnlyExecutedStdout()
    {
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Submit("Write-Output (\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("\"Continuation output works correctly.\"\r\n", waitForContinuationPrompt: true),
            InputStep.Submit(")\r\n", delayMilliseconds: 500),
        ]);

        CollectionAssert.AreEqual(
            new[] { "Continuation output works correctly." },
            result.Sources,
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51ContinuationExpression_KeyByKey_ProviderReceivesOnlyExecutedStdout()
    {
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Type("Write-Output (\r\n", waitForContinuationPrompt: true),
            InputStep.Type("\"Continuation output works correctly.\"\r\n", waitForContinuationPrompt: true),
            InputStep.Type(")\r\n", delayMilliseconds: 500),
        ]);

        CollectionAssert.AreEqual(
            new[] { "Continuation output works correctly." },
            result.Sources,
            result.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51ArrayAssignment_DoesNotTranslateInput_ThenTranslatesArrayOutput()
    {
        JourneyResult assignment = await RunPowerShell51Async(
        [
            InputStep.Submit("$items = @(\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("\"First item\"\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("\"Second item\"\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("\"Third item\"\r\n", waitForContinuationPrompt: true),
            InputStep.Submit(")\r\n", delayMilliseconds: 500),
        ]);

        Assert.AreEqual(0, assignment.Sources.Length, assignment.Diagnostic);

        JourneyResult output = await RunPowerShell51Async(
        [
            InputStep.Submit("$items = @(\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("\"First item\"\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("\"Second item\"\r\n", waitForContinuationPrompt: true),
            InputStep.Submit("\"Third item\"\r\n", waitForContinuationPrompt: true),
            InputStep.Submit(")\r\n", delayMilliseconds: 250),
            InputStep.Submit("$items\r\n", delayMilliseconds: 500),
        ]);

        CollectionAssert.AreEqual(
            new[] { "First item\nSecond item\nThird item" },
            output.Sources,
            output.Diagnostic);
    }

    [TestMethod]
    public async Task PowerShell51ArrayAssignment_KeyByKey_DoesNotTranslateInput()
    {
        JourneyResult result = await RunPowerShell51Async(
        [
            InputStep.Type("$items = @(\r\n", waitForContinuationPrompt: true),
            InputStep.Type("\"First item\"\r\n", waitForContinuationPrompt: true),
            InputStep.Type("\"Second item\"\r\n", waitForContinuationPrompt: true),
            InputStep.Type("\"Third item\"\r\n", waitForContinuationPrompt: true),
            InputStep.Type(")\r\n", delayMilliseconds: 500),
        ]);

        Assert.AreEqual(0, result.Sources.Length, result.Diagnostic);
    }

    private static async Task<JourneyResult> RunPowerShell51Async(
        IReadOnlyList<InputStep> inputs,
        int viewportColumns = 120,
        bool enableAfterInitialPrompt = false,
        TimeSpan? providerDelay = null,
        string? holdProviderUntilOutput = null)
    {
        Guid sessionId = Guid.NewGuid();
        TraceRecorder trace = new();
        TaskCompletionSource? providerRelease = holdProviderUntilOutput is null
            ? null
            : new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProviderSpy provider = new(
            trace,
            providerDelay ?? TimeSpan.Zero,
            providerRelease?.Task);
        NullEventSink eventSink = new(trace);
        RuntimeObserver runtimeObserver = new(trace);
        SubmittedCommandTracker submittedCommands = new();
        TracingSubmittedCommandTracker tracedCommands = new(submittedCommands, trace);
        using CancellationTokenSource cancellation = new(JourneyTimeout);
        await using MemoryStream terminalOutput = new();

        await using ProductionTranslationPipeline pipeline =
            ProductionRuntimeComposition.CreateTranslationPipeline(
                sessionId,
                provider,
                eventSink,
                TimeSpan.FromSeconds(2),
                viewportColumns: viewportColumns,
                viewportRows: 20,
                submittedCommandTracker: tracedCommands,
                runtimeObserver: runtimeObserver);
        if (!enableAfterInitialPrompt)
        {
            pipeline.Enable();
        }

        await using ConPtySession conPty = ConPtySession.Start(
            "powershell.exe",
            "-NoLogo -NoExit -Command \"Set-PSReadLineOption -HistorySaveStyle SaveNothing\"",
            Environment.CurrentDirectory,
            new Coord((short)viewportColumns, 20));
        await using TraceReadStream tracedChildOutput = new(conPty.Output, trace, "raw-conpty-read");
        await using TraceWriteStream tracedProgramPane = new(terminalOutput, trace, "raw-pane-write");
        TracingAnalysisSink tracedAnalysis = new(pipeline, trace);
        Task outputTask = new ConsoleOutputRelay(tracedChildOutput, tracedProgramPane, tracedAnalysis)
            .CopyAsync(cancellation.Token);

        Assert.IsTrue(
            await WaitUntilAsync(
                () => Encoding.UTF8.GetString(terminalOutput.ToArray())
                    .Contains("PS ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)),
            trace.Format(terminalOutput.ToArray(), provider.Sources));

        if (enableAfterInitialPrompt)
        {
            Assert.IsFalse(
                trace.Contains("runtime", "stage=VtUnitEmitted"),
                "The initial disabled prompt must bypass analysis (FR-010).");
            pipeline.Enable();
        }
        else
        {
            Assert.IsTrue(
                await WaitUntilAsync(
                    () => trace.Contains("logical-unit", "PowerShellHardBoundary"),
                    TimeSpan.FromSeconds(2)),
                trace.Format(terminalOutput.ToArray(), provider.Sources));
        }

        for (int inputIndex = 0; inputIndex < inputs.Count; inputIndex++)
        {
            InputStep input = inputs[inputIndex];
            if (input.Kind == InputStepKind.Disable)
            {
                Assert.IsTrue(pipeline.Disable());
                continue;
            }

            if (input.Kind == InputStepKind.Enable)
            {
                pipeline.Enable();
                continue;
            }

            int continuationCount = CountOccurrences(
                Encoding.UTF8.GetString(terminalOutput.ToArray()), ">>");
            await RelayInputStepAsync(
                conPty.Input,
                bytes => _ = pipeline.TryObserveSubmittedInput(bytes),
                trace,
                terminalOutput,
                input,
                cancellation.Token);
            if (input.WaitForContinuationPrompt)
            {
                Assert.IsTrue(
                    await WaitUntilAsync(
                        () => CountOccurrences(
                            Encoding.UTF8.GetString(terminalOutput.ToArray()), ">>") > continuationCount,
                        TimeSpan.FromSeconds(3)),
                    trace.Format(terminalOutput.ToArray(), provider.Sources));
            }

            if (input.DelayMilliseconds > 0)
            {
                await Task.Delay(input.DelayMilliseconds, cancellation.Token);
            }

            if (providerRelease is not null && inputIndex == 0)
            {
                Assert.IsTrue(
                    await WaitUntilAsync(
                        () => trace.Contains("provider-start", string.Empty),
                        TimeSpan.FromSeconds(3)),
                    trace.Format(terminalOutput.ToArray(), provider.Sources));
            }
        }

        if (providerRelease is not null)
        {
            Assert.IsTrue(
                await WaitUntilAsync(
                    () => Encoding.UTF8.GetString(terminalOutput.ToArray())
                        .Contains(holdProviderUntilOutput!, StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5)),
                trace.Format(terminalOutput.ToArray(), provider.Sources));
            Assert.IsFalse(
                trace.Contains("provider-complete", string.Empty),
                "The provider gate must remain blocked while the program pane drains.");
            providerRelease.TrySetResult();
            Assert.IsTrue(
                await WaitUntilAsync(
                    () => trace.Contains("provider-complete", string.Empty),
                    TimeSpan.FromSeconds(3)),
                trace.Format(terminalOutput.ToArray(), provider.Sources));
        }

        await RelayInputAsync(
            conPty.Input,
            bytes => _ = pipeline.TryObserveSubmittedInput(bytes),
            trace,
            "exit 0\r\n",
            cancellation.Token);
        int exitCode = await conPty.WaitForExitAsync(cancellation.Token);
        await conPty.CompleteInputAsync();
        conPty.ClosePseudoConsole();
        await outputTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(
            await WaitUntilAsync(
                () => trace.Contains("logical-unit", "exit 0"),
                TimeSpan.FromSeconds(2)),
            trace.Format(terminalOutput.ToArray(), provider.Sources));

        Assert.AreEqual(
            0,
            exitCode,
            trace.Format(terminalOutput.ToArray(), provider.Sources));
        return new JourneyResult(
            provider.Sources.ToArray(),
            tracedChildOutput.CapturedBytes,
            terminalOutput.ToArray(),
            trace);
    }

    private static async Task RelayInputAsync(
        Stream pseudoConsoleInput,
        Action<ReadOnlyMemory<byte>> inputObserver,
        TraceRecorder trace,
        string text,
        CancellationToken cancellationToken)
    {
        string vtInput = text.Replace("\r\n", "\r", StringComparison.Ordinal);

        void Observe(ReadOnlyMemory<byte> bytes)
        {
            trace.Record("stdin-observed", Encoding.UTF8.GetString(bytes.Span));
            inputObserver(bytes);
            trace.Record("ownership-established", "submitted input observed before ConPTY write");
        }

        await using TraceWriteStream tracedChildInput = new(
            pseudoConsoleInput,
            trace,
            "stdin-conpty-write",
            leaveOpen: true);
        await new ConsoleInputRelay(
            new MemoryStream(Encoding.UTF8.GetBytes(vtInput)),
            tracedChildInput,
            Observe).CopyAsync(cancellationToken);
    }

    private static async Task RelayInputStepAsync(
        Stream pseudoConsoleInput,
        Action<ReadOnlyMemory<byte>> inputObserver,
        TraceRecorder trace,
        MemoryStream terminalOutput,
        InputStep input,
        CancellationToken cancellationToken)
    {
        if (!input.KeyByKey)
        {
            await RelayInputAsync(
                pseudoConsoleInput,
                inputObserver,
                trace,
                input.Text,
                cancellationToken);
            return;
        }

        string vtInput = input.Text.Replace("\r\n", "\r", StringComparison.Ordinal);
        foreach (char value in vtInput)
        {
            long outputLength = terminalOutput.Length;
            await RelayInputAsync(
                pseudoConsoleInput,
                inputObserver,
                trace,
                value.ToString(),
                cancellationToken);
            if (value != '\r')
            {
                Assert.IsTrue(
                    await WaitUntilAsync(
                        () => terminalOutput.Length > outputLength,
                        TimeSpan.FromSeconds(2)),
                    trace.Format(terminalOutput.ToArray(), []));
            }
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(20);
        }

        return true;
    }

    private static string Escape(byte[] bytes) => Encoding.UTF8.GetString(bytes)
        .Replace("\u001b", "<ESC>", StringComparison.Ordinal)
        .Replace("\r", "<CR>", StringComparison.Ordinal)
        .Replace("\n", "<LF>", StringComparison.Ordinal);

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed class ProviderSpy(
        TraceRecorder trace,
        TimeSpan delay,
        Task? completionGate = null) : ITranslationProvider
    {
        public ConcurrentQueue<string> Sources { get; } = new();

        public async Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            trace.Record("provider-start", $"sequence={request.SegmentSequence} source={request.SourceText}");
            Sources.Enqueue(request.SourceText);
            if (completionGate is not null)
            {
                await completionGate.WaitAsync(cancellationToken);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            trace.Record("provider-complete", $"sequence={request.SegmentSequence}");
            return new TranslationResult("translation", "real-conpty-spy");
        }
    }

    private sealed class ActualControlHandler : IControlPipeRequestHandler
    {
        public string CurrentState => "enabled";

        public Task<ControlResultMessage> EnableAsync(
            string providerFingerprint,
            bool consent,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ControlResultMessage(
                "control-result", SessionProtocol.Version, "enable", true, "enabled", 1));

        public Task<ControlResultMessage> DisableAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ControlResultMessage(
                "control-result", SessionProtocol.Version, "disable", true, "disabled", 2));

        public Task<StatusResultMessage> StatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StatusResultMessage(
                "status-result",
                SessionProtocol.Version,
                "enabled",
                "api.deepseek.com",
                "deepseek-v4-flash",
                0,
                0,
                0,
                0));
    }

    private sealed class NullEventSink(TraceRecorder trace) : ITranslationEventSink
    {
        public ValueTask PublishAsync(TranslationItem item, CancellationToken cancellationToken)
        {
            trace.Record("translation-item-created", $"sequence={item.SegmentSequence}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed record JourneyResult(
        string[] Sources,
        byte[] RawConPtyBytes,
        byte[] TerminalBytes,
        TraceRecorder Trace)
    {
        public string Diagnostic => Trace.Format(TerminalBytes, Sources);
    }

    private sealed record InputStep(
        string Text,
        int DelayMilliseconds,
        bool WaitForContinuationPrompt,
        bool KeyByKey,
        InputStepKind Kind)
    {
        public static InputStep Submit(
            string text,
            int delayMilliseconds = 0,
            bool waitForContinuationPrompt = false) =>
            new(
                text,
                delayMilliseconds,
                waitForContinuationPrompt,
                KeyByKey: false,
                InputStepKind.Input);

        public static InputStep Type(
            string text,
            int delayMilliseconds = 0,
            bool waitForContinuationPrompt = false) =>
            new(
                text,
                delayMilliseconds,
                waitForContinuationPrompt,
                KeyByKey: true,
                InputStepKind.Input);

        public static InputStep Disable() =>
            new(string.Empty, 0, false, false, InputStepKind.Disable);

        public static InputStep Enable() =>
            new(string.Empty, 0, false, false, InputStepKind.Enable);
    }

    private enum InputStepKind { Input, Disable, Enable }

    private sealed class TracingAnalysisSink(
        INonBlockingAnalysisSink inner,
        TraceRecorder trace) : INonBlockingAnalysisSink
    {
        public bool IsEnabled => inner.IsEnabled;

        public bool IsObserving => inner.IsObserving;

        public bool TryOffer(ReadOnlyMemory<byte> bytes)
        {
            trace.Record("analysis-offer", Encoding.UTF8.GetString(bytes.Span));
            return inner.TryOffer(bytes);
        }

        public bool TryObserve(ReadOnlyMemory<byte> bytes)
        {
            trace.Record("analysis-offer", Encoding.UTF8.GetString(bytes.Span));
            return inner.TryObserve(bytes);
        }
    }

    private sealed class TracingSubmittedCommandTracker(
        SubmittedCommandTracker inner,
        TraceRecorder trace) : ISubmittedCommandTracker
    {
        public void BeginObservationEpoch(long epoch)
        {
            inner.BeginObservationEpoch(epoch);
            trace.Record("ownership-epoch", $"generation={epoch}");
        }

        public void BeginObservationEpoch(long epoch, bool claimDormantEnableControl)
        {
            inner.BeginObservationEpoch(epoch, claimDormantEnableControl);
            trace.Record(
                "ownership-epoch",
                $"generation={epoch} claimDormantEnableControl={claimDormantEnableControl}");
        }

        public bool Observe(long epoch, ReadOnlyMemory<byte> bytes) => inner.Observe(epoch, bytes);

        public bool ObserveDormantInput(long epoch, ReadOnlyMemory<byte> bytes) =>
            inner.ObserveDormantInput(epoch, bytes);

        public AnalysisLineDisposition ClassifyAnalysisLine(long epoch, string line)
        {
            AnalysisLineDisposition disposition = inner.ClassifyAnalysisLine(epoch, line);
            trace.Record("logical-unit", $"{disposition}: {line}");
            return disposition;
        }
    }

    private sealed class RuntimeObserver(TraceRecorder trace) : ITranslationRuntimeObserver
    {
        public void Record(TranslationRuntimeEvent runtimeEvent)
        {
            trace.Record(
                "runtime",
                $"stage={runtimeEvent.Stage} sequence={runtimeEvent.Sequence} " +
                $"generation={runtimeEvent.Generation} " +
                $"atMs={Milliseconds(runtimeEvent.Timestamp)} " +
                $"createdAtMs={Milliseconds(runtimeEvent.CreatedAt)} " +
                $"enqueuedAtMs={Milliseconds(runtimeEvent.EnqueuedAt)} " +
                $"dequeuedAtMs={Milliseconds(runtimeEvent.DequeuedAt)} " +
                $"providerStartedAtMs={Milliseconds(runtimeEvent.ProviderStartedAt)} " +
                $"providerCompletedAtMs={Milliseconds(runtimeEvent.ProviderCompletedAt)} " +
                $"cancelRequestedAtMs={Milliseconds(runtimeEvent.CancelRequestedAt)} " +
                $"cancelObservedAtMs={Milliseconds(runtimeEvent.CancelObservedAt)} " +
                $"queueAgeMs={Milliseconds(runtimeEvent.QueueAge)} " +
                $"providerElapsedMs={Milliseconds(runtimeEvent.ProviderElapsed)} " +
                $"totalAgeMs={Milliseconds(runtimeEvent.TotalAge)} " +
                $"cancelReason={runtimeEvent.CancelReason} " +
                $"normalizedProviderError={runtimeEvent.NormalizedProviderError}");
        }

        private static string Milliseconds(TimeSpan? duration) =>
            duration?.TotalMilliseconds.ToString("0.000") ?? "-";
    }

    private sealed class TraceRecorder
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly ConcurrentQueue<TraceEvent> _events = new();

        public void Record(string name, string detail) =>
            _events.Enqueue(new TraceEvent(_stopwatch.Elapsed, name, Sanitize(detail)));

        public bool Contains(string name, string detail) =>
            _events.Any(item =>
                item.Name == name &&
                item.Detail.Contains(detail, StringComparison.Ordinal));

        public bool OccurredBefore(
            string firstName,
            string firstDetail,
            string secondName,
            string secondDetail)
        {
            TraceEvent? first = _events.FirstOrDefault(item =>
                item.Name == firstName &&
                item.Detail.Contains(firstDetail, StringComparison.Ordinal));
            TraceEvent? second = _events.FirstOrDefault(item =>
                item.Name == secondName &&
                item.Detail.Contains(secondDetail, StringComparison.Ordinal));
            return first is not null && second is not null && first.Elapsed < second.Elapsed;
        }

        public string Format(byte[] terminalBytes, IEnumerable<string> sources)
        {
            StringBuilder builder = new();
            builder.Append("provider=[")
                .Append(string.Join(" || ", sources.Select(Sanitize)))
                .Append("] terminal=")
                .Append(Sanitize(Escape(terminalBytes)))
                .AppendLine()
                .AppendLine("monotonic-trace:");
            TraceEvent[] events = _events.ToArray();
            IEnumerable<TraceEvent> displayed = events.Length <= 512
                ? events
                : events.Take(256).Concat(
                    [new TraceEvent(TimeSpan.Zero, "trace-omitted", $"count={events.Length - 512}")])
                    .Concat(events.Skip(events.Length - 256));
            foreach (TraceEvent traceEvent in displayed)
            {
                builder.Append(traceEvent.Elapsed.TotalMilliseconds.ToString("000000.000"))
                    .Append("ms ")
                    .Append(traceEvent.Name)
                    .Append(' ')
                    .AppendLine(traceEvent.Detail);
            }

            return builder.ToString();
        }

        private static string Sanitize(string value)
        {
            string sanitized = value.Replace(
                Environment.CurrentDirectory,
                "<cwd>",
                StringComparison.OrdinalIgnoreCase);
            sanitized = sanitized
                .Replace("\u001b", "<ESC>", StringComparison.Ordinal)
                .Replace("\r", "<CR>", StringComparison.Ordinal)
                .Replace("\n", "<LF>", StringComparison.Ordinal);
            return sanitized.Length <= 600 ? sanitized : sanitized[..600] + "<truncated>";
        }

        private sealed record TraceEvent(TimeSpan Elapsed, string Name, string Detail);
    }

    private sealed class TraceReadStream(
        Stream inner,
        TraceRecorder trace,
        string eventName) : Stream
    {
        private readonly MemoryStream _captured = new();

        public byte[] CapturedBytes => _captured.ToArray();

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int count = await inner.ReadAsync(buffer, cancellationToken);
            if (count > 0)
            {
                _captured.Write(buffer.Span[..count]);
                trace.Record(eventName, Encoding.UTF8.GetString(buffer.Span[..count]));
            }

            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TraceWriteStream(
        Stream inner,
        TraceRecorder trace,
        string eventName,
        bool leaveOpen = false) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            trace.Record(eventName, Encoding.UTF8.GetString(buffer.Span));
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                inner.Dispose();
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (!leaveOpen)
            {
                await inner.DisposeAsync();
            }
        }
    }
}
