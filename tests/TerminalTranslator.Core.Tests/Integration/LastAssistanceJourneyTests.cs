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

    private static LastAssistanceCoordinator Create(string output, int? exitCode, IAssistanceProvider provider) => new(
        () => PreviousCommandResult.Success(new PreviousCommandSnapshot(
            Session, 1, "dotnet build", output, exitCode, false,
            LocalCaptureCompleteness.Complete, System.Text.Encoding.UTF8.GetByteCount(output))),
        provider,
        new ApprovedAssistanceRequestAuthorizer());

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
}
