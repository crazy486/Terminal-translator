using System.Diagnostics;

namespace TerminalTranslator.Windows.Terminal;

public sealed class WindowsTerminalLauncher
{
    public static IReadOnlyList<string> BuildArguments(
        string executablePath,
        string sessionId,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string windowName = $"Terminal Translator {sessionId[..Math.Min(8, sessionId.Length)]}";
        return
        [
            "-w", windowName,
            "new-tab", "--title", "Program",
            executablePath, "__host", "--session", sessionId, "--working-directory", workingDirectory,
            ";",
            "split-pane", "--vertical", "--size", "0.35", "--title", "Translation",
            executablePath, "__companion", "--session", sessionId,
            ";",
            "focus-pane", "-t", "0",
        ];
    }

    public void Launch(
        string executablePath,
        string sessionId,
        string nonce,
        string workingDirectory)
    {
        ProcessStartInfo startInfo = CreateStartInfo(executablePath, sessionId, nonce, workingDirectory);
        Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Windows Terminal did not accept the launch request.");
        }
    }

    public static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string sessionId,
        string nonce,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        ProcessStartInfo startInfo = new("wt.exe")
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (string argument in BuildArguments(executablePath, sessionId, workingDirectory))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["TT_SESSION_ID"] = sessionId;
        startInfo.Environment["TT_SESSION_NONCE"] = nonce;
        string? executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            string inheritedPath = startInfo.Environment.TryGetValue("PATH", out string? path)
                ? path ?? string.Empty
                : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            startInfo.Environment["PATH"] = executableDirectory + Path.PathSeparator + inheritedPath;
        }

        return startInfo;
    }
}
