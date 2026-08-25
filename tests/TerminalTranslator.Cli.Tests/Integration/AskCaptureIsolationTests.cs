using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class AskCaptureIsolationTests
{
    [TestMethod]
    public async Task OrdinaryAsk_DoesNotResolveIdentityOpenRecordsOrConstructContextualRetriever()
    {
        CaptureSessionId validSession = new(Guid.NewGuid(), "valid-but-forbidden");
        PreviousCommandRetrievalState validCapture = new(
            CapturePreference.Enabled,
            CaptureHealthState.Healthy,
            validSession,
            [Command(validSession)],
            true);
        _ = validCapture;

        RecordingAssistanceProvider provider = new()
        {
            Result = AssistanceResult.CreateAnswer("独立回答"),
        };
        AskAssistanceCoordinator coordinator = new(
            provider,
            new ApprovedAssistanceRequestAuthorizer());
        bool contextualFactoryConstructed = false;
        using StringWriter output = new();
        var command = AskCommand.Create(
            coordinator.ExecuteAsync,
            output: output,
            contextualExecutorFactory: () =>
            {
                contextualFactoryConstructed = true;
                throw new AssertFailedException("Contextual retriever composition must remain lazy.");
            });

        int exitCode = await command.Parse(["why?"]).InvokeAsync();

        Assert.AreEqual(0, exitCode);
        Assert.IsFalse(contextualFactoryConstructed);
        AuthorizedAssistanceRequest sent = provider.Requests.Single();
        Assert.AreEqual(AssistanceRequestKind.QuestionOnly, sent.Request.Kind);
        Assert.AreEqual(string.Empty, sent.Request.CommandText);
        Assert.AreEqual(string.Empty, sent.Request.SelectedOutput);
        Assert.IsFalse(typeof(AskAssistanceCoordinator).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType == typeof(Func<PreviousCommandResult>)));
        Assert.AreEqual("独立回答", output.ToString().Trim());
    }

    private static CapturedCommand Command(CaptureSessionId session)
    {
        CommandBoundary boundary = new(session, 1, "secret previous command", true, 1, false);
        return new(session, 1, boundary.CommandText, "secret previous output", boundary,
            LocalCaptureCompleteness.Complete, 22, false);
    }
}
