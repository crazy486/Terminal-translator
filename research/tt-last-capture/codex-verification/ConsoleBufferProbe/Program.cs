using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class Program
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint ConsoleTextModeBuffer = 1;
    private const uint InvalidFileType = 0;
    private const uint FileTypeDisk = 1;
    private const uint FileTypeChar = 2;
    private const uint FileTypePipe = 3;
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return Usage("Missing command.");
            }

            string command = args[0].ToLowerInvariant();
            string outputPath = RequireOption(args, "--output");
            object result = command switch
            {
                "environment" => CaptureEnvironment(),
                "abi-test" => RunAbiTest(),
                "capture" => CaptureConsole(GetOption(args, "--session")),
                "set-height" => SetBufferHeight(ParseInt(RequireOption(args, "--height"), "--height")),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'.")
            };

            WriteJson(outputPath, result);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PROBE_ERROR={exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static object CaptureEnvironment()
    {
        nint stdin = GetStdHandle(StdInputHandle);
        nint stdout = GetStdHandle(StdOutputHandle);
        nint stderr = GetStdHandle(StdErrorHandle);
        bool stdoutInfoOk = GetConsoleScreenBufferInfo(stdout, out ConsoleScreenBufferInfo stdoutInfo);
        int stdoutInfoError = stdoutInfoOk ? 0 : Marshal.GetLastWin32Error();

        nint conout = OpenConout(GenericRead | GenericWrite);
        bool conoutOpened = IsValidHandle(conout);
        bool conoutInfoOk = false;
        int conoutInfoError = 0;
        ConsoleScreenBufferInfo conoutInfo = default;
        if (conoutOpened)
        {
            conoutInfoOk = GetConsoleScreenBufferInfo(conout, out conoutInfo);
            conoutInfoError = conoutInfoOk ? 0 : Marshal.GetLastWin32Error();
            CloseHandle(conout);
        }
        else
        {
            conoutInfoError = Marshal.GetLastWin32Error();
        }

        return new
        {
            Schema = "tt-capture-verification/environment/v1",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Os = new
            {
                Description = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                Framework = RuntimeInformation.FrameworkDescription,
                Environment.OSVersion.Version
            },
            Process = new
            {
                Id = Environment.ProcessId,
                Name = Environment.ProcessPath,
                Is64Bit = Environment.Is64BitProcess,
                Chain = GetProcessChain(Environment.ProcessId)
            },
            Terminal = new
            {
                WtSession = Environment.GetEnvironmentVariable("WT_SESSION"),
                TermProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM"),
                Term = Environment.GetEnvironmentVariable("TERM")
            },
            Handles = new
            {
                Stdin = DescribeHandle(stdin),
                Stdout = DescribeHandle(stdout),
                Stderr = DescribeHandle(stderr),
                StdoutConsoleInfo = new
                {
                    Success = stdoutInfoOk,
                    LastError = stdoutInfoError,
                    Info = stdoutInfoOk ? DescribeInfo(stdoutInfo) : null
                },
                Conout = new
                {
                    Opened = conoutOpened,
                    InfoSuccess = conoutInfoOk,
                    LastError = conoutInfoError,
                    Info = conoutInfoOk ? DescribeInfo(conoutInfo) : null
                }
            }
        };
    }

    private static object RunAbiTest()
    {
        nint conout = OpenConout(GenericRead | GenericWrite);
        if (!IsValidHandle(conout))
        {
            throw Win32("CreateFileW(CONOUT$)");
        }

        CloseHandle(conout);
        nint buffer = CreateConsoleScreenBuffer(
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            0,
            ConsoleTextModeBuffer,
            0);
        if (!IsValidHandle(buffer))
        {
            throw Win32("CreateConsoleScreenBuffer");
        }

        try
        {
            if (!GetConsoleScreenBufferInfo(buffer, out ConsoleScreenBufferInfo initialInfo))
            {
                throw Win32("GetConsoleScreenBufferInfo(initial ABI buffer)");
            }

            Coord requestedSize = new(
                checked((short)Math.Max(80, (int)initialInfo.Size.X)),
                checked((short)Math.Max(25, (int)initialInfo.Size.Y)));
            bool sizeSetAttempted = requestedSize.X != initialInfo.Size.X || requestedSize.Y != initialInfo.Size.Y;
            bool sizeSet = !sizeSetAttempted || SetConsoleScreenBufferSize(buffer, requestedSize);
            int sizeError = sizeSet ? 0 : Marshal.GetLastWin32Error();
            if (!GetConsoleScreenBufferInfo(buffer, out ConsoleScreenBufferInfo info))
            {
                throw Win32("GetConsoleScreenBufferInfo(ABI buffer)");
            }

            int[] requestedRows = [0, 1, 2, 20];
            Dictionary<int, string> expected = [];
            foreach (int row in requestedRows)
            {
                string marker = $"ROW{row:000}_UNIQUE";
                expected[row] = marker;
                if (!WriteConsoleOutputCharacterW(buffer, marker, (uint)marker.Length, new Coord(0, checked((short)row)), out uint written)
                    || written != marker.Length)
                {
                    throw Win32($"WriteConsoleOutputCharacterW(row={row})");
                }
            }

            List<object> comparisons = [];
            foreach (int row in requestedRows)
            {
                string correct = ReadCorrect(buffer, 20, new Coord(0, checked((short)row)));
                string legacy = ReadLegacy(buffer, 20, checked((uint)row));
                comparisons.Add(new
                {
                    Requested = new { X = 0, Y = row },
                    Correct = new { Text = correct, MatchesExpected = correct.StartsWith(expected[row], StringComparison.Ordinal) },
                    LegacyArgument = new
                    {
                        UInt32 = row,
                        DecodedX = unchecked((short)(row & 0xffff)),
                        DecodedY = unchecked((short)((row >> 16) & 0xffff))
                    },
                    Legacy = new { Text = legacy, MatchesExpected = legacy.StartsWith(expected[row], StringComparison.Ordinal) }
                });
            }

            return new
            {
                Schema = "tt-capture-verification/abi-test/v1",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                CoordLayout = new
                {
                    Size = Marshal.SizeOf<Coord>(),
                    XOffset = Marshal.OffsetOf<Coord>(nameof(Coord.X)).ToInt32(),
                    YOffset = Marshal.OffsetOf<Coord>(nameof(Coord.Y)).ToInt32()
                },
                CorrectSignature = "ReadConsoleOutputCharacterW(nint, char[], uint, COORD, out uint)",
                LegacySignature = "ReadConsoleOutputCharacterW(nint, char[], uint, uint, out uint)",
                Buffer = new
                {
                    Initial = DescribeInfo(initialInfo),
                    Requested = new { Width = requestedSize.X, Height = requestedSize.Y },
                    SetAttempted = sizeSetAttempted,
                    SetSucceeded = sizeSet,
                    SetLastError = sizeError,
                    Actual = DescribeInfo(info)
                },
                Comparisons = comparisons
            };
        }
        finally
        {
            CloseHandle(buffer);
        }
    }

    private static object CaptureConsole(string? sessionId)
    {
        nint conout = OpenConout(GenericRead | GenericWrite);
        if (!IsValidHandle(conout))
        {
            throw Win32("CreateFileW(CONOUT$)");
        }

        try
        {
            if (!GetConsoleScreenBufferInfo(conout, out ConsoleScreenBufferInfo info))
            {
                throw Win32("GetConsoleScreenBufferInfo(CONOUT$)");
            }

            int width = info.Size.X;
            int height = info.Size.Y;
            List<string> rows = new(height);
            for (int y = 0; y < height; y++)
            {
                rows.Add(ReadCorrect(conout, width, new Coord(0, checked((short)y))).TrimEnd('\0', ' '));
            }

            object? previous = null;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                previous = FindPreviousSegment(rows, sessionId);
            }

            return new
            {
                Schema = "tt-capture-verification/console-capture/v1",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                SessionId = sessionId,
                Console = DescribeInfo(info),
                Rows = rows,
                PreviousCompletedBoundary = previous
            };
        }
        finally
        {
            CloseHandle(conout);
        }
    }

    private static object SetBufferHeight(int targetHeight)
    {
        if (targetHeight is < 1 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(targetHeight), "Height must be 1..32767.");
        }

        nint conout = OpenConout(GenericRead | GenericWrite);
        if (!IsValidHandle(conout))
        {
            throw Win32("CreateFileW(CONOUT$)");
        }

        try
        {
            if (!GetConsoleScreenBufferInfo(conout, out ConsoleScreenBufferInfo before))
            {
                throw Win32("GetConsoleScreenBufferInfo(before set-height)");
            }

            Coord requested = new(before.Size.X, checked((short)targetHeight));
            bool success = SetConsoleScreenBufferSize(conout, requested);
            int lastError = success ? 0 : Marshal.GetLastWin32Error();
            bool afterSuccess = GetConsoleScreenBufferInfo(conout, out ConsoleScreenBufferInfo after);
            int afterError = afterSuccess ? 0 : Marshal.GetLastWin32Error();
            return new
            {
                Schema = "tt-capture-verification/set-height/v1",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Before = DescribeInfo(before),
                Requested = new { Width = requested.X, Height = requested.Y },
                ApiSucceeded = success,
                ApiLastError = lastError,
                AfterInfoSucceeded = afterSuccess,
                AfterInfoLastError = afterError,
                After = afterSuccess ? DescribeInfo(after) : null
            };
        }
        finally
        {
            CloseHandle(conout);
        }
    }

    private static object FindPreviousSegment(IReadOnlyList<string> rows, string sessionId)
    {
        Regex marker = new($"TT_BOUNDARY:{Regex.Escape(sessionId)}:(?<ordinal>[0-9]+)", RegexOptions.CultureInvariant);
        List<object> boundaries = [];
        List<(int Row, int Ordinal)> positions = [];
        for (int row = 0; row < rows.Count; row++)
        {
            Match match = marker.Match(rows[row]);
            if (!match.Success)
            {
                continue;
            }

            int ordinal = int.Parse(match.Groups["ordinal"].Value, System.Globalization.CultureInfo.InvariantCulture);
            positions.Add((row, ordinal));
            boundaries.Add(new { Row = row, Ordinal = ordinal, Text = match.Value });
        }

        if (positions.Count < 2)
        {
            return new
            {
                Found = false,
                Reason = "Fewer than two completed prompt sentinels were retained.",
                Boundaries = boundaries
            };
        }

        (int startRow, int startOrdinal) = positions[^2];
        (int endRow, int endOrdinal) = positions[^1];
        string[] segmentRows = rows.Skip(startRow + 1).Take(Math.Max(0, endRow - startRow - 1)).ToArray();
        return new
        {
            Found = true,
            Start = new { Row = startRow, Ordinal = startOrdinal },
            End = new { Row = endRow, Ordinal = endOrdinal },
            Rows = segmentRows,
            Text = string.Join(Environment.NewLine, segmentRows)
        };
    }

    private static object DescribeHandle(nint handle)
    {
        uint type = GetFileType(handle);
        int lastError = type == InvalidFileType ? Marshal.GetLastWin32Error() : 0;
        return new
        {
            Value = $"0x{handle.ToInt64():x}",
            TypeValue = type,
            Type = type switch
            {
                FileTypeDisk => "DISK",
                FileTypeChar => "CHAR",
                FileTypePipe => "PIPE",
                InvalidFileType => "UNKNOWN",
                _ => $"OTHER({type})"
            },
            LastError = lastError
        };
    }

    private static object DescribeInfo(ConsoleScreenBufferInfo info) => new
    {
        Buffer = new { Width = info.Size.X, Height = info.Size.Y },
        Cursor = new { X = info.CursorPosition.X, Y = info.CursorPosition.Y },
        Window = new
        {
            Left = info.Window.Left,
            Top = info.Window.Top,
            Right = info.Window.Right,
            Bottom = info.Window.Bottom,
            Width = info.Window.Right - info.Window.Left + 1,
            Height = info.Window.Bottom - info.Window.Top + 1
        },
        MaximumWindow = new { Width = info.MaximumWindowSize.X, Height = info.MaximumWindowSize.Y },
        Attributes = info.Attributes
    };

    private static List<object> GetProcessChain(int startProcessId)
    {
        Dictionary<int, (int ParentId, string Name)> processes = SnapshotProcesses();
        List<object> chain = [];
        HashSet<int> visited = [];
        int current = startProcessId;
        for (int depth = 0; depth < 16 && current != 0 && visited.Add(current); depth++)
        {
            if (!processes.TryGetValue(current, out (int ParentId, string Name) entry))
            {
                chain.Add(new { ProcessId = current, ParentProcessId = 0, Name = "<unavailable>" });
                break;
            }

            chain.Add(new { ProcessId = current, ParentProcessId = entry.ParentId, entry.Name });
            current = entry.ParentId;
        }

        return chain;
    }

    private static Dictionary<int, (int ParentId, string Name)> SnapshotProcesses()
    {
        Dictionary<int, (int ParentId, string Name)> result = [];
        nint snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (!IsValidHandle(snapshot))
        {
            return result;
        }

        try
        {
            ProcessEntry32 entry = new() { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result[checked((int)entry.ProcessId)] = (checked((int)entry.ParentProcessId), entry.ExecutableFile ?? string.Empty);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    private static string ReadCorrect(nint handle, int length, Coord coordinate)
    {
        char[] buffer = new char[length];
        if (!ReadConsoleOutputCharacterW(handle, buffer, checked((uint)length), coordinate, out uint read))
        {
            throw Win32($"ReadConsoleOutputCharacterW(correct X={coordinate.X},Y={coordinate.Y})");
        }

        return new string(buffer, 0, checked((int)read));
    }

    private static string ReadLegacy(nint handle, int length, uint packedCoordinate)
    {
        char[] buffer = new char[length];
        if (!ReadConsoleOutputCharacterWLegacy(handle, buffer, checked((uint)length), packedCoordinate, out uint read))
        {
            throw Win32($"ReadConsoleOutputCharacterW(legacy uint={packedCoordinate})");
        }

        return new string(buffer, 0, checked((int)read));
    }

    private static nint OpenConout(uint access) => CreateFileW(
        "CONOUT$",
        access,
        FileShareRead | FileShareWrite,
        0,
        OpenExisting,
        0,
        0);

    private static bool IsValidHandle(nint handle) => handle != 0 && handle != InvalidHandleValue;

    private static Exception Win32(string operation) =>
        new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), operation);

    private static int ParseInt(string value, string option) =>
        int.TryParse(value, out int parsed) ? parsed : throw new ArgumentException($"{option} must be an integer.");

    private static string RequireOption(string[] args, string name) =>
        GetOption(args, name) ?? throw new ArgumentException($"Missing required option {name}.");

    private static string? GetOption(string[] args, string name)
    {
        for (int index = 1; index < args.Length; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {name}.");
                }

                return args[index + 1];
            }
        }

        return null;
    }

    private static void WriteJson(string path, object value)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    }

    private static int Usage(string error)
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine("Usage: ConsoleBufferProbe <environment|abi-test|capture|set-height> --output <path> [--session <id>] [--height <n>]");
        return 2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public Coord Size;
        public Coord CursorPosition;
        public ushort Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(nint handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfo(nint consoleOutput, out ConsoleScreenBufferInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleScreenBufferSize(nint consoleOutput, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateConsoleScreenBuffer(
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint flags,
        nint screenBufferData);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleOutputCharacterW(
        nint consoleOutput,
        string character,
        uint length,
        Coord writeCoordinate,
        out uint charactersWritten);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleOutputCharacterW(
        nint consoleOutput,
        [Out] char[] character,
        uint length,
        Coord readCoordinate,
        out uint charactersRead);

    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleOutputCharacterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleOutputCharacterWLegacy(
        nint consoleOutput,
        [Out] char[] character,
        uint length,
        uint readCoordinate,
        out uint charactersRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);
}
