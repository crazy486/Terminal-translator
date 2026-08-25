using TerminalTranslator.Windows.Capture;
using System.Runtime.Versioning;

namespace TerminalTranslator.Windows.Tests.Unit;

[TestClass]
public sealed class CaptureSessionIdentityTests
{
    private static readonly CaptureOwnerIdentity Owner = new("S-1-5-21-1000", 4242, 638900000000000000);

    [TestMethod]
    public void Create_BindsIndependentGuidNonceOwnerAndVersion()
    {
        CaptureSessionProof first = CaptureSessionIdentity.Create(Owner, "2.0");
        CaptureSessionProof second = CaptureSessionIdentity.Create(Owner, "2.0");

        Assert.AreNotEqual(first.SessionId, second.SessionId);
        Assert.AreNotEqual(first.Nonce, second.Nonce);
        Assert.AreEqual(64, first.Nonce.Length);
        Assert.AreEqual(Owner, first.Owner);
        Assert.AreEqual("2.0", first.IntegrationVersion);
    }

    [TestMethod]
    public void Validate_RequiresEveryOwnerFactAndNonce()
    {
        CaptureSessionProof proof = CaptureSessionIdentity.Create(Owner, "2.0");
        Assert.IsTrue(CaptureSessionIdentity.Validate(proof, proof.SessionId, proof.Nonce, Owner, "2.0"));
        Assert.IsFalse(CaptureSessionIdentity.Validate(proof, proof.SessionId, "wrong", Owner, "2.0"));
        Assert.IsFalse(CaptureSessionIdentity.Validate(proof, proof.SessionId, proof.Nonce, Owner with { ProcessId = 999 }, "2.0"));
        Assert.IsFalse(CaptureSessionIdentity.Validate(proof, proof.SessionId, proof.Nonce, Owner with { ProcessStartUtcTicks = 1 }, "2.0"));
        Assert.IsFalse(CaptureSessionIdentity.Validate(proof, proof.SessionId, proof.Nonce, Owner with { UserSid = "S-1-5-21-2000" }, "2.0"));
        Assert.IsFalse(CaptureSessionIdentity.Validate(proof, proof.SessionId, proof.Nonce, Owner, "3.0"));
    }

    [TestMethod]
    public void EnvironmentRoundTrip_ContainsOpaqueIdentityOnly()
    {
        CaptureSessionProof proof = CaptureSessionIdentity.Create(Owner, "2.0");
        IReadOnlyDictionary<string, string> values = CaptureSessionIdentity.ToEnvironment(proof);

        Assert.AreEqual(proof.SessionId.ToString("N"), values[CaptureSessionIdentity.SessionEnvironmentVariable]);
        Assert.AreEqual(proof.Nonce, values[CaptureSessionIdentity.NonceEnvironmentVariable]);
        Assert.AreEqual(2, values.Count);
        Assert.IsTrue(CaptureSessionIdentity.TryReadEnvironment(values, out Guid sessionId, out string? nonce));
        Assert.AreEqual(proof.SessionId, sessionId);
        Assert.AreEqual(proof.Nonce, nonce);
    }

    [TestMethod]
    public void Association_RejectsWrongUnknownDuplicateNestedAndStaleOwners()
    {
        CaptureSessionProof proof = CaptureSessionIdentity.Create(Owner, "2.0");
        Assert.IsFalse(CaptureSessionIdentity.ValidateAssociation(proof, Guid.NewGuid(), proof.Nonce, Owner, "2.0", true, false, 1));
        Assert.IsFalse(CaptureSessionIdentity.ValidateAssociation(proof, proof.SessionId, "wrong", Owner, "2.0", true, false, 1));
        Assert.IsFalse(CaptureSessionIdentity.ValidateAssociation(proof, proof.SessionId, proof.Nonce, Owner with { UserSid = "S-1-5-99" }, "2.0", true, false, 1));
        Assert.IsFalse(CaptureSessionIdentity.ValidateAssociation(proof, proof.SessionId, proof.Nonce, Owner with { ProcessId = 8 }, "2.0", true, false, 1));
        Assert.IsFalse(CaptureSessionIdentity.ValidateAssociation(proof, proof.SessionId, proof.Nonce, Owner with { ProcessStartUtcTicks = 8 }, "2.0", true, false, 1));
        Assert.IsFalse(CaptureSessionIdentity.ValidateAssociation(proof, proof.SessionId, proof.Nonce, Owner, "2.0", false, false, 1));
        Assert.IsFalse(CaptureSessionIdentity.ValidateAssociation(proof, proof.SessionId, proof.Nonce, Owner, "2.0", true, true, 1));
        Assert.IsFalse(CaptureSessionIdentity.ValidateAssociation(proof, proof.SessionId, proof.Nonce, Owner, "2.0", true, false, 2));
        Assert.IsTrue(CaptureSessionIdentity.ValidateAssociation(proof, proof.SessionId, proof.Nonce, Owner, "2.0", true, false, 1));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void DirectParentValidation_UsesObservedParentPidAndStartToken()
    {
        Assert.IsTrue(CaptureSessionIdentity.TryGetDirectParentOwner(out CaptureOwnerIdentity? parent));
        CaptureSessionProof proof = CaptureSessionIdentity.Create(parent!, "2.0");
        Assert.IsTrue(CaptureSessionIdentity.ValidateDirectParent(proof, proof.SessionId, proof.Nonce, "2.0"));
        Assert.IsFalse(CaptureSessionIdentity.ValidateDirectParent(proof, proof.SessionId, proof.Nonce, "other"));
    }
}
