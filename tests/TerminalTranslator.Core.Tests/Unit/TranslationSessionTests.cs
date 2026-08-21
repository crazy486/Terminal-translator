using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Tests.TestDoubles;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class TranslationSessionTests
{
    [TestMethod]
    public void StartEnableDisableReEnable_AdvancesGenerationAndBindsConsent()
    {
        FakeClock clock = new();
        Guid sessionId = Guid.NewGuid();
        using TranslationSession session = new(sessionId, clock);

        session.Start();
        Assert.AreEqual(SessionState.Disabled, session.State);
        Assert.AreEqual(0, session.Generation);

        Assert.IsTrue(session.Enable("provider-a", consent: true));
        Assert.AreEqual(SessionState.Enabled, session.State);
        Assert.AreEqual(1, session.Generation);
        Assert.AreEqual(sessionId, session.Consent!.SessionId);
        Assert.AreEqual(1, session.Consent.Generation);
        Assert.AreEqual("provider-a", session.Consent.ProviderFingerprint);
        Assert.IsTrue(session.IsAuthorized(1, "provider-a"));

        Assert.IsTrue(session.Disable());
        Assert.AreEqual(SessionState.Disabled, session.State);
        Assert.AreEqual(2, session.Generation);
        Assert.IsNull(session.Consent);
        Assert.IsFalse(session.IsAuthorized(1, "provider-a"));

        Assert.IsTrue(session.Enable("provider-a", consent: true));
        Assert.AreEqual(3, session.Generation);
        Assert.IsTrue(session.IsAuthorized(3, "provider-a"));
    }

    [TestMethod]
    public void EnableWithoutConsentOrFromInvalidState_IsRejected()
    {
        using TranslationSession session = new(Guid.NewGuid(), new FakeClock());

        Assert.ThrowsExactly<InvalidOperationException>(() => session.Enable("provider-a", consent: true));
        session.Start();
        Assert.IsFalse(session.Enable("provider-a", consent: false));
        Assert.AreEqual(SessionState.Disabled, session.State);
        Assert.ThrowsExactly<InvalidOperationException>(() => session.End());
    }

    [TestMethod]
    public void IdempotentEnableAndDisable_DoNotAdvanceGenerationTwice()
    {
        using TranslationSession session = new(Guid.NewGuid(), new FakeClock());
        session.Start();

        Assert.IsTrue(session.Enable("provider-a", consent: true));
        Assert.IsFalse(session.Enable("provider-a", consent: true));
        Assert.AreEqual(1, session.Generation);
        Assert.IsTrue(session.Disable());
        Assert.IsFalse(session.Disable());
        Assert.AreEqual(2, session.Generation);
    }

    [TestMethod]
    public void Disable_CancelsGenerationAndClearsOwnedContent()
    {
        using TranslationSession session = new(Guid.NewGuid(), new FakeClock());
        session.Start();
        session.Enable("provider-a", consent: true);
        CancellationToken generationToken = session.TranslationCancellationToken;
        session.TrackSegment(CreateSegment(session.SessionId, session.Generation, 1));
        session.TrackTranslation(CreateTranslation(session.SessionId, session.Generation, 1));

        session.Disable();

        Assert.IsTrue(generationToken.IsCancellationRequested);
        Assert.AreEqual(0, session.RetainedSegmentCount);
        Assert.AreEqual(0, session.RetainedTranslationCount);
    }

    [TestMethod]
    public void LateResult_FromOldGenerationCannotBeRetained()
    {
        using TranslationSession session = new(Guid.NewGuid(), new FakeClock());
        session.Start();
        session.Enable("provider-a", consent: true);
        long oldGeneration = session.Generation;
        session.Disable();

        Assert.IsFalse(session.TryTrackTranslation(CreateTranslation(session.SessionId, oldGeneration, 9)));
        Assert.AreEqual(0, session.RetainedTranslationCount);
    }

    private static OutputSegment CreateSegment(Guid sessionId, long generation, ulong sequence) => new(
        sessionId,
        generation,
        sequence,
        "Useful English output.",
        new LayoutHints(1, [0]),
        SourceBoundary.Line,
        TranslationPriority.Normal,
        null,
        TimeSpan.Zero);

    private static TranslationItem CreateTranslation(Guid sessionId, long generation, ulong sequence) => new(
        sessionId,
        generation,
        sequence,
        "Useful English output.",
        "有用的中文翻译。",
        new LayoutHints(1, [0]),
        null,
        TimeSpan.Zero);
}
