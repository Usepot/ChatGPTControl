using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Wpf.Ui.Appearance;
using WpfApp = System.Windows.Application;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;
using WpfMessageBoxResult = System.Windows.MessageBoxResult;
using PortableCodex.Native.Models;
using PortableCodex.Native.Services;
using PortableCodex.Native.Utils;
using Forms = System.Windows.Forms;

namespace PortableCodex.Native.ViewModels;

public sealed partial class MainViewModel
{
    private async Task RefreshDiffAsync()
    {
        ClearError();

        var workspace = CurrentWorkspace;
        if (string.IsNullOrWhiteSpace(workspace))
        {
            DiffStatus = "Choose a workspace first.";
            DiffText = "No current workspace is selected.";
            return;
        }

        if (!Directory.Exists(workspace))
        {
            DiffStatus = "Workspace folder not found.";
            DiffText = workspace;
            return;
        }

        DiffStatus = "Checking workspace...";
        try
        {
            if (!await IsGitRepositoryAsync(workspace))
            {
                DiffStatus = "Not a Git repository.";
                DiffText = string.Join(Environment.NewLine,
                    "This workspace is not inside a Git repository, so there is no uncommitted diff to show.",
                    string.Empty,
                    "To use this view, open or trust a Git project folder, or run `git init` in this workspace.",
                    string.Empty,
                    $"Workspace: {workspace}");
                return;
            }

            DiffStatus = "Loading diff...";
            var paths = GetDiffModePaths(workspace);
            if (string.Equals(DiffMode, "last turns", StringComparison.OrdinalIgnoreCase) && paths.Count == 0)
            {
                DiffStatus = $"No changed paths found in last {DiffTurnCount} turns.";
                DiffText = "Only future write, patch, delete, and mkdir tool calls record affected paths for this view. Use uncommitted to see the full workspace diff.";
                return;
            }

            var diff = await GetUncommittedDiffAsync(workspace, paths);
            if (string.IsNullOrWhiteSpace(diff))
            {
                DiffText = string.Equals(DiffMode, "last turns", StringComparison.OrdinalIgnoreCase)
                    ? $"No uncommitted diff for paths touched in the last {DiffTurnCount} turns."
                    : "No uncommitted changes in this workspace.";
            }
            else
            {
                DiffText = diff;
            }

            DiffStatus = string.Equals(DiffMode, "last turns", StringComparison.OrdinalIgnoreCase)
                ? $"Showing uncommitted diff for {paths.Count} path(s) from last {DiffTurnCount} turns."
                : "Showing all uncommitted workspace changes.";
        }
        catch (Exception ex)
        {
            DiffStatus = "Could not load diff.";
            DiffText = FormatDiffError(ex.Message);
        }
    }

    private void CopyDiff()
    {
        CopyText(DiffText);
    }

    private IReadOnlyList<string> GetDiffModePaths(string workspace)
    {
        if (!string.Equals(DiffMode, "last turns", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return ActivityLogs
            .Where(log => string.Equals(log.WorkspaceRoot, workspace, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Max(1, DiffTurnCount))
            .SelectMany(log => log.AffectedPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<string> GetUncommittedDiffAsync(string workspace, IReadOnlyList<string> paths)
    {
        var unstaged = await RunGitAsync(workspace, paths, "diff", "--no-ext-diff");
        var staged = await RunGitAsync(workspace, paths, "diff", "--cached", "--no-ext-diff");

        if (string.IsNullOrWhiteSpace(staged))
        {
            return unstaged;
        }

        if (string.IsNullOrWhiteSpace(unstaged))
        {
            return staged;
        }

        return staged + Environment.NewLine + Environment.NewLine + unstaged;
    }

    private static async Task<bool> IsGitRepositoryAsync(string workspace)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workspace,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            process.StartInfo.ArgumentList.Add("rev-parse");
            process.StartInfo.ArgumentList.Add("--is-inside-work-tree");

            if (!process.Start())
            {
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var stdout = (await stdoutTask).Trim();
            return process.ExitCode == 0 && string.Equals(stdout, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatDiffError(string message)
    {
        if (message.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
        {
            return "This workspace is not inside a Git repository. Open or trust a Git project folder to view uncommitted changes.";
        }

        if (message.Contains("could not start git", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("The system cannot find the file specified", StringComparison.OrdinalIgnoreCase))
        {
            return "Git could not be started. Install Git or make sure git.exe is available on PATH, then refresh the diff.";
        }

        return string.IsNullOrWhiteSpace(message)
            ? "Git did not return a diff. Try refreshing again."
            : message;
    }

    private static async Task<string> RunGitAsync(string workspace, IReadOnlyList<string> paths, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.StartInfo.ArgumentList.Add("--");
        foreach (var path in paths)
        {
            process.StartInfo.ArgumentList.Add(path);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start git.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "git diff failed." : stderr.Trim());
        }

        return stdout.TrimEnd();
    }

    private static List<string> GetAffectedPaths(ToolRequest request)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            paths.Add(request.Path);
        }

        if (!string.IsNullOrWhiteSpace(request.Patch))
        {
            foreach (var line in request.Patch.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                const string addFile = "*** Add File: ";
                const string updateFile = "*** Update File: ";
                const string deleteFile = "*** Delete File: ";

                if (line.StartsWith(addFile, StringComparison.Ordinal))
                {
                    paths.Add(line[addFile.Length..].Trim());
                }
                else if (line.StartsWith(updateFile, StringComparison.Ordinal))
                {
                    paths.Add(line[updateFile.Length..].Trim());
                }
                else if (line.StartsWith(deleteFile, StringComparison.Ordinal))
                {
                    paths.Add(line[deleteFile.Length..].Trim());
                }
            }
        }

        return paths
            .Select(NormalizeDiffPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeDiffPath(string path)
    {
        return path.Trim().TrimStart('.', '/', '\\').Replace('\\', '/');
    }
}
