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
    private void AssertTrustedWorkspace(CompanionSettings settings, string workspaceRoot)
    {
        if (!_pathPolicy.IsWorkspaceTrusted(settings.TrustedWorkspaces, workspaceRoot))
        {
            throw new InvalidOperationException($"Workspace is not trusted: {workspaceRoot}");
        }
    }

    private Task<object> ListDirAsync(string workspaceRoot, string targetPath, CancellationToken cancellationToken)
    {
        var absolutePath = _pathPolicy.ResolvePathWithinWorkspace(workspaceRoot, targetPath);
        var entries = new List<DirectoryEntry>();

        foreach (var entryPath in Directory.EnumerateFileSystemEntries(absolutePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entryPath);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var size = isDirectory ? 0 : new FileInfo(entryPath).Length;
            entries.Add(new DirectoryEntry
            {
                Name = Path.GetFileName(entryPath),
                Path = _pathPolicy.ToRelativeWorkspacePath(workspaceRoot, entryPath),
                Kind = isDirectory ? "directory" : "file",
                Size = size,
            });
        }

        entries.Sort((left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<object>(new
        {
            path = targetPath,
            entries,
        });
    }

    private async Task<object> ReadFileAsync(
        string workspaceRoot,
        string targetPath,
        int? maxBytes,
        string? encoding,
        CancellationToken cancellationToken)
    {
        var absolutePath = _pathPolicy.ResolvePathWithinWorkspace(workspaceRoot, targetPath);
        var bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken);
        var limit = maxBytes.GetValueOrDefault(DefaultReadMaxBytes);
        if (limit <= 0)
        {
            limit = DefaultReadMaxBytes;
        }

        var readLength = Math.Min(limit, bytes.Length);
        var slice = new ReadOnlySpan<byte>(bytes, 0, readLength).ToArray();
        var requestedEncoding = string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase) ? "base64" : "utf-8";

        return new
        {
            path = targetPath,
            content = requestedEncoding == "base64" ? Convert.ToBase64String(slice) : Encoding.UTF8.GetString(slice),
            truncated = bytes.Length > limit,
            bytes = readLength,
            encoding = requestedEncoding,
        };
    }

    private async Task<ToolResponse> HandleWriteRequestAsync(ToolRequest request, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var path = RequirePath(request);
        var content = request.Content ?? string.Empty;
        var summary = $"Write {path} ({Encoding.UTF8.GetByteCount(content)} bytes)";
        var approved = await EnsureWriteApprovalAsync(request, summary, context);
        if (!approved)
        {
            return Denied(request.RequestId, "User denied write_file request");
        }

        var absolutePath = _pathPolicy.ResolvePathWithinWorkspace(request.WorkspaceRoot!, path);
        if (request.CreateDirectories == true)
        {
            var parent = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        await File.WriteAllTextAsync(absolutePath, content, cancellationToken);
        return Ok(
            request.RequestId,
            new
            {
                path,
                bytesWritten = Encoding.UTF8.GetByteCount(content),
            });
    }
}
