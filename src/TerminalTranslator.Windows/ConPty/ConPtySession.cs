using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TerminalTranslator.Windows.ConPty;

public sealed class ConPtySession : IAsyncDisposable
{
    private static readonly object HostedEnvironmentGate = new();
    private readonly Process _process;
    private readonly SafePseudoConsoleHandle _pseudoConsole;
    private bool _disposed;

    private ConPtySession(
        Process process,
        SafePseudoConsoleHandle pseudoConsole,
        FileStream input,
        FileStream output)
    {
        _process = process;
        _pseudoConsole = pseudoConsole;
        Input = input;
        Output = output;
    }

    public Stream Input { get; }

    public Stream Output { get; }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public static ConPtySession StartPowerShell(string workingDirectory, Coord? size = null)
        => StartHostedPowerShell("-NoLogo", workingDirectory, size);

    public static ConPtySession StartPowerShellWithProfile(
        string workingDirectory,
        string profilePath,
        string? startupCommand = null,
        Coord? size = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        string fullProfilePath = Path.GetFullPath(profilePath);
        if (!File.Exists(fullProfilePath))
        {
            throw new FileNotFoundException("The controlled PowerShell profile does not exist.", fullProfilePath);
        }

        string escapedProfilePath = fullProfilePath.Replace("'", "''", StringComparison.Ordinal);
        string command = $". '{escapedProfilePath}'";
        if (!string.IsNullOrWhiteSpace(startupCommand))
        {
            command += $"; {startupCommand}";
        }

        string arguments = $"-NoLogo -NoExit -NoProfile -Command \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        return StartHostedPowerShell(arguments, workingDirectory, size);
    }

    private static ConPtySession StartHostedPowerShell(
        string arguments,
        string workingDirectory,
        Coord? size)
    {
        lock (HostedEnvironmentGate)
        {
            string? previous = Environment.GetEnvironmentVariable("TT_HOSTED_SESSION_ID");
            try
            {
                Environment.SetEnvironmentVariable(
                    "TT_HOSTED_SESSION_ID",
                    Environment.GetEnvironmentVariable("TT_SESSION_ID") ?? "feature-001-hosted");
                return Start("powershell.exe", arguments, workingDirectory, size);
            }
            finally
            {
                Environment.SetEnvironmentVariable("TT_HOSTED_SESSION_ID", previous);
            }
        }
    }

    public static ConPtySession Start(
        string executable,
        string arguments,
        string workingDirectory,
        Coord? size = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException(workingDirectory);
        }

        NativeMethods.SecurityAttributes attributes = new()
        {
            Length = Marshal.SizeOf<NativeMethods.SecurityAttributes>(),
            InheritHandle = 1,
        };

        if (!NativeMethods.CreatePipe(out SafeFileHandle pseudoInput, out SafeFileHandle hostInput, ref attributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create ConPTY input pipe.");
        }

        if (!NativeMethods.CreatePipe(out SafeFileHandle hostOutput, out SafeFileHandle pseudoOutput, ref attributes, 0))
        {
            pseudoInput.Dispose();
            hostInput.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create ConPTY output pipe.");
        }

        SafePseudoConsoleHandle? pseudoConsole = null;
        nint attributeList = 0;
        try
        {
            Coord initialSize = size ?? new Coord(120, 30);
            int result = NativeMethods.CreatePseudoConsole(
                initialSize,
                pseudoInput.DangerousGetHandle(),
                pseudoOutput.DangerousGetHandle(),
                0,
                out pseudoConsole);
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            nuint attributeListSize = 0;
            _ = NativeMethods.InitializeProcThreadAttributeList(0, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(checked((int)attributeListSize));
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to initialize ConPTY process attributes.");
            }

            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (nuint)NativeMethods.ProcThreadAttributePseudoConsole,
                    pseudoConsole.DangerousGetHandle(),
                    (nuint)nint.Size,
                    0,
                    0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to attach the PowerShell process to ConPTY.");
            }

            NativeMethods.StartupInfoEx startupInfo = new()
            {
                StartupInfo = new NativeMethods.StartupInfo
                {
                    Cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>(),
                    // Explicit null standard handles prevent CreateProcess from duplicating a
                    // redirected parent's handles instead of binding the child to ConPTY.
                    Flags = NativeMethods.StartfUseStdHandles,
                },
                AttributeList = attributeList,
            };
            string command = Quote(executable) + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments);
            char[] mutableCommand = (command + '\0').ToCharArray();
            if (!NativeMethods.CreateProcessW(
                    null,
                    mutableCommand,
                    0,
                    0,
                    false,
                    NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment,
                    0,
                    workingDirectory,
                    ref startupInfo,
                    out NativeMethods.ProcessInformation processInformation))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"Unable to start PowerShell in ConPTY (Win32 {error}).");
            }

            try
            {
                // The pseudoconsole-side pipe handles must remain open through CreateProcess.
                // Closing them earlier lets the ConPTY communication channels break while the
                // child is still attaching.
                pseudoInput.Dispose();
                pseudoOutput.Dispose();
                Process process = Process.GetProcessById(processInformation.ProcessId);
                _ = process.Handle;
                // Anonymous Win32 pipes are synchronous handles. FileStream still exposes async
                // APIs and schedules their operations without falsely marking them overlapped.
                FileStream input = new(hostInput, FileAccess.Write, 4096, isAsync: false);
                FileStream output = new(hostOutput, FileAccess.Read, 4096, isAsync: false);
                hostInput = null!;
                hostOutput = null!;
                ConPtySession session = new(process, pseudoConsole, input, output);
                pseudoConsole = null;
                return session;
            }
            finally
            {
                NativeMethods.CloseHandle(processInformation.Thread);
                NativeMethods.CloseHandle(processInformation.Process);
            }
        }
        finally
        {
            if (attributeList != 0)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            pseudoInput.Dispose();
            pseudoOutput.Dispose();
            hostInput?.Dispose();
            hostOutput?.Dispose();
            pseudoConsole?.Dispose();
        }
    }

    public void Resize(short columns, short rows)
    {
        if (columns <= 0 || rows <= 0)
        {
            return;
        }

        int result = NativeMethods.ResizePseudoConsole(_pseudoConsole, new Coord(columns, rows));
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public async ValueTask CompleteInputAsync()
    {
        await Input.DisposeAsync().ConfigureAwait(false);
    }

    public void ClosePseudoConsole() => _pseudoConsole.Dispose();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Input.DisposeAsync().ConfigureAwait(false);
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }

        _pseudoConsole.Dispose();
        await Output.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
