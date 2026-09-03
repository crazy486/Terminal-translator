using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Tests.TestDoubles;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Core.Tests.Integration;

[TestClass]
public sealed class LastAssistanceJourneyTests
{
    private static readonly CaptureSessionId Session = new(Guid.Parse("44444444-4444-4444-4444-444444444444"), "nonce");

    [TestMethod]
    public async Task ExecuteAsync_PreservesOrdinaryErrorAndMixedStreamContext()
    {
        foreach ((string output, int? exit) in new[]
        {
            ("Build succeeded.", (int?)0),
            ("Write-Error: missing path", (int?)1),
            ("stdout one\nstderr two\nstdout three", (int?)9),
        })
        {
            RecordingProvider provider = new();
            LastAssistanceCoordinator coordinator = Create(output, exit, provider);

            LastAssistanceOutcome outcome = await coordinator.ExecuteAsync(CancellationToken.None);

            Assert.AreEqual(AssistanceFailureKind.None, outcome.Failure);
            Assert.AreEqual(output, provider.Requests.Single().Request.SelectedOutput);
            Assert.AreEqual(exit, provider.Requests.Single().Request.Termination.ExitCode);
            Assert.IsNotNull(outcome.Result);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_MapsProviderTimeoutAndErrorWithoutExecutionAuthority()
    {
        LastAssistanceOutcome timeout = await Create("Failed", 1,
            new ThrowingProvider(new TranslationProviderException(TranslationErrorCode.Timeout))).ExecuteAsync(CancellationToken.None);
        LastAssistanceOutcome error = await Create("Failed", 1,
            new ThrowingProvider(new TranslationProviderException(TranslationErrorCode.Unavailable))).ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(AssistanceFailureKind.ProviderTimeout, timeout.Failure);
        Assert.AreEqual(AssistanceFailureKind.ProviderError, error.Failure);
        Assert.IsFalse(typeof(LastAssistanceCoordinator).GetMethods().Any(method => method.Name.Contains("ExecuteCommand")));
    }

    [TestMethod]
    public async Task ExecuteAsync_PreservesProviderFailureLayerForSafeUx()
    {
        foreach ((TranslationProviderFailureSource source, AssistanceFailureKind expected) in new[]
        {
            (TranslationProviderFailureSource.Network, AssistanceFailureKind.ProviderNetworkFailure),
            (TranslationProviderFailureSource.HttpStatus, AssistanceFailureKind.ProviderHttpFailure),
            (TranslationProviderFailureSource.Authentication, AssistanceFailureKind.ProviderHttpFailure),
            (TranslationProviderFailureSource.RateLimit, AssistanceFailureKind.ProviderHttpFailure),
            (TranslationProviderFailureSource.MalformedResponse, AssistanceFailureKind.ProviderMalformedResponse),
        })
        {
            TranslationProviderException providerFailure = new(
                TranslationErrorCode.Unavailable,
                new TranslationProviderFailureDetails(source));

            LastAssistanceOutcome outcome = await Create(
                "Failed",
                1,
                new ThrowingProvider(providerFailure)).ExecuteAsync(CancellationToken.None);

            Assert.AreEqual(expected, outcome.Failure);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_StrictPreviousCommandDoesNotSkipInterveningLastExitCode()
    {
        CapturedCommand native = Captured(1, "cmd /d /c echo", "The package installation completed successfully.", 5);
        CapturedCommand lastExitCode = Captured(2, "$LASTEXITCODE", "5", null);
        CapturedCommand contextual = Captured(3, "tt last", "No translatable English content was found.", null, contextual: true);
        PreviousCommandResult withInterveningCommand = PreviousCommandRetriever.Retrieve(State([
            native,
            lastExitCode,
            contextual,
        ]));
        RecordingProvider blockedProvider = new();
        RecordingPipelineDiagnostics blockedDiagnostics = new();

        LastAssistanceOutcome blocked = await new LastAssistanceCoordinator(
            () => withInterveningCommand,
            blockedProvider,
            new ApprovedAssistanceRequestAuthorizer(),
            diagnostics: blockedDiagnostics).ExecuteAsync(CancellationToken.None);

        Assert.AreEqual("$LASTEXITCODE", withInterveningCommand.Snapshot!.CommandText);
        Assert.AreEqual("5", withInterveningCommand.Snapshot.Output);
        Assert.AreEqual(AssistanceFailureKind.NoTranslatableEnglish, blocked.Failure);
        Assert.IsEmpty(blockedProvider.Requests);
        Assert.AreEqual(false, blockedDiagnostics.Items.Single(item =>
            item.Stage == AssistancePipelineStage.Eligibility).EnglishEligible);

        PreviousCommandResult direct = PreviousCommandRetriever.Retrieve(State([native, contextual]));
        RecordingProvider directProvider = new();
        RecordingPipelineDiagnostics directDiagnostics = new();
        LastAssistanceOutcome sent = await new LastAssistanceCoordinator(
            () => direct,
            directProvider,
            new ApprovedAssistanceRequestAuthorizer(),
            diagnostics: directDiagnostics).ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(AssistanceFailureKind.None, sent.Failure);
        Assert.AreEqual(native.Output, directProvider.Requests.Single().Request.SelectedOutput);
        Assert.AreEqual(true, directDiagnostics.Items.Single(item =>
            item.Stage == AssistancePipelineStage.Eligibility).EnglishEligible);
        Assert.AreEqual(
            System.Text.Encoding.UTF8.GetByteCount(native.Output),
            directDiagnostics.Items.Single(item => item.Stage == AssistancePipelineStage.Selection).SelectedOutputUtf8Bytes);
    }

    private static LastAssistanceCoordinator Create(string output, int? exitCode, IAssistanceProvider provider) => new(
        () => PreviousCommandResult.Success(new PreviousCommandSnapshot(
            Session, 1, "dotnet build", output, exitCode, false,
            LocalCaptureCompleteness.Complete, System.Text.Encoding.UTF8.GetByteCount(output))),
        provider,
        new ApprovedAssistanceRequestAuthorizer());

    private static CapturedCommand Captured(
        long sequence,
        string command,
        string output,
        int? nativeExitCode,
        bool contextual = false) =>
        new(
            Session,
            sequence,
            command,
            output,
            new CommandBoundary(Session, sequence, command, true, nativeExitCode, false),
            LocalCaptureCompleteness.Complete,
            System.Text.Encoding.UTF8.GetByteCount(output),
            contextual);

    private static PreviousCommandRetrievalState State(IReadOnlyList<CapturedCommand> records) => new(
        CapturePreference.Enabled,
        CaptureHealthState.Healthy,
        Session,
        records,
        IsStoreReliable: true);

    private sealed class RecordingProvider : IAssistanceProvider
    {
        public List<AuthorizedAssistanceRequest> Requests { get; } = [];
        public Task<AssistanceResult> CompleteAsync(AuthorizedAssistanceRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new AssistanceResult("翻译", "建议"));
        }
    }

    private sealed class ThrowingProvider(Exception failure) : IAssistanceProvider
    {
        public Task<AssistanceResult> CompleteAsync(AuthorizedAssistanceRequest request, CancellationToken cancellationToken) =>
            Task.FromException<AssistanceResult>(failure);
    }

    private sealed class RecordingPipelineDiagnostics : IAssistancePipelineDiagnosticSink
    {
        public List<AssistancePipelineDiagnostic> Items { get; } = [];

        public void Write(AssistancePipelineDiagnostic diagnostic) => Items.Add(diagnostic);
    }
}
