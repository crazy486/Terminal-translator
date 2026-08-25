using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class ProductionCommandCompositionTests
{
    [TestMethod]
    public async Task ProductionRoot_LastAndAskLast_UseOneSharedContextualRetrieverAndCompleteProviderChain()
    {
        RecordingAssistanceProvider provider = new();
        int retrieverConstructions = 0;
        int retrievals = 0;
        OnDemandAssistanceRuntimeComposition composition = Composition(
            provider,
            () =>
            {
                retrieverConstructions++;
                return _ =>
                {
                    retrievals++;
                    return Task.FromResult(PreviousCommandResult.Success(Snapshot()));
                };
            });
        var root = CommandFactory.CreateRootCommand(composition);

        Assert.AreEqual(0, await root.Parse(["last"]).InvokeAsync());
        provider.Result = AssistanceResult.CreateAnswer("context answer");
        Assert.AreEqual(0, await root.Parse(["ask", "last", "why?"]).InvokeAsync());

        Assert.AreEqual(1, retrieverConstructions);
        Assert.AreEqual(2, retrievals);
        CollectionAssert.AreEqual(
            new[] { AssistanceRequestKind.LastTranslation, AssistanceRequestKind.QuestionWithPreviousCommand },
            provider.Requests.Select(request => request.Request.Kind).ToArray());
        Assert.IsTrue(root.Subcommands.Any(command => command.Name == "start"));
        Assert.IsTrue(root.Subcommands.Any(command => command.Name == "last"));
        Assert.IsTrue(root.Subcommands.Any(command => command.Name == "ask"));
    }

    [TestMethod]
    public async Task ProductionRoot_QuestionOnly_NeverConstructsContextualCaptureDependencies()
    {
        RecordingAssistanceProvider provider = new() { Result = AssistanceResult.CreateAnswer("answer") };
        OnDemandAssistanceRuntimeComposition composition = Composition(
            provider,
            () => throw new AssertFailedException("Question-only composition constructed the contextual retriever."));

        int exitCode = await CommandFactory.CreateRootCommand(composition)
            .Parse(["ask", "what", "now?"])
            .InvokeAsync();

        Assert.AreEqual(0, exitCode);
        AuthorizedAssistanceRequest sent = provider.Requests.Single();
        Assert.AreEqual(AssistanceRequestKind.QuestionOnly, sent.Request.Kind);
        Assert.AreEqual(string.Empty, sent.Request.CommandText);
        Assert.AreEqual(string.Empty, sent.Request.SelectedOutput);
    }

    [TestMethod]
    public async Task ProductionRoot_ContextFailurePreservesCaptureOutcomeWithoutResolvingProviderConfiguration()
    {
        OnDemandAssistanceRuntimeComposition composition = new(
            _ => throw new AssertFailedException("Capture failure must be returned before provider configuration is resolved."),
            _ => throw new AssertFailedException("Provider must not be constructed."),
            _ => throw new AssertFailedException("Authorizer must not be constructed."),
            () => _ => Task.FromResult(PreviousCommandResult.CaptureDisabled()));

        int exitCode = await CommandFactory.CreateRootCommand(composition).Parse(["last"]).InvokeAsync();

        Assert.AreEqual(5, exitCode);
    }

    [TestMethod]
    public async Task ProductionRoot_UnauthorizedAndSecretSelections_StopBeforeProvider()
    {
        RecordingAssistanceProvider provider = new();
        OnDemandAssistanceRuntimeComposition denied = new(
            _ => Task.FromResult<ProviderSettings?>(Settings()),
            _ => provider,
            _ => new DenyingAuthorizer(),
            () => _ => Task.FromResult(PreviousCommandResult.Success(Snapshot())));
        Assert.AreEqual(5, await CommandFactory.CreateRootCommand(denied).Parse(["last"]).InvokeAsync());

        OnDemandAssistanceRuntimeComposition secret = Composition(
            provider,
            () => _ => Task.FromResult(PreviousCommandResult.Success(
                Snapshot(output: "failure sk-AAAAAAAAAAAAAAAAAAAA"))));
        Assert.AreEqual(5, await CommandFactory.CreateRootCommand(secret).Parse(["last"]).InvokeAsync());

        Assert.AreEqual(0, provider.Requests.Count);
    }

    [TestMethod]
    public async Task PublishedLikeRoot_Last_UsesValidatedSessionProofCommittedStoreAndProductionRetriever()
    {
        using TemporaryDirectory temporary = new();
        string settingsDirectory = Path.Combine(temporary.Path, "settings");
        string captureRoot = Path.Combine(temporary.Path, "capture");
        CapturePreferenceStore preference = new(settingsDirectory);
        await preference.SaveAsync(CapturePreference.Enabled);
        Guid sessionId = Guid.Parse("90909090-9090-9090-9090-909090909090");
        string nonce = "published-like-nonce";
        CaptureOwnerIdentity owner = new("S-1-5-21-test", 4242, 638917344000000000);
        CaptureSessionProof proof = new(sessionId, nonce, owner, CaptureSessionBootstrap.CurrentIntegrationVersion);
        string sessionDirectory = Path.Combine(captureRoot, sessionId.ToString("N"));
        Directory.CreateDirectory(sessionDirectory);
        await CaptureSessionBootstrap.WriteOwnerManifestAsync(
            sessionDirectory,
            new CaptureOwnerManifest(1, proof, 2, CaptureHealthState.Healthy));
        CaptureSessionId session = new(sessionId, nonce);
        CommandBoundary boundary = new(session, 1, "dotnet build", true, 1, false);
        CapturedCommand command = new(
            session, 1, boundary.CommandText, "English compiler failure", boundary,
            LocalCaptureCompleteness.Complete, 24, false);
        RetainedCaptureStore store = new(sessionDirectory);
        Assert.AreEqual(FinalizationOutcome.Published, (await new CapturedCommandFinalizer(store).FinalizeAsync(
            new CapturedCommandCandidate(
                "published-like",
                1,
                System.Text.Encoding.UTF8.GetBytes(command.Output),
                RetainedCommandRecordCodec.SerializeMetadata(command),
                true,
                command))).Outcome);
        Dictionary<string, string> environment = new()
        {
            [CaptureSessionIdentity.SessionEnvironmentVariable] = sessionId.ToString("N"),
            [CaptureSessionIdentity.NonceEnvironmentVariable] = nonce,
        };
        ProductionPreviousCommandRetriever retriever = new(
            preference,
            captureRoot,
            name => environment.GetValueOrDefault(name),
            () => (true, owner));
        RecordingAssistanceProvider provider = new();
        OnDemandAssistanceRuntimeComposition composition = Composition(provider, () => retriever.RetrieveAsync);

        Assert.AreEqual(0, await CommandFactory.CreateRootCommand(composition).Parse(["last"]).InvokeAsync());
        Assert.AreEqual("dotnet build", provider.Requests.Single().Request.CommandText);
        Assert.AreEqual("English compiler failure", provider.Requests.Single().Request.SelectedOutput);
    }

    private static OnDemandAssistanceRuntimeComposition Composition(
        IAssistanceProvider provider,
        Func<Func<CancellationToken, Task<PreviousCommandResult>>> retrieverFactory) => new(
        _ => Task.FromResult<ProviderSettings?>(Settings()),
        _ => provider,
        _ => new ApprovedAssistanceRequestAuthorizer(),
        retrieverFactory);

    private static ProviderSettings Settings() => ProviderSettings.Create(
        new Uri("https://provider.example/v1/chat/completions"),
        "test-model",
        "TT_TEST_API_KEY",
        TimeSpan.FromSeconds(1));

    private static PreviousCommandSnapshot Snapshot(string output = "English build failure") => new(
        new CaptureSessionId(Guid.Parse("78787878-7878-7878-7878-787878787878"), "nonce"),
        7,
        "dotnet build",
        output,
        1,
        false,
        LocalCaptureCompleteness.Complete,
        System.Text.Encoding.UTF8.GetByteCount(output));

    private sealed class DenyingAuthorizer : IAssistanceRequestAuthorizer
    {
        public Task<AssistanceAuthorizationDecision> AuthorizeAsync(
            AssistanceRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(AssistanceAuthorizationDecision.Denied());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-production-root-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
