using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Tests.TestDoubles;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Tests.Integration;

[TestClass]
public sealed class AskLastFailureJourneyTests
{
    [TestMethod]
    public async Task AllStrictCaptureFailuresSkipProviderWithoutQuestionOnlyFallback()
    {
        PreviousCommandResult[] failures =
        [
            PreviousCommandResult.NoPreviousCommand(),
            PreviousCommandResult.NoOutput(),
            PreviousCommandResult.CaptureDisabled(),
            PreviousCommandResult.CaptureUnavailable(),
            PreviousCommandResult.UnreliableOrCorrupt(),
        ];

        foreach (PreviousCommandResult failure in failures)
        {
            RejectingProvider provider = new();
            AskLastAssistanceCoordinator coordinator = new(
                () => failure,
                provider,
                new ApprovedAssistanceRequestAuthorizer());

            AskAssistanceOutcome outcome = await coordinator.ExecuteAsync("why?", CancellationToken.None);

            Assert.AreEqual(Expected(failure.Kind), outcome.Failure);
            Assert.AreEqual(0, provider.Calls);
            Assert.IsNull(outcome.Request);
        }
    }

    [TestMethod]
    public async Task WrongSessionAndAmbiguousInterruptionFailClosed()
    {
        CaptureSessionId current = new(Guid.NewGuid(), "current");
        CaptureSessionId other = new(Guid.NewGuid(), "other");
        CommandBoundary boundary = new(other, 1, "build", true, 1, false);
        CapturedCommand crossSession = new(other, 1, "build", "failed", boundary,
            LocalCaptureCompleteness.Complete, 6, false);
        PreviousCommandRetrievalState wrongSession = new(
            CapturePreference.Enabled, CaptureHealthState.Healthy, current, [crossSession], true);
        RejectingProvider provider = new();

        AskAssistanceOutcome wrong = await new AskLastAssistanceCoordinator(
            () => PreviousCommandRetriever.Retrieve(wrongSession), provider,
            new ApprovedAssistanceRequestAuthorizer()).ExecuteAsync("why?", CancellationToken.None);
        AskAssistanceOutcome ambiguous = await new AskLastAssistanceCoordinator(
            PreviousCommandResult.UnreliableOrCorrupt, provider,
            new ApprovedAssistanceRequestAuthorizer()).ExecuteAsync("why?", CancellationToken.None);

        Assert.AreEqual(AssistanceFailureKind.UnreliableOrCorrupt, wrong.Failure);
        Assert.AreEqual(AssistanceFailureKind.UnreliableOrCorrupt, ambiguous.Failure);
        Assert.AreEqual(0, provider.Calls);
    }

    private static AssistanceFailureKind Expected(PreviousCommandResultKind kind) => kind switch
    {
        PreviousCommandResultKind.NoPreviousCommand => AssistanceFailureKind.NoPreviousCommand,
        PreviousCommandResultKind.NoOutput => AssistanceFailureKind.NoOutput,
        PreviousCommandResultKind.CaptureDisabled => AssistanceFailureKind.CaptureDisabled,
        PreviousCommandResultKind.CaptureUnavailable => AssistanceFailureKind.CaptureUnavailable,
        _ => AssistanceFailureKind.UnreliableOrCorrupt,
    };

    private sealed class RejectingProvider : IAssistanceProvider
    {
        public int Calls { get; private set; }
        public Task<AssistanceResult> CompleteAsync(AuthorizedAssistanceRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new AssertFailedException("Provider must not be called for a strict capture failure.");
        }
    }
}
