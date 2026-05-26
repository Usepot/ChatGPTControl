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
    private void LoadState()
    {
        _isLoadingState = true;
        var loaded = _settingsStore.Load();
        var settings = loaded.Settings;

        try
        {
            _relayUrl = settings.RelayUrl;
            _deviceId = settings.DeviceId;
            _deviceToken = settings.DeviceToken;
            _deviceName = settings.DeviceName;
            _gptApiToken = settings.GptApiToken;
            _integrationMode = string.Equals(settings.IntegrationMode, "mcp", StringComparison.Ordinal)
                ? "mcp"
                : "legacy_action";
            _tunnelMode = "tailscale_funnel";
            _currentWorkspace = settings.CurrentWorkspace;
            _importCodexCliWorkspaces = settings.ImportCodexCliWorkspaces;
            _importCodexCliSkills = settings.ImportCodexCliSkills;
            _requireApprovalForWrites = false;
            _multithreadedFileSearches = settings.MultithreadedFileSearches;
            _isDarkMode = settings.IsDarkMode;
            _chatGptPinnedUrl = ResolvePersistedChatGptPinnedUrl(settings);

            TrustedWorkspaces.Clear();
            foreach (var workspace in settings.TrustedWorkspaces.Order(StringComparer.OrdinalIgnoreCase))
            {
                TrustedWorkspaces.Add(workspace);
            }

            SkillRoots.Clear();
            foreach (var skillRoot in settings.SkillRoots.Order(StringComparer.OrdinalIgnoreCase))
            {
                SkillRoots.Add(skillRoot);
            }

            NormalizeTrustedWorkspaces();
            if (ImportCodexCliWorkspaces)
            {
                ImportCodexCliWorkspacesNow();
            }

            NormalizeSkillRoots();
            if (ImportCodexCliSkills)
            {
                ImportCodexCliSkillsNow();
            }

            if (!string.IsNullOrWhiteSpace(CurrentWorkspace) &&
                !TrustedWorkspaces.Any(w => string.Equals(w, CurrentWorkspace, StringComparison.OrdinalIgnoreCase)))
            {
                _currentWorkspace = string.Empty;
            }

            LoadChatGptProjectsFromSettings(settings.ChatGptProjects ?? []);

            ActivityLogs.Clear();
            foreach (var log in loaded.Logs.Take(MaxLogEntries))
            {
                ActivityLogs.Add(log);
            }
        }
        finally
        {
            _isLoadingState = false;
        }

        OnPropertyChanged(nameof(DeviceId));
        OnPropertyChanged(nameof(TunnelModeSummary));
        OnPropertyChanged(nameof(GptApiToken));
        NotifyIntegrationModeChanged();
        OnPropertyChanged(nameof(CurrentWorkspace));
        OnPropertyChanged(nameof(CurrentWorkspaceChoices));
        OnPropertyChanged(nameof(ImportCodexCliWorkspaces));
        OnPropertyChanged(nameof(ImportCodexCliSkills));
        OnPropertyChanged(nameof(HasSkills));
        OnPropertyChanged(nameof(ViewAllSkillsButtonText));
        OnPropertyChanged(nameof(SkillSummaryText));
        OnPropertyChanged(nameof(RequireApprovalForWrites));
        OnPropertyChanged(nameof(MultithreadedFileSearches));
        OnPropertyChanged(nameof(IsDarkMode));
        ApplyTheme(IsDarkMode);
        NotifySchemaChanged();
        RefreshTunnelBarText();
    }

    private void PersistState()
    {
        _settingsStore.Save(new PersistedState
        {
            Settings = BuildCurrentSettings(),
            Logs = ActivityLogs.Take(MaxLogEntries).ToList(),
        });
    }

    private CompanionSettings BuildCurrentSettings()
    {
        return new CompanionSettings
        {
            TunnelMode = _tunnelMode,
            NamedTunnelName = string.Empty,
            NamedTunnelHostname = string.Empty,
            RelayUrl = (_relayUrl ?? string.Empty).Trim(),
            DeviceId = (_deviceId ?? string.Empty).Trim(),
            DeviceToken = (_deviceToken ?? string.Empty).Trim(),
            DeviceName = (_deviceName ?? string.Empty).Trim(),
            GptApiToken = (_gptApiToken ?? string.Empty).Trim(),
            IntegrationMode = IntegrationMode,
            TrustedWorkspaces = TrustedWorkspaces.ToList(),
            CurrentWorkspace = (CurrentWorkspace ?? string.Empty).Trim(),
            ImportCodexCliWorkspaces = ImportCodexCliWorkspaces,
            SkillRoots = SkillRoots.ToList(),
            ImportCodexCliSkills = ImportCodexCliSkills,
            RequireApprovalForWrites = false,
            MultithreadedFileSearches = MultithreadedFileSearches,
            IsDarkMode = IsDarkMode,
            ChatGptPinnedUrl = ChatGptPinnedUrl,
            ChatGptProjects = ChatGptProjects.Select(p => p.ToModel()).ToList(),
        };
    }

    private static string ResolvePersistedChatGptPinnedUrl(CompanionSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ChatGptPinnedUrl))
        {
            return settings.ChatGptPinnedUrl.Trim();
        }

        var migrated = (settings.ChatGptProjects ?? [])
            .Select(p => p.ProjectUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url) &&
                !string.Equals(url.Trim(), ChatGptProjectViewModel.DefaultProjectUrl, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(migrated)
            ? ChatGptProjectViewModel.DefaultProjectUrl
            : migrated.Trim();
    }

    private static void ApplyRequestDefaults(ToolRequest request, CompanionSettings settings)
    {
        if (string.Equals(request.Tool, "search_files", StringComparison.Ordinal) && request.Multithreaded is null)
        {
            request.Multithreaded = settings.MultithreadedFileSearches;
        }

        if (ToolUsesWorkspace(request.Tool) &&
            string.IsNullOrWhiteSpace(request.WorkspaceRoot) &&
            !string.IsNullOrWhiteSpace(settings.CurrentWorkspace))
        {
            request.WorkspaceRoot = settings.CurrentWorkspace;
        }
    }

    private static bool ToolUsesWorkspace(string tool)
    {
        return !string.Equals(tool, "list_trusted_workspaces", StringComparison.Ordinal) &&
               !string.Equals(tool, "get_gpt_instructions", StringComparison.Ordinal) &&
               !string.Equals(tool, "list_skills", StringComparison.Ordinal) &&
               !string.Equals(tool, "get_skill", StringComparison.Ordinal) &&
               !string.Equals(tool, "write_stdin", StringComparison.Ordinal) &&
               !string.Equals(tool, "request_permissions", StringComparison.Ordinal) &&
               !string.Equals(tool, "view_desktop", StringComparison.Ordinal) &&
               !string.Equals(tool, "screenshot_desktop", StringComparison.Ordinal) &&
               !string.Equals(tool, "click_desktop", StringComparison.Ordinal);
    }
}
