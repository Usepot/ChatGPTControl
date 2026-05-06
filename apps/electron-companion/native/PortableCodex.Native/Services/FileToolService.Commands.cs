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
    private async Task<ToolResponse> HandleRunCommandAsync(ToolRequest request, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var command = GetEffectiveCommandSpec(request);
        if (command.IsEmpty)
        {
            throw new InvalidOperationException($"command is required for {request.Tool}");
        }

        var workingDirectory = GetEffectiveWorkingDirectory(request);
        var absoluteWorkingDirectory = _pathPolicy.ResolvePathWithinWorkspace(request.WorkspaceRoot!, workingDirectory);
        if (!Directory.Exists(absoluteWorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {workingDirectory}");
        }

        var timeoutMs = (request.TimeoutMs ?? request.TimeoutMsSnake).GetValueOrDefault(DefaultCommandTimeoutMs);
        timeoutMs = Math.Clamp(timeoutMs, 1_000, MaxCommandTimeoutMs);
        var maxOutputBytes = GetEffectiveMaxOutputBytes(request);
        var stdin = request.Stdin ?? request.Input;

        var approved = await EnsureWriteApprovalAsync(
            request,
            $"Run command in {workingDirectory}: {command.Display}",
            context);
        if (!approved)
        {
            return Denied(request.RequestId, $"User denied {request.Tool} request");
        }

        if (string.Equals(request.Tool, "exec_command", StringComparison.Ordinal))
        {
            return await RunPersistentExecCommandAsync(
                request,
                command,
                workingDirectory,
                absoluteWorkingDirectory,
                timeoutMs,
                maxOutputBytes,
                stdin,
                cancellationToken);
        }

        return await RunCommandAsync(
            request.RequestId,
            command,
            workingDirectory,
            absoluteWorkingDirectory,
            timeoutMs,
            maxOutputBytes,
            stdin,
            cancellationToken);
    }

    private static async Task<ToolResponse> HandleWriteStdinAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        var sessionId = GetEffectiveSessionId(request);
        if (sessionId is null)
        {
            return Error(request.RequestId, "MISSING_SESSION_ID", "session_id is required for write_stdin");
        }

        if (!ShellSessions.TryGetValue(sessionId.Value, out var session))
        {
            return Error(request.RequestId, "UNKNOWN_PROCESS_ID", $"Unknown process id {sessionId.Value}");
        }

        var chars = request.Chars ?? request.Stdin ?? request.Input ?? string.Empty;
        if (chars.Length > 0)
        {
            if (!session.StdinOpen)
            {
                return Error(
                    request.RequestId,
                    "STDIN_CLOSED",
                    "stdin is closed for this session; rerun exec_command with tty=true to keep stdin open");
            }

            try
            {
                lock (session.InputLock)
                {
                    session.Process.StandardInput.Write(chars);
                    session.Process.StandardInput.Flush();
                }
            }
            catch
            {
                session.StdinOpen = false;
                return Error(request.RequestId, "WRITE_TO_STDIN", "failed to write to stdin");
            }
        }

        var wait = Stopwatch.StartNew();
        await WaitForProcessOrDelayAsync(session.Process, GetEffectiveYieldTimeMs(request), cancellationToken);
        wait.Stop();

        var result = BuildUnifiedExecResult(session, GetEffectiveMaxOutputBytes(request), wait.Elapsed);
        if (session.Process.HasExited)
        {
            ShellSessions.TryRemove(session.Id, out _);
            session.Dispose();
        }

        return Ok(request.RequestId, result);
    }

    private static string? GetEffectiveCommand(ToolRequest request)
    {
        return GetEffectiveCommandSpec(request).Display;
    }

    private static CommandSpec GetEffectiveCommandSpec(ToolRequest request)
    {
        if (request.Command is JsonArray commandArray)
        {
            var args = commandArray
                .Select(item => item?.GetValue<string>() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            return args.Count == 0
                ? CommandSpec.Empty
                : CommandSpec.FromArgv(args);
        }

        if (request.Command is JsonValue commandValue && commandValue.TryGetValue<string>(out var commandString))
        {
            return CommandSpec.FromShellLine(commandString);
        }

        return CommandSpec.FromShellLine(FirstNonWhiteSpace(request.Cmd, request.CommandLine));
    }

    private static string GetEffectiveWorkingDirectory(ToolRequest request)
    {
        return FirstNonWhiteSpace(request.Workdir, request.WorkingDirectory, request.WorkingDirectorySnake) ?? ".";
    }

    private static int GetEffectiveYieldTimeMs(ToolRequest request)
    {
        return Math.Clamp(request.YieldTimeMs.GetValueOrDefault(DefaultYieldTimeMs), 0, MaxCommandTimeoutMs);
    }

    private static int GetEffectiveMaxOutputBytes(ToolRequest request)
    {
        var requested = request.MaxOutputBytes ?? request.MaxOutputBytesSnake;
        if (requested is null && request.MaxOutputTokens is not null)
        {
            requested = Math.Max(1, request.MaxOutputTokens.Value) * 4;
        }

        return Math.Clamp(requested.GetValueOrDefault(DefaultCommandMaxOutputBytes), 1, DefaultCommandMaxOutputBytes);
    }

    private static int? GetEffectiveSessionId(ToolRequest request)
    {
        if (request.SessionIdSnake is not null)
        {
            return request.SessionIdSnake;
        }

        if (int.TryParse(request.SessionId, out var sessionId))
        {
            return sessionId;
        }

        if (int.TryParse(request.ProcessId, out var processId))
        {
            return processId;
        }

        return null;
    }

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static async Task<ToolResponse> RunCommandAsync(
        string requestId,
        CommandSpec command,
        string workingDirectory,
        string absoluteWorkingDirectory,
        int timeoutMs,
        int maxOutputBytes,
        string? stdin,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = CreateProcessStartInfo(command, absoluteWorkingDirectory, redirectStdin: stdin is not null);

        process.Start();
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var timedOut = false;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort; the process may already have exited.
            }

            await process.WaitForExitAsync(CancellationToken.None);
        }

        var stdout = LimitOutput(await stdoutTask, maxOutputBytes, out var stdoutTruncated);
        var stderr = LimitOutput(await stderrTask, maxOutputBytes, out var stderrTruncated);
        var exitCode = process.HasExited ? process.ExitCode : (int?)null;
        var result = new
        {
            command = command.Display,
            workingDirectory,
            exitCode,
            exit_code = exitCode,
            timedOut,
            timeoutMs,
            stdinProvided = stdin is not null,
            stdout,
            stderr,
            output = string.IsNullOrEmpty(stderr) ? stdout : stdout + stderr,
            stdoutTruncated,
            stderrTruncated,
        };

        if (timedOut)
        {
            return new ToolResponse
            {
                RequestId = requestId,
                Status = "timeout",
                Result = JsonSerializer.SerializeToNode(result, JsonDefaults.Transport),
                Error = new ToolError
                {
                    Code = "COMMAND_TIMEOUT",
                    Message = $"Command exceeded timeout of {timeoutMs} ms",
                },
            };
        }

        return Ok(requestId, result);
    }

    private static async Task<ToolResponse> RunPersistentExecCommandAsync(
        ToolRequest request,
        CommandSpec command,
        string workingDirectory,
        string absoluteWorkingDirectory,
        int timeoutMs,
        int maxOutputBytes,
        string? stdin,
        CancellationToken cancellationToken)
    {
        var sessionId = Interlocked.Increment(ref NextShellSessionId);
        var process = new Process
        {
            StartInfo = CreateProcessStartInfo(command, absoluteWorkingDirectory, redirectStdin: true),
            EnableRaisingEvents = true,
        };
        var session = new ShellSession(sessionId, process, command.Display, workingDirectory, keepStdinOpen: request.Tty == true);

        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        ShellSessions[sessionId] = session;
        _ = Task.Run(() => ReadProcessStreamAsync(process.StandardOutput, session));
        _ = Task.Run(() => ReadProcessStreamAsync(process.StandardError, session));

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }

        if (request.Tty != true)
        {
            session.CloseStdin();
        }

        var wait = Stopwatch.StartNew();
        await WaitForProcessOrDelayAsync(process, GetEffectiveYieldTimeMs(request), cancellationToken);
        wait.Stop();

        if (!process.HasExited)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(timeoutMs, CancellationToken.None);
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Best effort; the process may already have exited.
                    }
                }
            });
        }

        var result = BuildUnifiedExecResult(session, maxOutputBytes, wait.Elapsed);
        if (process.HasExited)
        {
            ShellSessions.TryRemove(sessionId, out _);
            session.Dispose();
        }

        return Ok(request.RequestId, result);
    }

    private static ProcessStartInfo CreateProcessStartInfo(CommandSpec command, string absoluteWorkingDirectory, bool redirectStdin)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = absoluteWorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (command.Argv is { Count: > 0 })
        {
            startInfo.FileName = command.Argv[0];
            foreach (var arg in command.Argv.Skip(1))
            {
                startInfo.ArgumentList.Add(arg);
            }

            return startInfo;
        }

        startInfo.FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command.Display);
        }
        else
        {
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command.Display);
        }

        return startInfo;
    }

    private static async Task ReadProcessStreamAsync(StreamReader reader, ShellSession session)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return;
                }

                session.AppendOutput(new string(buffer, 0, read));
            }
        }
        catch
        {
            // Stream readers are best-effort for long-lived sessions.
        }
    }

    private static async Task WaitForProcessOrDelayAsync(Process process, int yieldTimeMs, CancellationToken cancellationToken)
    {
        if (process.HasExited || yieldTimeMs <= 0)
        {
            return;
        }

        var delayTask = Task.Delay(yieldTimeMs, cancellationToken);
        var exitTask = process.WaitForExitAsync(cancellationToken);
        await Task.WhenAny(delayTask, exitTask);
    }

    private static object BuildUnifiedExecResult(ShellSession session, int maxOutputBytes, TimeSpan wallTime)
    {
        var output = session.TakeUnreadOutput();
        var limitedOutput = LimitOutput(output, maxOutputBytes, out var outputTruncated);
        var exitCode = session.Process.HasExited ? session.Process.ExitCode : (int?)null;
        var runningSessionId = exitCode is null ? session.Id : (int?)null;
        return new
        {
            chunk_id = session.NextChunkId(),
            wall_time_seconds = wallTime.TotalSeconds,
            exit_code = exitCode,
            exitCode,
            session_id = runningSessionId,
            sessionId = runningSessionId?.ToString(),
            original_token_count = Math.Max(1, output.Length / 4),
            output = limitedOutput,
            stdout = limitedOutput,
            stderr = string.Empty,
            output_truncated = outputTruncated,
            command = session.Command,
            workingDirectory = session.WorkingDirectory,
            stdin_open = session.StdinOpen,
        };
    }

    private static string LimitOutput(string value, int maxBytes, out bool truncated)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            truncated = false;
            return value;
        }

        truncated = true;
        return Encoding.UTF8.GetString(bytes, 0, maxBytes) + "\n...[truncated]";
    }
}
