using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerminalTranslator.Cli.Configuration;

public sealed record ProviderConsentContext(
    string AdapterIdentity,
    string NormalizedDestination,
    string ModelIdentity,
    string CredentialSourceIdentity,
    string DisclosedScope,
    string PolicyVersion)
{
    public const string CurrentPolicyVersion = "assistance-external-transmission-v1";

    public string Fingerprint => ComputeFingerprint(
        AdapterIdentity,
        NormalizedDestination,
        ModelIdentity,
        CredentialSourceIdentity,
        DisclosedScope,
        PolicyVersion);

    public static ProviderConsentContext Create(
        ProviderSettings settings,
        string disclosedScope,
        string policyVersion = CurrentPolicyVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(disclosedScope)) throw new ArgumentException("Scope is required.", nameof(disclosedScope));
        if (string.IsNullOrWhiteSpace(policyVersion)) throw new ArgumentException("Policy version is required.", nameof(policyVersion));

        return new ProviderConsentContext(
            settings.Adapter,
            NormalizeDestination(settings.Endpoint),
            settings.Model,
            settings.ApiKeyEnvironmentVariable,
            disclosedScope,
            policyVersion);
    }

    public static string NormalizeDestination(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri) throw new ArgumentException("Destination must be absolute.", nameof(endpoint));
        return endpoint.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
    }

    private static string ComputeFingerprint(params string[] fields)
    {
        StringBuilder canonical = new();
        foreach (string field in fields)
            canonical.Append(Encoding.UTF8.GetByteCount(field)).Append(':').Append(field).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}

public sealed record ProviderConsentGrant(string Fingerprint, string Scope, DateTimeOffset GrantedAtUtc);

public sealed class ProviderConsentGrantStore
{
    private const string FileName = "provider-consent-grants.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<DateTimeOffset> _utcNow;

    public ProviderConsentGrantStore(string? settingsDirectory = null, Func<DateTimeOffset>? utcNow = null)
    {
        SettingsDirectory = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerminalTranslator");
        SettingsPath = Path.Combine(SettingsDirectory, FileName);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string SettingsDirectory { get; }
    public string SettingsPath { get; }

    public async Task<ProviderConsentGrant?> FindMatchingAsync(
        ProviderConsentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<ProviderConsentGrant> grants = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return grants.FirstOrDefault(grant =>
            string.Equals(grant.Fingerprint, context.Fingerprint, StringComparison.Ordinal) &&
            string.Equals(grant.Scope, context.DisclosedScope, StringComparison.Ordinal));
    }

    public async Task<ProviderConsentGrant> GrantAsync(
        ProviderConsentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<ProviderConsentGrant> grants = [.. await LoadAsync(cancellationToken).ConfigureAwait(false)];
        ProviderConsentGrant grant = new(context.Fingerprint, context.DisclosedScope, _utcNow());
        grants.RemoveAll(item => string.Equals(item.Fingerprint, grant.Fingerprint, StringComparison.Ordinal));
        grants.Add(grant);
        await SaveAsync(grants, cancellationToken).ConfigureAwait(false);
        return grant;
    }

    private async Task<IReadOnlyList<ProviderConsentGrant>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(SettingsPath)) return [];
        await using FileStream stream = new(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        List<ProviderConsentGrant>? grants = await JsonSerializer.DeserializeAsync<List<ProviderConsentGrant>>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return grants ?? throw new InvalidDataException("Provider consent grants are empty or invalid.");
    }

    private async Task SaveAsync(IReadOnlyList<ProviderConsentGrant> grants, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(SettingsDirectory);
        string temporaryPath = Path.Combine(SettingsDirectory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, grants, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
