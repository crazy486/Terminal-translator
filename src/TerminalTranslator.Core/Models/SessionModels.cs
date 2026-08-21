namespace TerminalTranslator.Core.Models;

public enum SessionState
{
    Created,
    Starting,
    Disabled,
    Enabling,
    Enabled,
    Stopping,
    Ended,
    Faulted,
}

public enum PaneRole { Program, Companion }

public enum ProcessRole { Host, Companion }

public sealed record PaneDescriptor(PaneRole Role, string Title, ProcessRole ProcessRole, bool Connected);

public enum ConsentScope { EligibleCurrentSessionSegments }

public sealed record ConsentGrant(
    Guid SessionId,
    long Generation,
    string ProviderFingerprint,
    ConsentScope Scope,
    DateTimeOffset GrantedAt);

public sealed record TranslationSessionDescriptor(
    Guid SessionId,
    long Generation,
    SessionState State,
    PaneDescriptor ProgramPane,
    PaneDescriptor CompanionPane,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt = null);
