using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
#if !PORTABLE_CORE
using System.Runtime.InteropServices;
#endif
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;

namespace PortableCodex.Native.Services;

public sealed partial class FileToolService
{
    private sealed record CommandSpec(string Display, IReadOnlyList<string>? Argv)
    {
        public static CommandSpec Empty { get; } = new(string.Empty, null);

        public bool IsEmpty => string.IsNullOrWhiteSpace(Display) && (Argv is null || Argv.Count == 0);

        public static CommandSpec FromShellLine(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return Empty;
            }

            return TryParseDirectPowerShellCommand(command, out var argv)
                ? FromArgv(argv)
                : new CommandSpec(command, null);
        }

        public static CommandSpec FromArgv(IReadOnlyList<string> argv)
        {
            return new CommandSpec(string.Join(" ", argv.Select(QuoteForDisplay)), argv);
        }

        private static string QuoteForDisplay(string value)
        {
            return value.Any(char.IsWhiteSpace)
                ? $"\"{value.Replace("\"", "\\\"")}" + "\""
                : value;
        }

        private static bool TryParseDirectPowerShellCommand(string command, out IReadOnlyList<string> argv)
        {
            argv = Array.Empty<string>();
#if PORTABLE_CORE
            return false;
#else
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            var parsed = SplitWindowsCommandLine(command);
            if (parsed.Count == 0)
            {
                return false;
            }

            var executable = Path.GetFileName(parsed[0]);
            if (!string.Equals(executable, "powershell", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(executable, "powershell.exe", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(executable, "pwsh", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(executable, "pwsh.exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            argv = parsed;
            return true;
#endif
        }

#if !PORTABLE_CORE
        private static IReadOnlyList<string> SplitWindowsCommandLine(string command)
        {
            var argvPtr = CommandLineToArgvW(command, out var argc);
            if (argvPtr == IntPtr.Zero)
            {
                return Array.Empty<string>();
            }

            try
            {
                var argv = new string[argc];
                for (var i = 0; i < argc; i++)
                {
                    var argPtr = Marshal.ReadIntPtr(argvPtr, i * IntPtr.Size);
                    argv[i] = Marshal.PtrToStringUni(argPtr) ?? string.Empty;
                }

                return argv;
            }
            finally
            {
                LocalFree(argvPtr);
            }
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(
            [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine,
            out int pNumArgs);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);
#endif
    }

    private sealed class ShellSession : IDisposable
    {
        private readonly StringBuilder _output = new();
        private int _readOffset;
        private int _chunkCounter;

        public ShellSession(int id, Process process, string command, string workingDirectory, bool keepStdinOpen)
        {
            Id = id;
            Process = process;
            Command = command;
            WorkingDirectory = workingDirectory;
            StdinOpen = keepStdinOpen;
        }

        public int Id { get; }

        public Process Process { get; }

        public string Command { get; }

        public string WorkingDirectory { get; }

        public bool StdinOpen { get; set; }

        public object InputLock { get; } = new();

        public void AppendOutput(string value)
        {
            lock (_output)
            {
                _output.Append(value);
            }
        }

        public string TakeUnreadOutput()
        {
            lock (_output)
            {
                if (_readOffset >= _output.Length)
                {
                    return string.Empty;
                }

                var value = _output.ToString(_readOffset, _output.Length - _readOffset);
                _readOffset = _output.Length;
                return value;
            }
        }

        public string NextChunkId()
        {
            return $"{Id}:{Interlocked.Increment(ref _chunkCounter)}";
        }

        public void CloseStdin()
        {
            lock (InputLock)
            {
                if (!StdinOpen)
                {
                    return;
                }

                try
                {
                    Process.StandardInput.Close();
                }
                catch
                {
                    // Best effort; stdin may already be closed by the child process.
                }

                StdinOpen = false;
            }
        }

        public void Dispose()
        {
            CloseStdin();
            Process.Dispose();
        }
    }
}
