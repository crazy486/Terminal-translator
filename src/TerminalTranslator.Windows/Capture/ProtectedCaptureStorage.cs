using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TerminalTranslator.Windows.Capture;

[SupportedOSPlatform("windows")]
public sealed class ProtectedCaptureStorage
{
    public ProtectedCaptureStorage(string? root = null) => Root = root ?? DefaultRoot;

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TerminalTranslator",
        "Capture");

    public string Root { get; }

    public string CreateSessionDirectory()
        => CreateSessionDirectory(Guid.NewGuid());

    public string CreateSessionDirectory(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session identity cannot be empty.", nameof(sessionId));
        }

        Directory.CreateDirectory(Root);
        ApplyProtectedAcl(Root);
        string path = Path.Combine(Root, sessionId.ToString("N"));
        Directory.CreateDirectory(path);
        ApplyProtectedAcl(path);
        return path;
    }

    public void ApplyProtectedAcl(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        FileSystemRights rights = FileSystemRights.FullControl;
        InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(currentUser, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(currentUser);
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
