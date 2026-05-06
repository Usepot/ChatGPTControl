using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;

namespace PortableCodex.Native.Services;

public sealed class ToolExecutionContext
{
    public required CompanionSettings Settings { get; init; }

    public required Func<ToolRequest, string, Task<bool>> ApproveWriteAsync { get; init; }
}

public sealed partial class FileToolService
{
    private const int DefaultReadMaxBytes = 200_000;
    private const int DefaultCommandTimeoutMs = 60_000;
    private const int MaxCommandTimeoutMs = 300_000;
    private const int DefaultCommandMaxOutputBytes = 200_000;
    private const int DefaultImageMaxBytes = 5_000_000;
    private const int DefaultYieldTimeMs = 1_000;
    private const int MaxSearchThreadCount = 32;
    private static readonly ConcurrentDictionary<int, ShellSession> ShellSessions = new();
    private static int NextShellSessionId;
    private static readonly HashSet<string> SearchSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "node_modules",
        "dist",
    };

    private readonly PathPolicyService _pathPolicy;
    private readonly Action<ToolProgress>? _progressReporter;

    public FileToolService(PathPolicyService pathPolicy, Action<ToolProgress>? progressReporter = null)
    {
        _pathPolicy = pathPolicy;
        _progressReporter = progressReporter;
    }

    public async Task<ToolResponse> ExecuteAsync(ToolRequest request, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.Tool == "list_trusted_workspaces")
            {
                return Error(request.RequestId, "UNSUPPORTED_TOOL", "Workspace discovery is handled outside the file tool service");
            }

            if (request.Tool == "write_stdin")
            {
                return await HandleWriteStdinAsync(request, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
            {
                return Error(
                    request.RequestId,
                    "FILE_TOOL_ERROR",
                    "workspaceRoot is required when no current workspace default is available");
            }

            AssertTrustedWorkspace(context.Settings, request.WorkspaceRoot);

            return request.Tool switch
            {
                "list_dir" => Ok(request.RequestId, await ListDirAsync(request.WorkspaceRoot, request.Path ?? ".", cancellationToken)),
                "read_file" => Ok(
                    request.RequestId,
                    await ReadFileAsync(
                        request.WorkspaceRoot,
                        RequirePath(request),
                        request.MaxBytes,
                        request.Encoding,
                        cancellationToken)),
                "write_file" => await HandleWriteRequestAsync(request, context, cancellationToken),
                "apply_patch" => await HandlePatchRequestAsync(request, context, cancellationToken),
                "search_files" => Ok(request.RequestId, await SearchFilesAsync(request.WorkspaceRoot, request, cancellationToken)),
                "stat_path" => Ok(request.RequestId, await StatPathAsync(request.WorkspaceRoot, request.Path ?? ".", cancellationToken)),
                "make_dir" => Ok(request.RequestId, await MakeDirAsync(request.WorkspaceRoot, RequirePath(request), cancellationToken)),
                "delete_path" => await HandleDeleteRequestAsync(request, context, cancellationToken),
                "run_command" or "shell" or "exec_command" or "shell_command" => await HandleRunCommandAsync(request, context, cancellationToken),
                "view_image" => Ok(request.RequestId, await ViewImageAsync(request.WorkspaceRoot!, RequirePath(request), request.MaxBytes, cancellationToken)),
                "screenshot_desktop" => await HandleScreenshotDesktopAsync(request, context, cancellationToken),
                _ => Error(request.RequestId, "UNSUPPORTED_TOOL", "Unsupported tool"),
            };
        }
        catch (Exception ex)
        {
            return Error(request.RequestId, "FILE_TOOL_ERROR", ex.Message);
        }
    }

    public string SummarizeRequestForLog(ToolRequest request)
    {
        return request.Tool switch
        {
            "list_trusted_workspaces" => "List trusted workspaces",
            "get_gpt_instructions" => "Get GPT instructions",
            "list_skills" => "List skills",
            "get_skill" => $"Get skill /{request.SkillName}",
            "list_dir" => $"List {request.Path ?? "."} in {Path.GetFileName(_pathPolicy.NormalizeWorkspaceRoot(request.WorkspaceRoot ?? string.Empty))}",
            "read_file" => $"Read {request.Path}",
            "write_file" => $"Write {request.Path}",
            "apply_patch" => string.IsNullOrWhiteSpace(request.Patch) ? $"Patch {request.Path}" : "Apply Codex patch",
            "search_files" => $"Search \"{request.Query}\"",
            "stat_path" => $"Stat {request.Path ?? "."}",
            "make_dir" => $"Create directory {request.Path}",
            "delete_path" => $"Delete {request.Path}",
            "run_command" or "shell" or "exec_command" or "shell_command" => $"Run {GetEffectiveCommand(request)}",
            "write_stdin" => "Write stdin to running command",
            "request_permissions" => "Request sandbox permissions",
            "view_image" => $"View image {request.Path}",
            "screenshot_desktop" => $"Capture desktop screenshot to {request.Path ?? "desktop_screenshot.png"}",
            _ => "Unknown request",
        };
    }

}
