using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;

namespace PortableCodex.Native.Services;

public sealed class ToolExecutionContext
{
    public required CompanionSettings Settings { get; init; }

    public required Func<ToolRequest, string, Task<bool>> ApproveWriteAsync { get; init; }
}

public sealed class FileToolService
{
    private const int DefaultReadMaxBytes = 200_000;
    private const int DefaultCommandTimeoutMs = 60_000;
    private const int MaxCommandTimeoutMs = 300_000;
    private const int DefaultCommandMaxOutputBytes = 200_000;
    private const int MaxSearchThreadCount = 32;
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

            if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
            {
                return Error(request.RequestId, "FILE_TOOL_ERROR", "workspaceRoot is required");
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
                "run_command" => await HandleRunCommandAsync(request, context, cancellationToken),
                "shell" => await HandleShellAsync(request, context, cancellationToken),
                "view_image" => Ok(request.RequestId, await ViewImageAsync(request.WorkspaceRoot, RequirePath(request), cancellationToken)),
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
            "run_command" => $"Run {request.Command}",
            "shell" => $"Shell {request.Command}",
            "view_image" => $"View image {request.Path}",
            _ => "Unknown request",
        };
    }

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

    private async Task<object> SearchFilesAsync(string workspaceRoot, ToolRequest request, CancellationToken cancellationToken)
    {
        var basePath = _pathPolicy.ResolvePathWithinWorkspace(workspaceRoot, request.Path ?? ".");
        var matches = new List<SearchMatch>();
        var maxResults = request.MaxResults.GetValueOrDefault(100);
        if (maxResults <= 0)
        {
            maxResults = 100;
        }

        Regex? regex = null;
        if (request.IsRegex == true)
        {
            var options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
            if (request.CaseSensitive != true)
            {
                options |= RegexOptions.IgnoreCase;
            }

            regex = new Regex(request.Query ?? string.Empty, options);
        }

        var searchFiles = new List<string>();
        CollectSearchFiles(basePath, request, searchFiles, cancellationToken);
        var useMultithreadedSearch = request.Multithreaded == true && searchFiles.Count > 1;
        var threadCount = useMultithreadedSearch ? GetSearchThreadCount(request) : 1;
        ReportProgress(
            request,
            0,
            searchFiles.Count,
            useMultithreadedSearch
                ? $"Searching {searchFiles.Count:N0} files with {threadCount:N0} threads…"
                : $"Searching {searchFiles.Count:N0} files…");

        if (useMultithreadedSearch)
        {
            await SearchFilesMultithreadedAsync(
                searchFiles,
                workspaceRoot,
                request,
                regex,
                maxResults,
                matches,
                threadCount,
                cancellationToken);
        }
        else
        {
            for (var i = 0; i < searchFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (matches.Count >= maxResults)
                {
                    break;
                }

                var fileMatches = await SearchFileAsync(searchFiles[i], workspaceRoot, request, regex, maxResults, cancellationToken);
                matches.AddRange(fileMatches.Take(maxResults - matches.Count));
                ReportProgress(request, i + 1, searchFiles.Count, $"Searched {i + 1:N0} of {searchFiles.Count:N0} files");
            }
        }

        ReportProgress(request, searchFiles.Count, searchFiles.Count, $"Search complete: {matches.Count:N0} matches");

        return new
        {
            path = request.Path ?? ".",
            matches,
            truncated = matches.Count >= maxResults,
        };
    }

    private static int GetSearchThreadCount(ToolRequest request)
    {
        var requested = request.MaxSearchThreads.GetValueOrDefault(Environment.ProcessorCount);
        return Math.Clamp(requested, 1, MaxSearchThreadCount);
    }

    private async Task SearchFilesMultithreadedAsync(
        List<string> searchFiles,
        string workspaceRoot,
        ToolRequest request,
        Regex? regex,
        int maxResults,
        List<SearchMatch> matches,
        int threadCount,
        CancellationToken cancellationToken)
    {
        var matchesLock = new object();
        var processed = 0;

        await Parallel.ForEachAsync(
            searchFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = threadCount,
                CancellationToken = cancellationToken,
            },
            async (searchFile, token) =>
            {
                token.ThrowIfCancellationRequested();

                lock (matchesLock)
                {
                    if (matches.Count >= maxResults)
                    {
                        return;
                    }
                }

                var fileMatches = await SearchFileAsync(searchFile, workspaceRoot, request, regex, maxResults, token);
                lock (matchesLock)
                {
                    var remaining = maxResults - matches.Count;
                    if (remaining > 0)
                    {
                        matches.AddRange(fileMatches.Take(remaining));
                    }
                }

                var completed = Interlocked.Increment(ref processed);
                ReportProgress(request, completed, searchFiles.Count, $"Searched {completed:N0} of {searchFiles.Count:N0} files");
            });
    }

    private void CollectSearchFiles(
        string currentPath,
        ToolRequest request,
        List<string> files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Directory.Exists(currentPath))
        {
            var directoryName = Path.GetFileName(currentPath);
            if (SearchSkipDirs.Contains(directoryName))
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(currentPath))
            {
                CollectSearchFiles(entry, request, files, cancellationToken);
            }

            return;
        }

        if (!File.Exists(currentPath))
        {
            return;
        }

        if (request.FileExtensions is { Count: > 0 })
        {
            var extension = Path.GetExtension(currentPath);
            if (!request.FileExtensions.Contains(extension))
            {
                return;
            }
        }

        files.Add(currentPath);
    }

    private async Task<List<SearchMatch>> SearchFileAsync(
        string currentPath,
        string workspaceRoot,
        ToolRequest request,
        Regex? regex,
        int maxResults,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var matches = new List<SearchMatch>();

        var content = await File.ReadAllTextAsync(currentPath, cancellationToken);
        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            if (matches.Count >= maxResults)
            {
                return matches;
            }

            var line = lines[i];
            if (regex is not null)
            {
                var match = regex.Match(line);
                if (match.Success)
                {
                    matches.Add(new SearchMatch
                    {
                        Path = _pathPolicy.ToRelativeWorkspacePath(workspaceRoot, currentPath),
                        Line = i + 1,
                        Column = match.Index + 1,
                        Preview = line.Trim(),
                    });
                }

                continue;
            }

            var query = request.Query ?? string.Empty;
            var comparison = request.CaseSensitive == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var index = line.IndexOf(query, comparison);
            if (index >= 0)
            {
                matches.Add(new SearchMatch
                {
                    Path = _pathPolicy.ToRelativeWorkspacePath(workspaceRoot, currentPath),
                    Line = i + 1,
                    Column = index + 1,
                    Preview = line.Trim(),
                });
            }
        }

        return matches;
    }

    private void ReportProgress(ToolRequest request, int processed, int total, string message)
    {
        if (!string.Equals(request.Tool, "search_files", StringComparison.Ordinal))
        {
            return;
        }

        _progressReporter?.Invoke(new ToolProgress
        {
            RequestId = request.RequestId,
            Tool = request.Tool,
            Processed = processed,
            Total = total,
            Message = message,
        });
    }

    private Task<object> StatPathAsync(string workspaceRoot, string targetPath, CancellationToken cancellationToken)
    {
        var absolutePath = _pathPolicy.ResolvePathWithinWorkspace(workspaceRoot, targetPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (Directory.Exists(absolutePath))
        {
            return Task.FromResult<object>(new
            {
                path = targetPath,
                stat = new PathStatResult
                {
                    Path = targetPath,
                    Kind = "directory",
                    Size = 0,
                    ModifiedAt = Directory.GetLastWriteTimeUtc(absolutePath).ToString("O"),
                },
            });
        }

        var fileInfo = new FileInfo(absolutePath);
        return Task.FromResult<object>(new
        {
            path = targetPath,
            stat = new PathStatResult
            {
                Path = targetPath,
                Kind = "file",
                Size = fileInfo.Length,
                ModifiedAt = fileInfo.LastWriteTimeUtc.ToString("O"),
            },
        });
    }

    private async Task<object> MakeDirAsync(string workspaceRoot, string targetPath, CancellationToken cancellationToken)
    {
        var absolutePath = _pathPolicy.ResolvePathWithinWorkspace(workspaceRoot, targetPath);
        await Task.Run(() => Directory.CreateDirectory(absolutePath), cancellationToken);
        return new
        {
            path = targetPath,
            created = true,
        };
    }

    private async Task<ToolResponse> HandleRunCommandAsync(ToolRequest request, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            throw new InvalidOperationException("command is required for run_command");
        }

        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory) ? "." : request.WorkingDirectory;
        var absoluteWorkingDirectory = _pathPolicy.ResolvePathWithinWorkspace(request.WorkspaceRoot!, workingDirectory);
        if (!Directory.Exists(absoluteWorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {workingDirectory}");
        }

        var timeoutMs = request.TimeoutMs.GetValueOrDefault(DefaultCommandTimeoutMs);
        timeoutMs = Math.Clamp(timeoutMs, 1_000, MaxCommandTimeoutMs);
        var maxOutputBytes = request.MaxOutputBytes.GetValueOrDefault(DefaultCommandMaxOutputBytes);
        maxOutputBytes = Math.Clamp(maxOutputBytes, 1, DefaultCommandMaxOutputBytes);

        var approved = await EnsureWriteApprovalAsync(
            request,
            $"Run command in {workingDirectory}: {request.Command}",
            context);
        if (!approved)
        {
            return Denied(request.RequestId, "User denied run_command request");
        }

        return await RunCommandAsync(
            request.RequestId,
            request.Command,
            workingDirectory,
            absoluteWorkingDirectory,
            timeoutMs,
            maxOutputBytes,
            cancellationToken);
    }

    private static async Task<ToolResponse> RunCommandAsync(
        string requestId,
        string command,
        string workingDirectory,
        string absoluteWorkingDirectory,
        int timeoutMs,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = absoluteWorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (OperatingSystem.IsWindows())
        {
            process.StartInfo.ArgumentList.Add("/d");
            process.StartInfo.ArgumentList.Add("/c");
            process.StartInfo.ArgumentList.Add(command);
        }
        else
        {
            process.StartInfo.ArgumentList.Add("-lc");
            process.StartInfo.ArgumentList.Add(command);
        }

        process.Start();
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
            command,
            workingDirectory,
            exitCode,
            timedOut,
            timeoutMs,
            stdout,
            stderr,
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

    private async Task<ToolResponse> HandleShellAsync(ToolRequest request, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            throw new InvalidOperationException("command is required for shell");
        }

        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory) ? "." : request.WorkingDirectory;
        var absoluteWorkingDirectory = _pathPolicy.ResolvePathWithinWorkspace(request.WorkspaceRoot!, workingDirectory);
        if (!Directory.Exists(absoluteWorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {workingDirectory}");
        }

        var timeoutMs = request.TimeoutMs.GetValueOrDefault(DefaultCommandTimeoutMs);
        timeoutMs = Math.Clamp(timeoutMs, 1_000, MaxCommandTimeoutMs);
        var maxOutputBytes = request.MaxOutputBytes.GetValueOrDefault(DefaultCommandMaxOutputBytes);
        maxOutputBytes = Math.Clamp(maxOutputBytes, 1, DefaultCommandMaxOutputBytes);

        var approved = await EnsureWriteApprovalAsync(
            request,
            $"Shell in {workingDirectory}: {request.Command}",
            context);
        if (!approved)
        {
            return Denied(request.RequestId, "User denied shell request");
        }

        var shellChoice = (request.Shell ?? "auto").ToLowerInvariant();
        return await RunShellAsync(
            request.RequestId,
            request.Command,
            shellChoice,
            workingDirectory,
            absoluteWorkingDirectory,
            timeoutMs,
            maxOutputBytes,
            cancellationToken);
    }

    private static async Task<ToolResponse> RunShellAsync(
        string requestId,
        string command,
        string shellChoice,
        string workingDirectory,
        string absoluteWorkingDirectory,
        int timeoutMs,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        using var process = new Process();

        string fileName;
        string[] args;

        if (shellChoice == "bash")
        {
            fileName = "bash";
            args = ["-c", command];
        }
        else if (shellChoice == "cmd")
        {
            fileName = "cmd.exe";
            args = ["/d", "/c", command];
        }
        else if (shellChoice == "powershell")
        {
            fileName = "powershell.exe";
            args = ["-NonInteractive", "-Command", command];
        }
        else
        {
            // auto: PowerShell on Windows, bash elsewhere
            if (OperatingSystem.IsWindows())
            {
                fileName = "powershell.exe";
                args = ["-NonInteractive", "-Command", command];
            }
            else
            {
                fileName = "bash";
                args = ["-c", command];
            }
        }

        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = absoluteWorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
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
            command,
            shell = fileName,
            workingDirectory,
            exitCode,
            timedOut,
            timeoutMs,
            stdout,
            stderr,
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

    private static readonly Dictionary<string, string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".tiff"] = "image/tiff",
        [".tif"] = "image/tiff",
    };

    private async Task<object> ViewImageAsync(string workspaceRoot, string targetPath, CancellationToken cancellationToken)
    {
        var absolutePath = _pathPolicy.ResolvePathWithinWorkspace(workspaceRoot, targetPath);
        var bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken);
        var extension = Path.GetExtension(absolutePath);
        var mimeType = ImageMimeTypes.TryGetValue(extension, out var mime) ? mime : "application/octet-stream";
        var base64 = Convert.ToBase64String(bytes);
        return new
        {
            path = targetPath,
            imageUrl = $"data:{mimeType};base64,{base64}",
            mimeType,
            sizeBytes = bytes.Length,
            detail = (string?)null,
        };
    }


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

    private sealed record CodexPatchLine(char Prefix, string Text);

    private sealed record CodexPatchSection(string Action, string Path, string? MoveTo, List<string> Lines);

    private sealed class CodexPatchChange
    {
        public string Path { get; set; } = string.Empty;

        public string AbsolutePath { get; set; } = string.Empty;

        public bool OriginalExists { get; set; }

        public string? OriginalContent { get; set; }

        public string? NewContent { get; set; }

        public string Action { get; set; } = "update";
    }
}
