using System.CommandLine;
using System.ComponentModel;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Parsing;
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
        try
        {
            Coord initialSize = ReadInitialSize();
            string eventPipeName = SessionPipeNames.Event(requestedSession, nonce);
            string controlPipeName = SessionPipeNames.Control(requestedSession, nonce);
            await using EventPipeServer eventServer = new(eventPipeName, requestedSession, nonce);
            Task eventReady = eventServer.WaitForClientAsync(runtimeCancellation.Token);

            ChatCompletionTranslationProvider provider = new(settings, httpClient, Environment.GetEnvironmentVariable);
            await using ProductionTranslationPipeline pipeline =
                ProductionRuntimeComposition.CreateTranslationPipeline(
                    sessionId,
                    provider,
                    eventServer,
                    settings.RequestTimeout,
                    (code, token) => new ValueTask(eventServer.PublishAsync(
                        new StatusEventMessage(
                            "provider-error",
                            SessionProtocol.Version,
                            ToProtocolErrorCode(code)),
                        token)),
                    initialSize.X);
            await using MinimalControlPipeServer controlServer = new(
                controlPipeName,
                requestedSession,
                settings.Fingerprint,
                async token =>
                {
                    await eventReady.WaitAsync(token).ConfigureAwait(false);
                    pipeline.Enable();
                    _ = eventServer.PublishAsync(new StateEventMessage(
                        "state", SessionProtocol.Version, "enabled", settings.Endpoint.Host, settings.Model), token);
                });
            Task controlTask = controlServer.RunAsync(runtimeCancellation.Token);

            await using ConPtySession pseudoConsole = ConPtySession.StartPowerShell(workingDirectory, initialSize);
            using ConsoleEncodingScope consoleEncoding = ConsoleEncodingScope.EnterUtf8();
            using ConsoleModeScope? consoleMode = ConsoleModeScope.TryEnterRawInput();
            using CancellationTokenSource interactiveCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(runtimeCancellation.Token);
            ConsoleInputRelay inputRelay = new(global::System.Console.OpenStandardInput(), pseudoConsole.Input);
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
            interactiveCancellation.Cancel();
            await pseudoConsole.CompleteInputAsync().ConfigureAwait(false);
            pseudoConsole.ClosePseudoConsole();
            await outputTask.ConfigureAwait(false);

            runtimeCancellation.Cancel();
            await ObserveCancellationAsync(inputTask, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            await ObserveCancellationAsync(resizeTask).ConfigureAwait(false);
            await ObserveCancellationAsync(controlTask).ConfigureAwait(false);
            await ObserveCancellationAsync(eventReady).ConfigureAwait(false);
            return exitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            UnauthorizedAccessException or Win32Exception)
        {
            await error.WriteLineAsync($"Host terminal runtime failed: {exception.Message}").ConfigureAwait(false);
            return 6;
        }
        finally
        {
            runtimeCancellation.Cancel();
            if (ownsHttpClient)
            {
                httpClient.Dispose();
            }
        }
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

    private static string ToProtocolErrorCode(TranslationErrorCode code) => code switch
    {
        TranslationErrorCode.Canceled => "canceled",
        TranslationErrorCode.Timeout => "timeout",
        TranslationErrorCode.Authentication => "authentication",
        TranslationErrorCode.RateLimited => "rate-limited",
        TranslationErrorCode.Unavailable => "unavailable",
        TranslationErrorCode.InvalidResponse => "invalid-response",
        _ => "unknown",
    };

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
