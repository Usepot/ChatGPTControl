using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;

namespace PortableCodex.Native.Services;

public sealed partial class FileToolService
{
    private async Task<ToolResponse> HandleDeleteRequestAsync(ToolRequest request, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var path = RequirePath(request);
        var summary = $"Delete {path}";
        var approved = await EnsureWriteApprovalAsync(request, summary, context);
        if (!approved)
        {
            return Denied(request.RequestId, "User denied delete_path request");
        }

        var absolutePath = _pathPolicy.ResolvePathWithinWorkspace(request.WorkspaceRoot!, path);
        if (Directory.Exists(absolutePath))
        {
            Directory.Delete(absolutePath, request.Recursive == true);
        }
        else if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
        }
        else
        {
            throw new FileNotFoundException($"Path does not exist: {path}");
        }

        return Ok(
            request.RequestId,
            new
            {
                path,
                deleted = true,
            });
    }

    private async Task<bool> EnsureWriteApprovalAsync(ToolRequest request, string summary, ToolExecutionContext context)
    {
        if (!context.Settings.RequireApprovalForWrites)
        {
            return true;
        }

        return await context.ApproveWriteAsync(request, summary);
    }

    private static (string Content, int Applied) ApplyPatchOperation(string content, PatchOperation operation)
    {
        if (string.IsNullOrEmpty(operation.Find))
        {
            throw new InvalidOperationException("Patch operations require a non-empty find string");
        }

        if (operation.ReplaceAll == true)
        {
            var occurrences = CountOccurrences(content, operation.Find);
            if (occurrences == 0)
            {
                throw new InvalidOperationException($"Patch find string not found: {operation.Find}");
            }

            return (content.Replace(operation.Find, operation.Replace, StringComparison.Ordinal), occurrences);
        }

        var occurrence = operation.Occurrence.GetValueOrDefault(1);
        var fromIndex = 0;
        var foundAt = -1;
        for (var current = 1; current <= occurrence; current++)
        {
            foundAt = content.IndexOf(operation.Find, fromIndex, StringComparison.Ordinal);
            if (foundAt < 0)
            {
                throw new InvalidOperationException($"Patch occurrence {occurrence} not found for: {operation.Find}");
            }

            fromIndex = foundAt + operation.Find.Length;
        }

        var updated = content[..foundAt] + operation.Replace + content[(foundAt + operation.Find.Length)..];
        return (updated, 1);
    }

    private static int CountOccurrences(string content, string needle)
    {
        var count = 0;
        var currentIndex = 0;
        while (true)
        {
            var found = content.IndexOf(needle, currentIndex, StringComparison.Ordinal);
            if (found < 0)
            {
                return count;
            }

            count++;
            currentIndex = found + needle.Length;
        }
    }

    private static string RequirePath(ToolRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw new InvalidOperationException($"path is required for {request.Tool}");
        }

        return request.Path;
    }

    private static ToolResponse Ok(string requestId, object result)
    {
        return new ToolResponse
        {
            RequestId = requestId,
            Status = "ok",
            Result = JsonSerializer.SerializeToNode(result, JsonDefaults.Transport),
        };
    }

    private static ToolResponse Denied(string requestId, string message)
    {
        return new ToolResponse
        {
            RequestId = requestId,
            Status = "denied",
            Error = new ToolError
            {
                Code = "USER_DENIED",
                Message = message,
            },
        };
    }

    private static ToolResponse Error(string requestId, string code, string message)
    {
        return new ToolResponse
        {
            RequestId = requestId,
            Status = "error",
            Error = new ToolError
            {
                Code = code,
                Message = message,
            },
        };
    }

    private static ToolResponse UnsupportedPlatform(string requestId, string message)
    {
        return Error(requestId, "PLATFORM_CAPABILITY_UNAVAILABLE", message);
    }
}
