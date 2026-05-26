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

    private async Task<object> ViewImageAsync(
        string workspaceRoot,
        string targetPath,
        int? maxBytes,
        CancellationToken cancellationToken)
    {
        var absolutePath = _pathPolicy.ResolvePathWithinWorkspace(workspaceRoot, targetPath);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Image file does not exist: {targetPath}");
        }

        var bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken);
        var limit = maxBytes.GetValueOrDefault(DefaultImageMaxBytes);
        if (limit <= 0)
        {
            limit = DefaultImageMaxBytes;
        }

        if (bytes.Length > limit)
        {
            throw new InvalidOperationException($"Image is {bytes.Length} bytes, which exceeds maxBytes={limit}");
        }

        var mimeType = GetImageMimeType(absolutePath);
        return new
        {
            path = targetPath,
            mimeType,
            bytes = bytes.Length,
            dataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}",
        };
    }

    private static string GetImageMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => throw new InvalidOperationException("view_image supports png, jpg, jpeg, gif, webp, bmp, and svg files"),
        };
    }

    private async Task<ToolResponse> HandleScreenshotDesktopAsync(
        ToolRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(request.Path) ? null : request.Path;
        var requestedScreen = string.IsNullOrWhiteSpace(request.Screen) ? "primary" : request.Screen.Trim();

        if (!string.IsNullOrWhiteSpace(path))
        {
            if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
            {
                throw new InvalidOperationException("workspaceRoot is required when path is supplied for view_desktop");
            }

            AssertTrustedWorkspace(context.Settings, request.WorkspaceRoot);
            var approved = await EnsureWriteApprovalAsync(
                request,
                $"View desktop screen {requestedScreen} and save image to {path}",
                context);
            if (!approved)
            {
                return Denied(request.RequestId, "User denied view_desktop request");
            }
        }

        var absolutePath = string.IsNullOrWhiteSpace(path)
            ? null
            : _pathPolicy.ResolvePathWithinWorkspace(request.WorkspaceRoot!, path);
        var bytes = Array.Empty<byte>();

        var bounds = Rectangle.Empty;
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            bounds = ResolveDesktopCaptureBounds(requestedScreen);
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            bytes = stream.ToArray();
        }, cancellationToken);

        if (!string.IsNullOrWhiteSpace(absolutePath))
        {
            var parent = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await File.WriteAllBytesAsync(absolutePath, bytes, cancellationToken);
        }

        var inlineLimit = request.MaxBytes.GetValueOrDefault(DefaultImageMaxBytes);
        if (inlineLimit <= 0)
        {
            inlineLimit = DefaultImageMaxBytes;
        }

        var inlineImageReturned = bytes.Length <= inlineLimit;
        var dataUrl = inlineImageReturned
            ? $"data:image/png;base64,{Convert.ToBase64String(bytes)}"
            : null;

        return Ok(
            request.RequestId,
            new
            {
                path,
                screen = requestedScreen,
                width = bounds.Width,
                height = bounds.Height,
                bytes = bytes.Length,
                mimeType = "image/png",
                dataUrl,
                inlineImageReturned,
                inlineImageTruncated = !inlineImageReturned,
            });
    }

    private static Rectangle ResolveDesktopCaptureBounds(string requestedScreen)
    {
        if (string.Equals(requestedScreen, "all", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedScreen, "virtual", StringComparison.OrdinalIgnoreCase))
        {
            var virtualBounds = SystemInformation.VirtualScreen;
            if (virtualBounds.Width <= 0 || virtualBounds.Height <= 0)
            {
                throw new InvalidOperationException("No desktop screens are available");
            }

            return virtualBounds;
        }

        var screens = Screen.AllScreens;
        if (screens.Length == 0)
        {
            throw new InvalidOperationException("No desktop screens are available");
        }

        if (string.Equals(requestedScreen, "primary", StringComparison.OrdinalIgnoreCase))
        {
            return Screen.PrimaryScreen?.Bounds
                ?? screens.FirstOrDefault(screen => screen.Primary)?.Bounds
                ?? throw new InvalidOperationException("No primary screen is available");
        }

        if (int.TryParse(requestedScreen, out var screenIndex))
        {
            if (screenIndex < 0 || screenIndex >= screens.Length)
            {
                throw new InvalidOperationException($"Desktop screen index {screenIndex} is not available; found {screens.Length} screen(s)");
            }

            return screens[screenIndex].Bounds;
        }

        throw new InvalidOperationException("view_desktop screen must be primary, all, virtual, or a zero-based display index such as 0 or 1");
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

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(currentPath);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return;
        }

        if (Directory.Exists(currentPath))
        {
            var directoryName = Path.GetFileName(currentPath);
            if (SearchSkipDirs.Contains(directoryName))
            {
                return;
            }

            string[] entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(currentPath).ToArray();
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            foreach (var entry in entries)
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
            var extension = Path.GetExtension(currentPath).TrimStart('.');
            if (!request.FileExtensions.Any(requestedExtension =>
                    string.Equals(
                        requestedExtension.TrimStart('.'),
                        extension,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
        }

        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(currentPath);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        if (fileInfo.Length > DefaultSearchMaxFileBytes)
        {
            return;
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
}
