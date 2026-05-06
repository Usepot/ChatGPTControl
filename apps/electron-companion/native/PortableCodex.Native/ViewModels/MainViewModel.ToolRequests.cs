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
    #region Tool request handling

    private async Task<ToolResponse> HandleToolRequestAsync(ToolRequest request)
    {
        var settingsSnapshot = await RunOnUiThreadAsync(BuildCurrentSettings);
        ApplyRequestDefaults(request, settingsSnapshot);
        var logEntry = new ToolLogEntry
        {
            RequestId = request.RequestId,
            Tool = request.Tool,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            WorkspaceRoot = request.WorkspaceRoot,
            AffectedPaths = GetAffectedPaths(request),
            Summary = _fileToolService.SummarizeRequestForLog(request),
            Status = "pending",
            Approval = NeedsApproval(settingsSnapshot, request) ? "pending" : "not_required",
        };

        await RunOnUiThreadAsync(() =>
        {
            ActivityLogs.Insert(0, logEntry);
            while (ActivityLogs.Count > MaxLogEntries)
            {
                ActivityLogs.RemoveAt(ActivityLogs.Count - 1);
            }

            PersistState();
        });

        var response = request.Tool switch
        {
            "list_trusted_workspaces" => new ToolResponse
            {
                RequestId = request.RequestId,
                Status = "ok",
                Result = JsonSerializer.SerializeToNode(
                    new { workspaces = settingsSnapshot.TrustedWorkspaces, currentWorkspace = settingsSnapshot.CurrentWorkspace },
                    JsonDefaults.Transport),
            },
            "get_gpt_instructions" => new ToolResponse
            {
                RequestId = request.RequestId,
                Status = "ok",
                Result = JsonSerializer.SerializeToNode(
                    new { instructions = RelayContentService.GptInstructions },
                    JsonDefaults.Transport),
            },
            "list_skills" => _skillService.ListSkills(request, settingsSnapshot),
            "get_skill" => await _skillService.GetSkillAsync(request, settingsSnapshot),
            "request_permissions" => CreateRequestPermissionsResponse(request),
            _ => await _fileToolService.ExecuteAsync(
                request,
                new ToolExecutionContext
                {
                    Settings = settingsSnapshot,
                    ApproveWriteAsync = RequestWriteApprovalAsync,
                }),
        };

        if (string.Equals(request.Tool, "search_files", StringComparison.Ordinal))
        {
            await RunOnUiThreadAsync(() =>
            {
                FileSearchProgressValue = 100;
                FileSearchProgressText = "Search complete";
                IsFileSearchInProgress = false;
            });
        }

        await RunOnUiThreadAsync(() =>
        {
            var match = ActivityLogs
                .Select((item, index) => new { item, index })
                .FirstOrDefault(v => v.item.RequestId == request.RequestId);
            if (match is null)
            {
                return;
            }

            var approval = match.item.Approval;
            if (string.Equals(approval, "pending", StringComparison.Ordinal))
            {
                approval = string.Equals(response.Status, "denied", StringComparison.Ordinal) ? "denied" : "approved";
            }

            var updated = new ToolLogEntry
            {
                RequestId = match.item.RequestId,
                Tool = match.item.Tool,
                CreatedAt = match.item.CreatedAt,
                CompletedAt = DateTime.UtcNow.ToString("O"),
                WorkspaceRoot = match.item.WorkspaceRoot,
                AffectedPaths = match.item.AffectedPaths,
                Summary = match.item.Summary,
                Status = response.Status,
                Approval = approval,
                Detail = response.Error is not null
                    ? $"{response.Error.Code}: {response.Error.Message}"
                    : response.Result?.ToJsonString(JsonDefaults.Storage),
            };

            ActivityLogs[match.index] = updated;
            if (SelectedLog?.RequestId == updated.RequestId)
            {
                SelectedLog = updated;
                OnPropertyChanged(nameof(SelectedLogDetail));
            }

            PersistState();
        });

        return response;
    }

    private static ToolResponse CreateRequestPermissionsResponse(ToolRequest request)
    {
        return new ToolResponse
        {
            RequestId = request.RequestId,
            Status = "denied",
            Error = new ToolError
            {
                Code = "REQUEST_PERMISSIONS_UNSUPPORTED",
                Message = "Portable Codex uses trusted workspaces and companion write approvals instead of Codex-style dynamic sandbox permission escalation.",
            },
            Result = JsonSerializer.SerializeToNode(
                new
                {
                    granted = false,
                    permissions = request.Permissions ?? [],
                    reason = request.Reason,
                    alternative = "Trust additional workspace roots in the companion app or approve individual write/command prompts.",
                },
                JsonDefaults.Transport),
        };
    }

    private void OnRelayTerminalDispatch(ToolRequest request, ToolResponse response)
    {
        _ = RunOnUiThreadAsync(() =>
        {
            var entry = new ToolLogEntry
            {
                RequestId = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString() : request.RequestId,
                Tool = request.Tool,
                CreatedAt = DateTime.UtcNow.ToString("O"),
                CompletedAt = DateTime.UtcNow.ToString("O"),
                WorkspaceRoot = request.WorkspaceRoot,
                AffectedPaths = GetAffectedPaths(request),
                Summary = _fileToolService.SummarizeRequestForLog(request),
                Status = response.Status,
                Approval = "not_required",
                Detail = response.Error is not null
                    ? $"{response.Error.Code}: {response.Error.Message}"
                    : response.Result?.ToJsonString(JsonDefaults.Storage),
            };

            ActivityLogs.Insert(0, entry);
            while (ActivityLogs.Count > MaxLogEntries)
            {
                ActivityLogs.RemoveAt(ActivityLogs.Count - 1);
            }

            PersistState();
        });
    }

    private Task<bool> RequestWriteApprovalAsync(ToolRequest request, string summary)
    {
        return RunOnUiThreadAsync(() =>
        {
            var detail = JsonSerializer.Serialize(
                new { tool = request.Tool, workspaceRoot = request.WorkspaceRoot },
                JsonDefaults.Storage);
            var result = WpfMessageBox.Show(
                $"ChatGPT requested: {summary}{Environment.NewLine}{Environment.NewLine}{detail}",
                "Approve tool call",
                WpfMessageBoxButton.YesNo,
                WpfMessageBoxImage.Question);
            return result == WpfMessageBoxResult.Yes;
        });
    }

    private static bool NeedsApproval(CompanionSettings settings, ToolRequest request)
    {
        return settings.RequireApprovalForWrites && ProtocolConstants.WriteTools.Contains(request.Tool);
    }

    #endregion
}
