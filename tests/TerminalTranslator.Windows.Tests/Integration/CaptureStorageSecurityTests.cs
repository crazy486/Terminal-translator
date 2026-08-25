using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class CaptureStorageSecurityTests
{
    [TestMethod]
    public void DefaultRoot_UsesLocalApplicationDataNotDocumentsOrDesktop()
    {
        string root = ProtectedCaptureStorage.DefaultRoot;
        StringAssert.StartsWith(root, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(root.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(root.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CreateSessionDirectory_UsesRandomNameAndProtectedCurrentUserAcl()
    {
        using TemporaryDirectory temporary = new();
        ProtectedCaptureStorage storage = new(temporary.Path);
        string first = storage.CreateSessionDirectory();
        string second = storage.CreateSessionDirectory();

        Assert.AreNotEqual(first, second);
        DirectorySecurity security = new DirectoryInfo(first).GetAccessControl();
        Assert.IsTrue(security.AreAccessRulesProtected);
        SecurityIdentifier current = WindowsIdentity.GetCurrent().User!;
        AuthorizationRuleCollection rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
        Assert.IsTrue(rules.Cast<FileSystemAccessRule>().Any(rule =>
            current.Equals(rule.IdentityReference) &&
            rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)));
        Assert.IsFalse(rules.Cast<FileSystemAccessRule>().Any(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            (new SecurityIdentifier(WellKnownSidType.WorldSid, null).Equals(rule.IdentityReference) ||
             new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Equals(rule.IdentityReference))));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-capture-acl-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
