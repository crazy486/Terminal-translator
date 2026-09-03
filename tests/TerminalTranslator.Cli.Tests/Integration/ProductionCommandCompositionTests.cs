using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Core.Translation;
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

    [TestMethod]
    public async Task ProductionTranscriptPath_LastSendsExactlyCleanedCommandOutputToFakeProvider()
    {
        using TemporaryDirectory temporary = new();
        const string commandText = "Write-Output \"The package installation completed successfully.\"";
        const string commandOutput = "The package installation completed successfully.";
        (CapturePreferenceStore preference, string captureRoot, CaptureSessionProof proof, CaptureOwnerIdentity owner) =
            await FinalizeTranscriptAsync(temporary.Path, commandText, commandOutput);
        RecordingAssistanceProvider provider = new();
        ProductionPreviousCommandRetriever retriever = CreateProductionRetriever(preference, captureRoot, proof, owner);
        OnDemandAssistanceRuntimeComposition composition = Composition(provider, () => retriever.RetrieveAsync);

        Assert.AreEqual(0, await CommandFactory.CreateRootCommand(composition).Parse(["last"]).InvokeAsync());

        AuthorizedAssistanceRequest sent = provider.Requests.Single();
        Assert.AreEqual(commandOutput, sent.Request.SelectedOutput);
        Assert.IsFalse(sent.Request.SelectedOutput.Contains("脚本结束", StringComparison.Ordinal));
        Assert.IsFalse(sent.Request.SelectedOutput.Contains("20260829175932", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProductionTranscriptPath_ZeroOutputReturnsNoOutputAndNeverFallsBackOrCallsProvider()
    {
        using TemporaryDirectory temporary = new();
        const string commandText = "Set-Location ..";
        (CapturePreferenceStore preference, string captureRoot, CaptureSessionProof proof, CaptureOwnerIdentity owner) =
            await FinalizeTranscriptAsync(temporary.Path, commandText, string.Empty);
        ProductionPreviousCommandRetriever retriever = CreateProductionRetriever(preference, captureRoot, proof, owner);
        RejectIfCalledAssistanceProvider provider = new();
        OnDemandAssistanceRuntimeComposition composition = Composition(provider, () => retriever.RetrieveAsync);

        LastAssistanceOutcome outcome = await composition.ExecuteLastAsync(CancellationToken.None);

        Assert.AreEqual(AssistanceFailureKind.NoOutput, outcome.Failure);
        Assert.IsNull(outcome.Request);
    }

    [TestMethod]
    [DataRow(
        "Get-Item \"Z:\\TT_DEFINITELY_MISSING_002\"",
        "Get-Item : Cannot find drive.\r\n+ Get-Item \"Z:\\TT_DEFINITELY_MISSING_002\"\r\n    + CategoryInfo : ObjectNotFound\r\n    + FullyQualifiedErrorId : DriveNotFound,Microsoft.PowerShell.Commands.GetItemCommand")]
    [DataRow(
        "native-stream-probe",
        "The native command completed successfully.\r\nThe native command reported a recoverable warning.")]
    [DataRow(
        "git status",
        "On branch test\r\nChanges not staged for commit:\r\n  (use \"git add <file>...\" to update what will be committed)")]
    public async Task ProductionTranscriptPath_ErrorNativeAndGitLikeOutputReachAuthorizedProviderUnchanged(
        string commandText,
        string commandOutput)
    {
        using TemporaryDirectory temporary = new();
        (CapturePreferenceStore preference, string captureRoot, CaptureSessionProof proof, CaptureOwnerIdentity owner) =
            await FinalizeTranscriptAsync(temporary.Path, commandText, commandOutput);
        RecordingAssistanceProvider provider = new();
        ProductionPreviousCommandRetriever retriever = CreateProductionRetriever(preference, captureRoot, proof, owner);
        OnDemandAssistanceRuntimeComposition composition = Composition(provider, () => retriever.RetrieveAsync);

        LastAssistanceOutcome outcome = await composition.ExecuteLastAsync(CancellationToken.None);

        Assert.AreEqual(AssistanceFailureKind.None, outcome.Failure);
        Assert.AreEqual(commandOutput, provider.Requests.Single().Request.SelectedOutput);
    }

    [TestMethod]
    public async Task TtLastTimeoutExitFive_CannotPolluteFollowingCmdletAuthorizedPayload()
    {
        OnDemandAssistanceRuntimeComposition timeout = Composition(
            new TimeoutProvider(),
            () => _ => Task.FromResult(PreviousCommandResult.Success(Snapshot())));
        Assert.AreEqual(5, await CommandFactory.CreateRootCommand(timeout).Parse(["last"]).InvokeAsync());

        using TemporaryDirectory temporary = new();
        const string command = "Write-Output \"The package installation completed successfully.\"";
        const string output = "The package installation completed successfully.";
        (CapturePreferenceStore preference, string captureRoot, CaptureSessionProof proof, CaptureOwnerIdentity owner) =
            await FinalizeTranscriptAsync(temporary.Path, command, output);
        RecordingAssistanceProvider provider = new();
        ProductionPreviousCommandRetriever retriever = CreateProductionRetriever(preference, captureRoot, proof, owner);
        LastAssistanceOutcome outcome = await Composition(provider, () => retriever.RetrieveAsync)
            .ExecuteLastAsync(CancellationToken.None);

        Assert.AreEqual(AssistanceFailureKind.None, outcome.Failure);
        AuthorizedAssistanceRequest sent = provider.Requests.Single();
        Assert.IsTrue(sent.Request.Termination.PowerShellSucceeded);
        Assert.IsNull(sent.Request.Termination.NativeExitCode);
        string serialized = AssistanceRequestVariableInputSerializer.Serialize(sent.Request);
        StringAssert.Contains(serialized, "powerShellSucceeded=true");
        StringAssert.Contains(serialized, "nativeExitCode=unknown");
        Assert.IsFalse(serialized.Contains("nativeExitCode=5", StringComparison.Ordinal));
        sent.AssertValid();
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

    private static async Task<(CapturePreferenceStore Preference, string CaptureRoot, CaptureSessionProof Proof, CaptureOwnerIdentity Owner)>
        FinalizeTranscriptAsync(string root, string commandText, string commandOutput)
    {
        string settingsDirectory = Path.Combine(root, "settings");
        string captureRoot = Path.Combine(root, "capture");
        CapturePreferenceStore preference = new(settingsDirectory);
        await preference.SaveAsync(CapturePreference.Enabled);
        CaptureOwnerIdentity owner = new("S-1-5-21-production-envelope", 4242, 638917344000000000);
        CaptureBootstrapResult bootstrap = await new CaptureSessionBootstrap(captureRoot).InitializeAsync(
            CapturePreference.Enabled,
            owner,
            CaptureSessionBootstrap.CurrentIntegrationVersion,
            isHostedFeature001Session: false);
        const string separator = "**********************";
        string outputLine = commandOutput.Length == 0 ? string.Empty : commandOutput + "\r\n";
        string transcript = string.Join("\r\n",
            separator,
            "Windows PowerShell 脚本开始",
            "开始时间: 20260829175900",
            "用户名: test",
            separator,
            $"CUSTOM> {commandText}",
            outputLine + separator,
            "Windows PowerShell 脚本结束",
            "结束时间: 20260829175932",
            separator,
            string.Empty);
        await File.WriteAllTextAsync(bootstrap.StagingPath!, transcript);
        CaptureBoundaryProcessingResult result = await new CaptureBoundaryProcessor(captureRoot).ProcessAsync(
            new CaptureBoundaryRequest(
                bootstrap.Proof!.SessionId,
                bootstrap.Proof.Nonce,
                owner,
                CaptureSessionBootstrap.CurrentIntegrationVersion,
                bootstrap.StagingPath!,
                1,
                commandText,
                Succeeded: true,
                NativeExitCode: null),
            CapturePreference.Enabled);
        Assert.AreEqual(CaptureBoundaryStatus.ReadyForNextInterval, result.Status);
        return (preference, captureRoot, bootstrap.Proof, owner);
    }

    private static ProductionPreviousCommandRetriever CreateProductionRetriever(
        CapturePreferenceStore preference,
        string captureRoot,
        CaptureSessionProof proof,
        CaptureOwnerIdentity owner)
    {
        Dictionary<string, string> environment = new()
        {
            [CaptureSessionIdentity.SessionEnvironmentVariable] = proof.SessionId.ToString("N"),
            [CaptureSessionIdentity.NonceEnvironmentVariable] = proof.Nonce,
        };
        return new ProductionPreviousCommandRetriever(
            preference,
            captureRoot,
            name => environment.GetValueOrDefault(name),
            () => (true, owner));
    }

    private sealed class DenyingAuthorizer : IAssistanceRequestAuthorizer
    {
        public Task<AssistanceAuthorizationDecision> AuthorizeAsync(
            AssistanceRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(AssistanceAuthorizationDecision.Denied());
    }

    private sealed class TimeoutProvider : IAssistanceProvider
    {
        public Task<AssistanceResult> CompleteAsync(
            AuthorizedAssistanceRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<AssistanceResult>(new TranslationProviderException(TranslationErrorCode.Timeout));
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
