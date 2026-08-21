using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Commands;

public sealed class SessionControlHandler(
    TranslationSession session,
    ProductionTranslationPipeline pipeline,
    ProviderSettings settings,
    Func<StateEventMessage, CancellationToken, Task>? stateSink = null) : IControlPipeRequestHandler
{
    public string CurrentState => StateName(session.State);

    public async Task<ControlResultMessage> EnableAsync(
        string providerFingerprint,
        bool consent,
        CancellationToken cancellationToken)
    {
        bool fingerprintMatches = string.Equals(
            providerFingerprint,
            settings.Fingerprint,
            StringComparison.Ordinal);
        bool changed = false;
        if (fingerprintMatches && consent)
        {
            changed = pipeline.Enable(providerFingerprint, consent: true);
        }

        bool enabled = fingerprintMatches && consent &&
            session.IsAuthorized(session.Generation, settings.Fingerprint);
        if (enabled && stateSink is not null && (changed || session.State == SessionState.Enabled))
        {
            await stateSink(
                new StateEventMessage(
                    "state", SessionProtocol.Version, "enabled", settings.Endpoint.Host, settings.Model),
                cancellationToken).ConfigureAwait(false);
        }

        return new ControlResultMessage(
            "control-result",
            SessionProtocol.Version,
            "enable",
            enabled,
            StateName(session.State),
            session.Generation);
    }

    public async Task<ControlResultMessage> DisableAsync(CancellationToken cancellationToken)
    {
        pipeline.Disable();
        if (stateSink is not null)
        {
            await stateSink(
                new StateEventMessage(
                    "state", SessionProtocol.Version, "disabled", settings.Endpoint.Host, settings.Model),
                cancellationToken).ConfigureAwait(false);
        }

        return new ControlResultMessage(
            "control-result",
            SessionProtocol.Version,
            "disable",
            true,
            "disabled",
            session.Generation);
    }

    public Task<StatusResultMessage> StatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new StatusResultMessage(
            "status-result",
            SessionProtocol.Version,
            StateName(session.State),
            settings.Endpoint.Host,
            settings.Model,
            pipeline.HighQueued,
            pipeline.NormalQueued,
            session.PrivacySkipped,
            session.OverloadDropped));

    private static string StateName(SessionState state) => state switch
    {
        SessionState.Enabling => "enabling",
        SessionState.Enabled => "enabled",
        _ => "disabled",
    };
}
