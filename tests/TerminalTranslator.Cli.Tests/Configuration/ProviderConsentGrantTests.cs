using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Core.Assistance;

namespace TerminalTranslator.Cli.Tests.Configuration;

[TestClass]
public sealed class ProviderConsentGrantTests
{
    [TestMethod]
    public void Fingerprint_IsCanonicalAndContainsEveryTrustBoundaryFieldOnly()
    {
        ProviderSettings baseline = Settings("https://EXAMPLE.com", "model-a", "TT_KEY", 1);
        ProviderConsentContext expected = ProviderConsentContext.Create(baseline, AssistanceConsentScopes.QuestionOnly);

        Assert.AreEqual(expected.Fingerprint, ProviderConsentContext.Create(
            Settings("https://example.com/", "model-a", "TT_KEY", 9), AssistanceConsentScopes.QuestionOnly).Fingerprint,
            "Equivalent destination spellings and timeout changes are non-boundary changes.");

        (ProviderSettings Settings, string Scope, string Policy)[] changes =
        [
            (baseline with { Adapter = "other-adapter" }, AssistanceConsentScopes.QuestionOnly, ProviderConsentContext.CurrentPolicyVersion),
            (Settings("https://other.example/", "model-a", "TT_KEY", 1), AssistanceConsentScopes.QuestionOnly, ProviderConsentContext.CurrentPolicyVersion),
            (Settings("https://example.com/other", "model-a", "TT_KEY", 1), AssistanceConsentScopes.QuestionOnly, ProviderConsentContext.CurrentPolicyVersion),
            (Settings("https://example.com/", "model-b", "TT_KEY", 1), AssistanceConsentScopes.QuestionOnly, ProviderConsentContext.CurrentPolicyVersion),
            (Settings("https://example.com/", "model-a", "OTHER_KEY", 1), AssistanceConsentScopes.QuestionOnly, ProviderConsentContext.CurrentPolicyVersion),
            (baseline, AssistanceConsentScopes.PreviousCommandContext, ProviderConsentContext.CurrentPolicyVersion),
            (baseline, AssistanceConsentScopes.QuestionOnly, "policy-v2"),
        ];

        foreach ((ProviderSettings settings, string scope, string policy) in changes)
            Assert.AreNotEqual(expected.Fingerprint, ProviderConsentContext.Create(settings, scope, policy).Fingerprint);
    }

    [TestMethod]
    public async Task Store_PersistsAtomicContentFreeGrantsAndReusesExactMatch()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tt-consent-{Guid.NewGuid():N}");
        try
        {
            ProviderConsentGrantStore store = new(directory, () => DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
            ProviderConsentContext context = ProviderConsentContext.Create(
                Settings("https://provider.example/v1/chat", "model", "TT_KEY", 1),
                AssistanceConsentScopes.QuestionOnly);
            await store.GrantAsync(context);

            ProviderConsentGrant? loaded = await new ProviderConsentGrantStore(directory).FindMatchingAsync(context);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(context.Fingerprint, loaded.Fingerprint);
            string persisted = await File.ReadAllTextAsync(store.SettingsPath);
            StringAssert.Contains(persisted, context.Fingerprint);
            Assert.IsFalse(persisted.Contains("question text", StringComparison.Ordinal));
            Assert.IsFalse(persisted.Contains("command output", StringComparison.Ordinal));
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly).Length);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static ProviderSettings Settings(string endpoint, string model, string credentialSource, int timeoutSeconds) =>
        ProviderSettings.Create(new Uri(endpoint), model, credentialSource, TimeSpan.FromSeconds(timeoutSeconds));
}
