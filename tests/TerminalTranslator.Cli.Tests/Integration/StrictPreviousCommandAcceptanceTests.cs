using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class StrictPreviousCommandAcceptanceTests
{
    [TestMethod]
    public async Task ControlledComposition_CoversStrictNormalMultilineCustomPromptAndSelfPollution()
    {
        CaptureSessionId session = new(Guid.Parse("88888888-8888-8888-8888-888888888888"), "nonce-a");
        RecordingProvider provider = new();
        IReadOnlyList<CapturedCommand> records =
        [
            Command(session, 1, "& {`n Write-Output hello`n}", "custom λ> Hello", false),
            Command(session, 2, "tt last", "prior translation", true),
        ];
        PreviousCommandRetrievalState state = new(CapturePreference.Enabled, CaptureHealthState.Healthy, session, records, true);
        LastAssistanceCoordinator coordinator = new(
            () => PreviousCommandRetriever.Retrieve(state), provider, new ApprovedAssistanceRequestAuthorizer());
        using StringWriter output = new();

        int exitCode = await LastCommand.Create(coordinator.ExecuteAsync, output).Parse([]).InvokeAsync();

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("& {`n Write-Output hello`n}", provider.Requests.Single().Request.CommandText);
        Assert.AreEqual("custom λ> Hello", provider.Requests.Single().Request.SelectedOutput);
        StringAssert.Contains(output.ToString(), "[翻译]");
    }

    [TestMethod]
    public void TwoExplicitSessionsNeverCrossAndNoPreviousNoOutputRemainExclusive()
    {
        CaptureSessionId a = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "nonce-a");
        CaptureSessionId b = new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "nonce-b");
        Assert.AreEqual(PreviousCommandResultKind.NoPreviousCommand,
            PreviousCommandRetriever.Retrieve(new(CapturePreference.Enabled, CaptureHealthState.Healthy, a, [], true)).Kind);
        Assert.AreEqual(PreviousCommandResultKind.NoOutput,
            PreviousCommandRetriever.Retrieve(new(CapturePreference.Enabled, CaptureHealthState.Healthy, a,
                [Command(a, 1, "cd ..", string.Empty, false)], true)).Kind);
        Assert.AreEqual(PreviousCommandResultKind.UnreliableOrCorrupt,
            PreviousCommandRetriever.Retrieve(new(CapturePreference.Enabled, CaptureHealthState.Healthy, a,
                [Command(b, 1, "git status", "other session", false)], true)).Kind);
    }

    private static CapturedCommand Command(CaptureSessionId session, long sequence, string text, string output, bool contextual)
    {
        CommandBoundary boundary = new(session, sequence, text, true, 0, false);
        return new(session, sequence, text, output, boundary, LocalCaptureCompleteness.Complete,
            System.Text.Encoding.UTF8.GetByteCount(output), contextual);
    }

    private sealed class RecordingProvider : IAssistanceProvider
    {
        public List<AuthorizedAssistanceRequest> Requests { get; } = [];
        public Task<AssistanceResult> CompleteAsync(AuthorizedAssistanceRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new AssistanceResult("翻译", "建议"));
        }
    }
}
