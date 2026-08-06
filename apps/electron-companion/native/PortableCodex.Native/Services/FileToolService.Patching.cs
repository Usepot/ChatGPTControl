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
    private async Task<ToolResponse> HandlePatchRequestAsync(ToolRequest request, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Patch))
        {
            return await HandleCodexPatchRequestAsync(request, context, cancellationToken);
        }

        var path = RequirePath(request);
        var operations = request.Operations ?? [];
        var summary = $"Patch {path} ({operations.Count} operations)";
        var approved = await EnsureWriteApprovalAsync(request, summary, context);
        if (!approved)
        {
            return Denied(request.RequestId, "User denied apply_patch request");
        }

        var absolutePath = _pathPolicy.ResolvePathWithinWorkspace(request.WorkspaceRoot!, path);
        var content = await File.ReadAllTextAsync(absolutePath, cancellationToken);
        var applied = 0;

        foreach (var operation in operations)
        {
            var result = ApplyPatchOperation(content, operation);
            content = result.Content;
            applied += result.Applied;
        }

        await File.WriteAllTextAsync(absolutePath, content, cancellationToken);
        return Ok(
            request.RequestId,
            new
            {
                path,
                operationsApplied = applied,
            });
    }

    private async Task<ToolResponse> HandleCodexPatchRequestAsync(
        ToolRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var sections = ParseCodexPatch(request.Patch ?? string.Empty);
        if (sections.Count == 0)
        {
            throw new InvalidOperationException("Codex patch does not contain any file sections");
        }

        var approved = await EnsureWriteApprovalAsync(
            request,
            $"Apply Codex patch ({sections.Count} file sections)",
            context);
        if (!approved)
        {
            return Denied(request.RequestId, "User denied apply_patch request");
        }

        var changes = await BuildCodexPatchChangesAsync(request.WorkspaceRoot!, sections, cancellationToken);
        foreach (var change in changes)
        {
            if (change.NewContent is null)
            {
                if (File.Exists(change.AbsolutePath))
                {
                    File.Delete(change.AbsolutePath);
                }

                continue;
            }

            var parent = Path.GetDirectoryName(change.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await File.WriteAllTextAsync(change.AbsolutePath, change.NewContent, cancellationToken);
        }

        return Ok(
            request.RequestId,
            new
            {
                filesChanged = changes.Count,
                changes = changes.Select(change => new
                {
                    path = change.Path,
                    action = change.Action,
                }).ToList(),
            });
    }

    private async Task<List<CodexPatchChange>> BuildCodexPatchChangesAsync(
        string workspaceRoot,
        List<CodexPatchSection> sections,
        CancellationToken cancellationToken)
    {
        var pending = new Dictionary<string, CodexPatchChange>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        async Task<CodexPatchChange> GetChangeAsync(string path)
        {
            var absolutePath = _pathPolicy.ResolvePathWithinWorkspace(workspaceRoot, path);
            if (pending.TryGetValue(absolutePath, out var existing))
            {
                return existing;
            }

            var originalExists = File.Exists(absolutePath);
            var originalContent = originalExists
                ? await File.ReadAllTextAsync(absolutePath, cancellationToken)
                : null;
            var change = new CodexPatchChange
            {
                Path = path,
                AbsolutePath = absolutePath,
                OriginalExists = originalExists,
                OriginalContent = originalContent,
                NewContent = originalContent,
            };
            pending[absolutePath] = change;
            return change;
        }

        foreach (var section in sections)
        {
            var change = await GetChangeAsync(section.Path);
            switch (section.Action)
            {
                case "add":
                    if (change.NewContent is not null || change.OriginalExists)
                    {
                        throw new InvalidOperationException($"Cannot add file because it already exists: {section.Path}");
                    }

                    change.NewContent = JoinContentLines(section.Lines, trailingNewLine: section.Lines.Count > 0);
                    break;

                case "delete":
                    if (change.NewContent is null && !change.OriginalExists)
                    {
                        throw new FileNotFoundException($"Cannot delete missing file: {section.Path}");
                    }

                    change.NewContent = null;
                    break;

                case "update":
                    if (change.NewContent is null)
                    {
                        throw new FileNotFoundException($"Cannot update missing file: {section.Path}");
                    }

                    var updated = ApplyCodexPatchToContent(change.NewContent, section.Lines, section.Path);
                    if (string.IsNullOrWhiteSpace(section.MoveTo))
                    {
                        change.NewContent = updated;
                        break;
                    }

                    var targetChange = await GetChangeAsync(section.MoveTo);
                    if (targetChange.NewContent is not null || targetChange.OriginalExists)
                    {
                        throw new InvalidOperationException($"Cannot move file because target already exists: {section.MoveTo}");
                    }

                    change.NewContent = null;
                    targetChange.NewContent = updated;
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported Codex patch action: {section.Action}");
            }
        }

        return pending.Values
            .Where(change => change.OriginalContent != change.NewContent || change.OriginalExists != (change.NewContent is not null))
            .Select(change =>
            {
                change.Action = change switch
                {
                    { OriginalExists: false, NewContent: not null } => "add",
                    { OriginalExists: true, NewContent: null } => "delete",
                    _ => "update",
                };
                return change;
            })
            .ToList();
    }

    private static string ApplyCodexPatchToContent(string content, List<string> patchLines, string path)
    {
        var originalHadTrailingNewLine = NormalizeNewLines(content).EndsWith('\n');
        var lines = SplitContentLines(content);
        var hunks = ParseCodexHunks(patchLines);
        var cursor = 0;

        foreach (var hunk in hunks)
        {
            var oldLines = hunk.Where(line => line.Prefix != '+').Select(line => line.Text).ToList();
            var newLines = hunk.Where(line => line.Prefix != '-').Select(line => line.Text).ToList();
            var index = oldLines.Count == 0 ? cursor : FindSequence(lines, oldLines, cursor);
            if (index < 0)
            {
                index = FindSequence(lines, oldLines, 0);
            }

            if (index < 0)
            {
                throw new InvalidOperationException($"Codex patch hunk did not match: {path}");
            }

            lines.RemoveRange(index, oldLines.Count);
            lines.InsertRange(index, newLines);
            cursor = index + newLines.Count;
        }

        return JoinContentLines(lines, originalHadTrailingNewLine);
    }

    private static List<List<CodexPatchLine>> ParseCodexHunks(List<string> patchLines)
    {
        var hunks = new List<List<CodexPatchLine>>();
        var current = new List<CodexPatchLine>();

        foreach (var line in patchLines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (current.Count > 0)
                {
                    hunks.Add(current);
                    current = [];
                }

                continue;
            }

            if (line.Length == 0)
            {
                current.Add(new CodexPatchLine(' ', string.Empty));
                continue;
            }

            var prefix = line[0];
            if (prefix is not (' ' or '+' or '-'))
            {
                throw new InvalidOperationException($"Invalid Codex patch line prefix: {line}");
            }

            current.Add(new CodexPatchLine(prefix, line[1..]));
        }

        if (current.Count > 0)
        {
            hunks.Add(current);
        }

        return hunks;
    }

    private static int FindSequence(List<string> lines, List<string> sequence, int startIndex)
    {
        if (sequence.Count == 0)
        {
            return Math.Clamp(startIndex, 0, lines.Count);
        }

        for (var i = Math.Clamp(startIndex, 0, lines.Count); i <= lines.Count - sequence.Count; i++)
        {
            var matched = true;
            for (var j = 0; j < sequence.Count; j++)
            {
                if (!string.Equals(lines[i + j], sequence[j], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<CodexPatchSection> ParseCodexPatch(string patch)
    {
        var lines = NormalizeNewLines(patch).Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count < 2 || !string.Equals(lines[0], "*** Begin Patch", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex patch must start with *** Begin Patch");
        }

        var sections = new List<CodexPatchSection>();
        var i = 1;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.Equals(line, "*** End Patch", StringComparison.Ordinal))
            {
                return sections;
            }

            if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                var path = RequirePatchPath(line["*** Add File: ".Length..]);
                i++;
                var fileLines = new List<string>();
                while (i < lines.Count && !lines[i].StartsWith("*** ", StringComparison.Ordinal))
                {
                    if (!lines[i].StartsWith('+'))
                    {
                        throw new InvalidOperationException($"Add file lines must start with '+': {path}");
                    }

                    fileLines.Add(lines[i][1..]);
                    i++;
                }

                sections.Add(new CodexPatchSection("add", path, null, fileLines));
                continue;
            }

            if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                var path = RequirePatchPath(line["*** Delete File: ".Length..]);
                sections.Add(new CodexPatchSection("delete", path, null, []));
                i++;
                continue;
            }

            if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                var path = RequirePatchPath(line["*** Update File: ".Length..]);
                i++;
                string? moveTo = null;
                if (i < lines.Count && lines[i].StartsWith("*** Move to: ", StringComparison.Ordinal))
                {
                    moveTo = RequirePatchPath(lines[i]["*** Move to: ".Length..]);
                    i++;
                }

                var hunkLines = new List<string>();
                while (i < lines.Count && !lines[i].StartsWith("*** ", StringComparison.Ordinal))
                {
                    hunkLines.Add(lines[i]);
                    i++;
                }

                sections.Add(new CodexPatchSection("update", path, moveTo, hunkLines));
                continue;
            }

            throw new InvalidOperationException($"Invalid Codex patch header: {line}");
        }

        throw new InvalidOperationException("Codex patch must end with *** End Patch");
    }

    private static string RequirePatchPath(string value)
    {
        var path = value.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Codex patch file path is required");
        }

        return path;
    }

    private static List<string> SplitContentLines(string content)
    {
        var normalized = NormalizeNewLines(content);
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string JoinContentLines(List<string> lines, bool trailingNewLine)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("\n", lines) + (trailingNewLine ? "\n" : string.Empty);
    }

    private static string NormalizeNewLines(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
