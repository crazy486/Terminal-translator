using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Core.Privacy;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Cli.Commands;

public sealed class OnDemandAssistanceRuntimeComposition
{
    private readonly Func<CancellationToken, Task<ProviderSettings?>> _settingsLoader;
    private readonly Func<ProviderSettings, IAssistanceProvider> _providerFactory;
    private readonly Func<ProviderSettings, IAssistanceRequestAuthorizer> _authorizerFactory;
    private readonly Lazy<Func<CancellationToken, Task<PreviousCommandResult>>> _contextualRetriever;

    public OnDemandAssistanceRuntimeComposition(
        Func<CancellationToken, Task<ProviderSettings?>> settingsLoader,
        Func<ProviderSettings, IAssistanceProvider> providerFactory,
        Func<ProviderSettings, IAssistanceRequestAuthorizer> authorizerFactory,
        Func<Func<CancellationToken, Task<PreviousCommandResult>>> contextualRetrieverFactory)
    {
        _settingsLoader = settingsLoader ?? throw new ArgumentNullException(nameof(settingsLoader));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _authorizerFactory = authorizerFactory ?? throw new ArgumentNullException(nameof(authorizerFactory));
        ArgumentNullException.ThrowIfNull(contextualRetrieverFactory);
        _contextualRetriever = new Lazy<Func<CancellationToken, Task<PreviousCommandResult>>>(
            contextualRetrieverFactory,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static OnDemandAssistanceRuntimeComposition CreateProduction()
    {
        ProviderSettingsStore settingsStore = new();
        HttpClient httpClient = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        });
        SecretDetector secretDetector = new();
        AssistancePrivacyGate privacyGate = new(secretDetector);
        ProviderConsentGrantStore grantStore = new();

        return new OnDemandAssistanceRuntimeComposition(
            settingsStore.LoadAsync,
            settings => new ChatCompletionAssistanceProvider(
                settings,
                httpClient,
                Environment.GetEnvironmentVariable),
            settings => new AssistanceConsentPrompt(
                settings,
                grantStore,
                privacyGate,
                Console.In,
                Console.Out),
            () =>
            {
                CapturePreferenceStore preferenceStore = new();
                string captureRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TerminalTranslator",
                    "Capture");
                ProductionPreviousCommandRetriever retriever = new(
                    preferenceStore,
                    captureRoot,
                    Environment.GetEnvironmentVariable,
                    () =>
                    {
                        if (!OperatingSystem.IsWindows())
                            return (false, null);
                        bool success = CaptureSessionIdentity.TryGetDirectParentOwner(out CaptureOwnerIdentity? owner);
                        return (success, owner);
                    });
                return retriever.RetrieveAsync;
            });
    }

    public async Task<LastAssistanceOutcome> ExecuteLastAsync(CancellationToken cancellationToken)
    {
        DeferredRuntimeLeaves leaves = new(CreateLeavesAsync);
        LastAssistanceCoordinator coordinator = new(
            _contextualRetriever.Value,
            new DeferredProvider(leaves),
            new DeferredAuthorizer(leaves),
            adapterCapabilityBytes: ChatCompletionAssistanceProvider.DefaultVariableInputBytes);
        return await coordinator.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AskAssistanceOutcome> ExecuteQuestionAsync(
        string question,
        CancellationToken cancellationToken)
    {
        DeferredRuntimeLeaves leaves = new(CreateLeavesAsync);
        AskAssistanceCoordinator coordinator = new(
            new DeferredProvider(leaves),
            new DeferredAuthorizer(leaves),
            adapterCapabilityBytes: ChatCompletionAssistanceProvider.DefaultVariableInputBytes);
        return await coordinator.ExecuteAsync(question, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AskAssistanceOutcome> ExecuteLastQuestionAsync(
        string question,
        CancellationToken cancellationToken)
    {
        DeferredRuntimeLeaves leaves = new(CreateLeavesAsync);
        AskLastAssistanceCoordinator coordinator = new(
            _contextualRetriever.Value,
            new DeferredProvider(leaves),
            new DeferredAuthorizer(leaves),
            adapterCapabilityBytes: ChatCompletionAssistanceProvider.DefaultVariableInputBytes);
        return await coordinator.ExecuteAsync(question, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RuntimeLeaves?> CreateLeavesAsync(CancellationToken cancellationToken)
    {
        try
        {
            ProviderSettings? settings = await _settingsLoader(cancellationToken).ConfigureAwait(false);
            return settings is null
                ? null
                : new RuntimeLeaves(_providerFactory(settings), _authorizerFactory(settings));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record RuntimeLeaves(IAssistanceProvider Provider, IAssistanceRequestAuthorizer Authorizer);

    private sealed class DeferredRuntimeLeaves(
        Func<CancellationToken, Task<RuntimeLeaves?>> factory)
    {
        private readonly object _sync = new();
        private Task<RuntimeLeaves?>? _task;

        public Task<RuntimeLeaves?> GetAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                return _task ??= factory(cancellationToken);
            }
        }
    }

    private sealed class DeferredAuthorizer(DeferredRuntimeLeaves leaves) : IAssistanceRequestAuthorizer
    {
        public async Task<AssistanceAuthorizationDecision> AuthorizeAsync(
            AssistanceRequest request,
            CancellationToken cancellationToken)
        {
            RuntimeLeaves? resolved = await leaves.GetAsync(cancellationToken).ConfigureAwait(false);
            if (resolved is null)
                throw new TerminalTranslator.Core.Translation.TranslationProviderException(
                    TerminalTranslator.Core.Translation.TranslationErrorCode.Authentication);
            return await resolved.Authorizer.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class DeferredProvider(DeferredRuntimeLeaves leaves) : IAssistanceProvider
    {
        public async Task<AssistanceResult> CompleteAsync(
            AuthorizedAssistanceRequest request,
            CancellationToken cancellationToken)
        {
            RuntimeLeaves? resolved = await leaves.GetAsync(cancellationToken).ConfigureAwait(false);
            if (resolved is null)
                throw new TerminalTranslator.Core.Translation.TranslationProviderException(
                    TerminalTranslator.Core.Translation.TranslationErrorCode.Authentication);
            return await resolved.Provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
