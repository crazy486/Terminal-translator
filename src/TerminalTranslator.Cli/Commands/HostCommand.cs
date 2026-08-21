using System.CommandLine;
using System.ComponentModel;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Parsing;
using TerminalTranslator.Core.Privacy;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Translation;
using TerminalTranslator.Windows.ConPty;
using TerminalTranslator.Windows.Console;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Commands;

public sealed class BasicHostPipeline(
    Guid sessionId,
    VtTextExtractor extractor,
    EnglishCandidateClassifier classifier,
    TranslationCoordinator coordinator)
{
    private ulong _sequence;

    public bool Enabled { get; private set; }

    public void Enable() => Enabled = true;

    public void Disable() => Enabled = false;

    public async Task<int> ProcessAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return 0;
        }

        int translated = 0;
        foreach (ExtractedText extracted in extractor.Feed(bytes.Span))
        {
            CandidateClassification classification = classifier.Classify(
                extracted.Text,
                extracted.Layout,
                extracted.Boundary);
            if (!classification.IsEligible)
            {
                continue;
            }

            OutputSegment segment = new(
                sessionId,
                1,
                ++_sequence,
                extracted.Text,
                extracted.Layout,
                extracted.Boundary,
                classification.Priority,
                classification.DeduplicationKey,
                TimeSpan.Zero);
            if (await coordinator.TranslateAsync(segment, cancellationToken).ConfigureAwait(false) is not null)
            {
                translated++;
            }
        }

        return translated;
    }
}

public static class HostCommand
{
    public static Command Create(TextWriter? error = null)
    {
        TextWriter standardError = error ?? Console.Error;
        Option<string> session = new("--session") { Required = true };
        Option<string> workingDirectory = new("--working-directory") { Required = true };
        Command command = new("__host", "Internal program host.") { Hidden = true };
        command.Options.Add(session);
        command.Options.Add(workingDirectory);
        command.SetAction((parseResult, cancellationToken) => RunAsync(
            parseResult.GetRequiredValue(session),
            Environment.GetEnvironmentVariable("TT_SESSION_ID"),
            Environment.GetEnvironmentVariable("TT_SESSION_NONCE"),
            parseResult.GetRequiredValue(workingDirectory),
            standardError,
            cancellationToken));
        return command;
    }

    public static async Task<int> RunAsync(
        string requestedSession,
        string? inheritedSession,
        string? nonce,
        string workingDirectory,
        TextWriter error,
        CancellationToken cancellationToken,
        ProviderSettingsStore? settingsStore = null,
        HttpClient? httpClient = null)
    {
        if (!string.Equals(requestedSession, inheritedSession, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(nonce) ||
            !Guid.TryParseExact(requestedSession, "N", out Guid sessionId))
        {
            await error.WriteLineAsync("Host session authentication failed.").ConfigureAwait(false);
            return 5;
        }

        if (!Directory.Exists(workingDirectory))
        {
            await error.WriteLineAsync("Host working directory does not exist.").ConfigureAwait(false);
            return 6;
        }

        ProviderSettings? settings;
        try
        {
            settings = await (settingsStore ?? new ProviderSettingsStore()).LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            await error.WriteLineAsync("Host could not read provider settings.").ConfigureAwait(false);
            return 7;
        }

        if (settings is null ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(settings.ApiKeyEnvironmentVariable)))
        {
            await error.WriteLineAsync("Host provider configuration or credential is unavailable.").ConfigureAwait(false);
            return 4;
        }

        bool ownsHttpClient = httpClient is null;
        httpClient ??= new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        });

        using CancellationTokenSource runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TranslationSession? translationSession = null;
        SystemClock? clock = null;
        EventPipeServer? eventServer = null;
        try
        {
            clock = new SystemClock();
            translationSession = new TranslationSession(sessionId, clock);
            translationSession.Start();
            Coord initialSize = ReadInitialSize();
            string eventPipeName = SessionPipeNames.Event(requestedSession, nonce);
            string controlPipeName = SessionPipeNames.Control(requestedSession, nonce);
            eventServer = new EventPipeServer(eventPipeName, requestedSession, nonce);
            Task eventReady = RunEventConnectionsAsync(eventServer, runtimeCancellation.Token);
            StatusAggregator statusAggregator = new(sessionId, clock);

            SecretDetector secretDetector = new();
            ChatCompletionTranslationProvider provider = new(
                settings,
                httpClient,
                Environment.GetEnvironmentVariable,
                secretDetector,
                request => translationSession.IsAuthorized(request.SessionGeneration, settings.Fingerprint));
            SubmittedCommandTracker submittedCommands = new();
            await using ProductionTranslationPipeline pipeline =
                ProductionRuntimeComposition.CreateTranslationPipeline(
                    sessionId,
                    provider,
                    eventServer,
                    settings.RequestTimeout,
                    (code, token) => PublishAggregatedStatusAsync(
                        eventServer,
                        statusAggregator.RecordProviderFailure(code, 1, null, null),
                        token),
                    initialSize.X,
                    analysisLineFilter: submittedCommands.ClassifyAnalysisLine,
                    secretDetector: secretDetector,
                    session: translationSession,
                    providerFingerprint: settings.Fingerprint,
                    privacyDecisionSink: (decision, token) => PublishAggregatedStatusAsync(
                        eventServer,
                        statusAggregator.RecordPrivacySkip(decision.ReasonCode, 1),
                        token),
                    overloadSink: (count, token) => PublishAggregatedStatusAsync(
                        eventServer,
                        statusAggregator.RecordOverload(count),
                        token));
            SessionControlHandler controlHandler = new(
                translationSession,
                pipeline,
                settings,
                eventServer.PublishAsync);
            await using ControlPipeServer controlServer = new(
                controlPipeName,
                requestedSession,
                nonce,
                controlHandler);
            Task controlTask = controlServer.RunAsync(runtimeCancellation.Token);

            await using ConPtySession pseudoConsole = ConPtySession.StartPowerShell(workingDirectory, initialSize);
            using ConsoleEncodingScope consoleEncoding = ConsoleEncodingScope.EnterUtf8();
            using ConsoleModeScope? consoleMode = ConsoleModeScope.TryEnterRawInput();
            using CancellationTokenSource interactiveCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(runtimeCancellation.Token);
            ConsoleInputRelay inputRelay = new(
                global::System.Console.OpenStandardInput(),
                pseudoConsole.Input,
                submittedCommands.Observe);
            ConsoleOutputRelay outputRelay = new(
                pseudoConsole.Output,
                global::System.Console.OpenStandardOutput(),
                pipeline);
            PseudoConsoleResizeMonitor resizeMonitor = new(
                pseudoConsole,
                resized: (columns, _) => pipeline.Resize(columns));
            Task inputTask = inputRelay.CopyAsync(interactiveCancellation.Token);
            Task outputTask = outputRelay.CopyAsync(runtimeCancellation.Token);
            Task resizeTask = resizeMonitor.RunAsync(interactiveCancellation.Token);

            int exitCode = await pseudoConsole.WaitForExitAsync(runtimeCancellation.Token).ConfigureAwait(false);
            SessionTeardown teardown = new(translationSession, clock);
            await teardown.ExecuteAsync(
                "shell-exit",
                exitCode,
                abnormal: false,
                async _ =>
                {
                    interactiveCancellation.Cancel();
                    await pseudoConsole.CompleteInputAsync().ConfigureAwait(false);
                    pseudoConsole.ClosePseudoConsole();
                    await outputTask.ConfigureAwait(false);
                },
                (status, token) => new ValueTask(eventServer.PublishAsync(
                    new StatusEventMessage(
                        "session-ended",
                        SessionProtocol.Version,
                        status.Code,
                        ExitCode: status.Count),
                    token)),
                CancellationToken.None).ConfigureAwait(false);

            runtimeCancellation.Cancel();
            await ObserveCancellationAsync(inputTask, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            await ObserveCancellationAsync(resizeTask).ConfigureAwait(false);
            await ObserveCancellationAsync(controlTask).ConfigureAwait(false);
            await ObserveCancellationAsync(eventReady).ConfigureAwait(false);
            return exitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TeardownFaultedSessionAsync(
                translationSession,
                clock,
                eventServer,
                "host-canceled").ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            UnauthorizedAccessException or Win32Exception)
        {
            await TeardownFaultedSessionAsync(
                translationSession,
                clock,
                eventServer,
                "host-fault").ConfigureAwait(false);
            await error.WriteLineAsync($"Host terminal runtime failed: {exception.Message}").ConfigureAwait(false);
            return 6;
        }
        finally
        {
            runtimeCancellation.Cancel();
            translationSession?.Dispose();
            if (eventServer is not null)
            {
                await eventServer.DisposeAsync().ConfigureAwait(false);
            }

            if (ownsHttpClient)
            {
                httpClient.Dispose();
            }
        }
    }

    private static async Task TeardownFaultedSessionAsync(
        TranslationSession? session,
        IClock? clock,
        EventPipeServer? eventServer,
        string reason)
    {
        if (session is null || clock is null || session.State == SessionState.Ended)
        {
            return;
        }

        SessionTeardown teardown = new(session, clock);
        await teardown.ExecuteAsync(
            reason,
            exitCode: null,
            abnormal: true,
            _ => Task.CompletedTask,
            eventServer is null
                ? null
                : (status, token) => new ValueTask(eventServer.PublishAsync(
                    new StatusEventMessage(
                        "session-ended",
                        SessionProtocol.Version,
                        status.Code),
                    token)),
            CancellationToken.None).ConfigureAwait(false);
    }


    private static Coord ReadInitialSize()
    {
        try
        {
            return new Coord(
                (short)Math.Clamp(global::System.Console.WindowWidth, 1, short.MaxValue),
                (short)Math.Clamp(global::System.Console.WindowHeight, 1, short.MaxValue));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return new Coord(120, 30);
        }
    }

    private static async Task RunEventConnectionsAsync(
        EventPipeServer server,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await server.WaitForClientAsync(cancellationToken).ConfigureAwait(false);
                await server.WaitForDisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static ValueTask PublishAggregatedStatusAsync(
        EventPipeServer eventServer,
        StatusEvent? status,
        CancellationToken cancellationToken)
    {
        if (status is null)
        {
            return ValueTask.CompletedTask;
        }

        string type = status.Kind switch
        {
            StatusKind.ProviderError => "provider-error",
            StatusKind.PrivacySkip => "privacy-skip",
            StatusKind.Degraded => "degraded",
            _ => "status",
        };
        return new ValueTask(eventServer.PublishAsync(
            new StatusEventMessage(type, SessionProtocol.Version, status.Code, status.Count),
            cancellationToken));
    }

    private static async Task ObserveCancellationAsync(Task task, TimeSpan? maximumWait = null)
    {
        try
        {
            if (maximumWait is TimeSpan timeout)
            {
                await task.WaitAsync(timeout).ConfigureAwait(false);
            }
            else
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    public static async Task<int> RunEventServerAsync(
        string requestedSession,
        string? inheritedSession,
        string? nonce,
        string workingDirectory,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedSession, inheritedSession, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(nonce))
        {
            await error.WriteLineAsync("Host session authentication failed.").ConfigureAwait(false);
            return 5;
        }

        if (!Directory.Exists(workingDirectory))
        {
            await error.WriteLineAsync("Host working directory does not exist.").ConfigureAwait(false);
            return 6;
        }

        try
        {
            string pipeName = SessionPipeNames.Event(requestedSession, nonce);
            await using EventPipeServer server = new(pipeName, requestedSession, nonce);
            await server.WaitForClientAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Host event pipe failed: {exception.Message}").ConfigureAwait(false);
            return 6;
        }
    }
}
