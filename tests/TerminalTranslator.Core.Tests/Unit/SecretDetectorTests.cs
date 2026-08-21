using System.Text.Json;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Privacy;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class SecretDetectorTests
{
    [TestMethod]
    public async Task Screen_CorpusClassifiesSensitiveAndBenignInputs()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SecretCorpus.json");
        SecretFixture[] fixtures = JsonSerializer.Deserialize<SecretFixture[]>(
            await File.ReadAllTextAsync(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        SecretDetector detector = new();

        ulong sequence = 0;
        foreach (SecretFixture fixture in fixtures)
        {
            PrivacyDecision decision = detector.Screen(++sequence, fixture.Text);

            Assert.AreEqual(
                fixture.Skip ? PrivacyOutcome.Skip : PrivacyOutcome.Allow,
                decision.Outcome,
                fixture.Name);
            Assert.AreEqual(Enum.Parse<PrivacyReasonCode>(fixture.Reason), decision.ReasonCode, fixture.Name);
        }
    }

    [TestMethod]
    public void Screen_DetectorFailureFailsClosedWithoutReturningMatchedText()
    {
        const string secret = "never-return-this-secret";
        SecretDetector detector = new(_ => throw new InvalidOperationException(secret));

        PrivacyDecision decision = detector.Screen(42, secret);

        Assert.AreEqual(PrivacyOutcome.Skip, decision.Outcome);
        Assert.AreEqual(PrivacyReasonCode.DetectorFailure, decision.ReasonCode);
        Assert.AreEqual(42UL, decision.SegmentSequence);
        Assert.IsFalse(decision.ToString().Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Screen_PrivateKeyAcrossLinesSkipsWholeSegment()
    {
        SecretDetector detector = new();
        const string segment =
            "The command failed while loading credentials.\n" +
            "-----BEGIN RSA PRIVATE KEY-----\n" +
            "YWJjZGVmZ2hpamtsbW5vcA==\n" +
            "-----END RSA PRIVATE KEY-----\n" +
            "Retry after updating the configuration.";

        PrivacyDecision decision = detector.Screen(7, segment);

        Assert.AreEqual(PrivacyOutcome.Skip, decision.Outcome);
        Assert.AreEqual(PrivacyReasonCode.PrivateKey, decision.ReasonCode);
    }

    private sealed record SecretFixture(string Name, string Text, string Reason, bool Skip);
}
