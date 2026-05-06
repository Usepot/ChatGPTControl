using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Forms;
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
            return string.IsNullOrWhiteSpace(command)
                ? Empty
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
