using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;

namespace TerminalTranslator.Windows.Capture;

public sealed record CaptureOwnerIdentity
{
    public CaptureOwnerIdentity(string userSid, int processId, long processStartUtcTicks)
    {
        if (string.IsNullOrWhiteSpace(userSid))
        {
            throw new ArgumentException("A user SID is required.", nameof(userSid));
        }

        if (processId <= 0 || processStartUtcTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "A live process identity is required.");
        }

        UserSid = userSid;
        ProcessId = processId;
        ProcessStartUtcTicks = processStartUtcTicks;
    }

    public string UserSid { get; init; }
    public int ProcessId { get; init; }
    public long ProcessStartUtcTicks { get; init; }
}

public sealed record CaptureSessionProof(
    Guid SessionId,
    string Nonce,
    CaptureOwnerIdentity Owner,
    string IntegrationVersion);

public static class CaptureSessionIdentity
{
    public const string SessionEnvironmentVariable = "TT_CAPTURE_SESSION_ID";
    public const string NonceEnvironmentVariable = "TT_CAPTURE_SESSION_NONCE";

    public static CaptureSessionProof Create(CaptureOwnerIdentity owner, string integrationVersion)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (string.IsNullOrWhiteSpace(integrationVersion))
        {
            throw new ArgumentException("Integration version is required.", nameof(integrationVersion));
        }

        return new CaptureSessionProof(
            Guid.NewGuid(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            owner,
            integrationVersion);
    }

    [SupportedOSPlatform("windows")]
    public static CaptureSessionProof CreateForCurrentProcess(string integrationVersion)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        using Process process = Process.GetCurrentProcess();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("Current Windows SID is unavailable.");
        return Create(new CaptureOwnerIdentity(sid, process.Id, process.StartTime.ToUniversalTime().Ticks), integrationVersion);
    }

    public static bool Validate(
        CaptureSessionProof proof,
        Guid suppliedSessionId,
        string suppliedNonce,
        CaptureOwnerIdentity observedDirectOwner,
        string integrationVersion)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(observedDirectOwner);
        if (proof.SessionId != suppliedSessionId || proof.Owner != observedDirectOwner ||
            !string.Equals(proof.IntegrationVersion, integrationVersion, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] expected = Encoding.UTF8.GetBytes(proof.Nonce);
        byte[] supplied = Encoding.UTF8.GetBytes(suppliedNonce ?? string.Empty);
        return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    public static bool ValidateAssociation(
        CaptureSessionProof proof,
        Guid suppliedSessionId,
        string suppliedNonce,
        CaptureOwnerIdentity observedDirectOwner,
        string integrationVersion,
        bool ownerIsLive,
        bool isNestedSession,
        int matchingManifestCount) =>
        ownerIsLive && !isNestedSession && matchingManifestCount == 1 &&
        Validate(proof, suppliedSessionId, suppliedNonce, observedDirectOwner, integrationVersion);

    [SupportedOSPlatform("windows")]
    public static bool ValidateDirectParent(
        CaptureSessionProof proof,
        Guid suppliedSessionId,
        string suppliedNonce,
        string integrationVersion)
    {
        return TryGetDirectParentOwner(out CaptureOwnerIdentity? directParent) && directParent is not null &&
            Validate(proof, suppliedSessionId, suppliedNonce, directParent, integrationVersion);
    }

    [SupportedOSPlatform("windows")]
    public static bool TryGetDirectParentOwner(out CaptureOwnerIdentity? owner)
    {
        owner = null;
        try
        {
            int status = NtQueryInformationProcess(
                Process.GetCurrentProcess().Handle,
                0,
                out ProcessBasicInformation information,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);
            if (status != 0 || information.InheritedFromUniqueProcessId == IntPtr.Zero)
            {
                return false;
            }

            int parentId = checked((int)information.InheritedFromUniqueProcessId.ToInt64());
            using Process parent = Process.GetProcessById(parentId);
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string sid = identity.User?.Value ?? string.Empty;
            if (sid.Length == 0)
            {
                return false;
            }

            owner = new CaptureOwnerIdentity(sid, parent.Id, parent.StartTime.ToUniversalTime().Ticks);
            return true;
        }
        catch
        {
            owner = null;
            return false;
        }
    }

    public static IReadOnlyDictionary<string, string> ToEnvironment(CaptureSessionProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SessionEnvironmentVariable] = proof.SessionId.ToString("N"),
            [NonceEnvironmentVariable] = proof.Nonce,
        };
    }

    public static bool TryReadEnvironment(
        IReadOnlyDictionary<string, string> environment,
        out Guid sessionId,
        out string? nonce)
    {
        ArgumentNullException.ThrowIfNull(environment);
        nonce = null;
        if (!environment.TryGetValue(SessionEnvironmentVariable, out string? sessionText) ||
            !environment.TryGetValue(NonceEnvironmentVariable, out nonce) ||
            !Guid.TryParseExact(sessionText, "N", out sessionId) ||
            string.IsNullOrWhiteSpace(nonce))
        {
            sessionId = Guid.Empty;
            nonce = null;
            return false;
        }

        return true;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ProcessBasicInformation
    {
        public readonly IntPtr Reserved1;
        public readonly IntPtr PebBaseAddress;
        public readonly IntPtr Reserved2_0;
        public readonly IntPtr Reserved2_1;
        public readonly IntPtr UniqueProcessId;
        public readonly IntPtr InheritedFromUniqueProcessId;
    }
}
