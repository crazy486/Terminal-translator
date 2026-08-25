using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace TerminalTranslator.Windows.PowerShell;

public enum ProfileEditPhase
{
    TemporaryWritten,
    BeforeCommit,
    Committed,
}

public sealed class PowerShellProfileInstaller(Action<ProfileEditPhase>? observer = null)
{
    public const string BlockBegin = "# >>> Terminal Translator Capture >>>";
    public const string BlockEnd = "# <<< Terminal Translator Capture <<<";

    public Task InstallAsync(string profilePath, string loaderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loaderPath);
        return EditAsync(profilePath, text => AddOrReplaceBlock(text, loaderPath), cancellationToken);
    }

    public Task RemoveAsync(string profilePath, CancellationToken cancellationToken = default) =>
        EditAsync(profilePath, RemoveBlock, cancellationToken);

    private async Task EditAsync(
        string profilePath,
        Func<string, string> transform,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        string directory = Path.GetDirectoryName(Path.GetFullPath(profilePath)) ??
            throw new ArgumentException("Profile path must have a parent directory.", nameof(profilePath));
        Directory.CreateDirectory(directory);

        byte[] original = File.Exists(profilePath)
            ? await File.ReadAllBytesAsync(profilePath, cancellationToken).ConfigureAwait(false)
            : [];
        TextEncoding encoding = TextEncoding.Detect(original);
        string current = encoding.Decode(original);
        string updated = transform(current);
        if (string.Equals(current, updated, StringComparison.Ordinal))
        {
            return;
        }

        byte[] replacement = encoding.Encode(updated);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(profilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(replacement, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            ProtectTemporaryFile(temporaryPath);
            observer?.Invoke(ProfileEditPhase.TemporaryWritten);
            observer?.Invoke(ProfileEditPhase.BeforeCommit);

            byte[] observed = File.Exists(profilePath)
                ? await File.ReadAllBytesAsync(profilePath, cancellationToken).ConfigureAwait(false)
                : [];
            if (!observed.AsSpan().SequenceEqual(original))
            {
                throw new IOException("PowerShell profile changed during Terminal Translator integration update.");
            }

            File.Move(temporaryPath, profilePath, overwrite: true);
            observer?.Invoke(ProfileEditPhase.Committed);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string AddOrReplaceBlock(string profile, string loaderPath)
    {
        string withoutBlock = RemoveBlock(profile);
        string newline = DetectNewline(profile);
        string escapedPath = loaderPath.Replace("'", "''", StringComparison.Ordinal);
        return $"{withoutBlock}{newline}{BlockBegin}{newline}. '{escapedPath}'{newline}{BlockEnd}{newline}";
    }

    private static string RemoveBlock(string profile)
    {
        int begin = profile.IndexOf(BlockBegin, StringComparison.Ordinal);
        if (begin < 0)
        {
            return profile;
        }

        int endMarker = profile.IndexOf(BlockEnd, begin, StringComparison.Ordinal);
        if (endMarker < 0)
        {
            throw new InvalidDataException("Terminal Translator profile block is incomplete.");
        }

        int end = endMarker + BlockEnd.Length;
        if (end < profile.Length && profile[end] == '\r')
        {
            end++;
        }

        if (end < profile.Length && profile[end] == '\n')
        {
            end++;
        }

        if (begin > 0 && profile[begin - 1] == '\n')
        {
            begin--;
            if (begin > 0 && profile[begin - 1] == '\r')
            {
                begin--;
            }
        }

        return string.Concat(profile.AsSpan(0, begin), profile.AsSpan(end));
    }

    private static string DetectNewline(string profile) => profile.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static void ProtectTemporaryFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("Current Windows user SID is unavailable.");
        FileSecurity security = new();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(currentUser);
        security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private sealed record TextEncoding(Encoding Encoding, byte[] Preamble)
    {
        public static TextEncoding Detect(byte[] bytes)
        {
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            {
                return new TextEncoding(new UTF8Encoding(false, true), Encoding.UTF8.Preamble.ToArray());
            }

            if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
            {
                return new TextEncoding(new UnicodeEncoding(false, false, true), Encoding.Unicode.Preamble.ToArray());
            }

            return new TextEncoding(new UTF8Encoding(false, true), []);
        }

        public string Decode(byte[] bytes) => Encoding.GetString(bytes.AsSpan(Preamble.Length));

        public byte[] Encode(string text)
        {
            byte[] body = Encoding.GetBytes(text);
            byte[] result = new byte[Preamble.Length + body.Length];
            Preamble.CopyTo(result, 0);
            body.CopyTo(result, Preamble.Length);
            return result;
        }
    }
}
