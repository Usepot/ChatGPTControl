using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
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

public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogEntries = 250;
    private const int LocalRelayPort = 8787;

    public const int StepSetup = 0;
    public const int StepCreateGpt = 1;
    public const int StepConfigureAction = 2;
    public const int StepWorkspaces = 3;
    public const int StepReady = 4;
    public const int StepDiffViewer = 5;

    private readonly CredentialService _credentialService = new();
    private readonly PathPolicyService _pathPolicy = new();
    private readonly RelayContentService _relayContentService = new();
    private readonly CodexCliWorkspaceService _codexCliWorkspaceService = new();
    private readonly SkillService _skillService = new();

    private readonly SettingsStore _settingsStore;
    private readonly FileToolService _fileToolService;
    private readonly LocalRelayServerService _localRelayService;
    private readonly RelayClientService _relayClientService;
    private readonly TunnelService _tunnelService;

    private int _currentStep;
    private bool _credentialsReady;
    private bool _relayReady;
    private bool _tunnelReady;
    private string _tunnelUrl = string.Empty;

    private string _relayConnectionState = "disconnected";
    private string _relayConnectionMessage = "Not connected";
    private string _localRelayState = "stopped";
    private string _localRelayMessage = string.Empty;
    private string _tunnelState = "stopped";
    private string _tunnelMessage = string.Empty;
    private string _tunnelBarText = "No public Funnel URL";

    private string _relayUrl = string.Empty;
    private string _deviceId = string.Empty;
    private string _deviceToken = string.Empty;
    private string _deviceName = string.Empty;
    private string _gptApiToken = string.Empty;
    private string _integrationMode = "legacy_action";
    private string _tunnelMode = "tailscale_funnel";
    private string _currentWorkspace = string.Empty;
    private bool _importCodexCliWorkspaces;
    private bool _importCodexCliSkills;
    private bool _requireApprovalForWrites = true;
    private bool _multithreadedFileSearches;
    private bool _isDarkMode = true;

    private string _logFilter = "all";
    private ToolLogEntry? _selectedLog;
    private bool _isActivityLogExpanded;
    private bool _isFileSearchInProgress;
    private double _fileSearchProgressValue;
    private string _fileSearchProgressText = string.Empty;
    private string _diffMode = "uncommitted";
    private int _diffTurnCount = 5;
    private string _diffText = "Click Refresh diff to inspect workspace changes.";
    private string _diffStatus = "Diff viewer idle";
    private string _errorBanner = string.Empty;
    private string _schemaCopiedLabel = "Copy";
    private string _mcpEndpointCopiedLabel = "Copy";
    private string _instructionsCopiedLabel = "Copy prompt";
    private string _tokenCopiedLabel = "Copy";
    private string _tunnelCopiedLabel = "Copy";
    private bool _isLoadingState;

    public MainViewModel()
    {
        _settingsStore = new SettingsStore(_credentialService);
        _fileToolService = new FileToolService(_pathPolicy, OnToolProgress);

        _localRelayService = new LocalRelayServerService(
            status =>
            {
                _ = RunOnUiThreadAsync(() =>
                {
                    LocalRelayState = status.State;
                    LocalRelayMessage = status.Message;
                    RelayReady = string.Equals(status.State, "running", StringComparison.Ordinal);
                });
            },
            OnRelayTerminalDispatch);

        _relayClientService = new RelayClientService(
            BuildCurrentSettings,
            HandleToolRequestAsync,
            status =>
            {
                _ = RunOnUiThreadAsync(() =>
                {
                    RelayConnectionState = status.State;
                    RelayConnectionMessage = status.Message;
                });
            });

        _tunnelService = new TunnelService(status =>
        {
            _ = RunOnUiThreadAsync(() =>
            {
                TunnelState = status.State;
                TunnelMessage = status.Message;
                if (string.Equals(status.State, "running", StringComparison.Ordinal))
                {
                    TunnelUrl = status.Message;
                    TunnelReady = true;
                }
                else
                {
                    TunnelUrl = string.Empty;
                    TunnelReady = false;
                }

                NotifySchemaChanged();
                RefreshTunnelBarText();
            });
        });

        NextStepCommand = new AsyncRelayCommand(NextStepAsync, () => CanGoNext);
        PrevStepCommand = new RelayCommand(PrevStep, () => CanGoBack);
        SelectIntegrationModeCommand = new RelayCommand(param => SelectIntegrationMode(param as string));
        CopySchemaCommand = new RelayCommand(CopySchemaWithFeedback);
        CopyMcpEndpointCommand = new RelayCommand(CopyMcpEndpointWithFeedback);
        CopyInstructionsCommand = new RelayCommand(CopyInstructionsWithFeedback);
        CopyAuthTokenCommand = new RelayCommand(CopyTokenWithFeedback);
        CopyTunnelUrlCommand = new RelayCommand(CopyTunnelWithFeedback);
        ApplyTunnelSettingsCommand = new AsyncRelayCommand(ApplyTunnelSettingsAsync);
        InstallTailscaleCommand = new AsyncRelayCommand(InstallTailscaleAsync);
        AddWorkspaceCommand = new RelayCommand(AddWorkspace);
        AddSkillCommand = new RelayCommand(AddSkill);
        ViewSkillCommand = new RelayCommand(ViewSkill);
        RemoveSkillCommand = new RelayCommand(param => RemoveSkill(param as string));
        TrustWholeSystemCommand = new RelayCommand(TrustWholeSystem);
        ViewAllWorkspacesCommand = new RelayCommand(ViewAllWorkspaces);
        ViewAllSkillsCommand = new RelayCommand(ViewAllSkills);
        RemoveWorkspaceCommand = new RelayCommand(param => RemoveWorkspace(param as string));
        ReturnToSetupCommand = new RelayCommand(() => CurrentStep = StepSetup);
        OpenGptBuilderCommand = new RelayCommand(OpenGptBuilder);
        ToggleActivityLogCommand = new RelayCommand(ToggleActivityLog);
        ViewDiffViewerCommand = new RelayCommand(() => CurrentStep = StepDiffViewer);
        ReturnToDashboardCommand = new RelayCommand(() => CurrentStep = StepReady);
        RefreshDiffCommand = new AsyncRelayCommand(RefreshDiffAsync);
        CopyDiffCommand = new RelayCommand(CopyDiff);

        FilteredLogs = CollectionViewSource.GetDefaultView(ActivityLogs);
        FilteredLogs.Filter = FilterLogs;
        TrustedWorkspaces.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasWorkspaces));
            OnPropertyChanged(nameof(ViewAllWorkspacesButtonText));
            OnPropertyChanged(nameof(CurrentWorkspaceChoices));
            if (!_isLoadingState &&
                !string.IsNullOrWhiteSpace(CurrentWorkspace) &&
                !TrustedWorkspaces.Any(w => string.Equals(w, CurrentWorkspace, StringComparison.OrdinalIgnoreCase)))
            {
                CurrentWorkspace = string.Empty;
            }

            SyncCurrentStepWithState();
        };
        SkillRoots.CollectionChanged += (_, _) =>
        {
            RefreshSkillList();
            OnPropertyChanged(nameof(HasSkills));
            OnPropertyChanged(nameof(ViewAllSkillsButtonText));
            OnPropertyChanged(nameof(SkillSummaryText));
        };
        ActivityLogs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasActivityLogs));
            FilteredLogs.Refresh();
        };

        LoadState();
    }

    #region Collections

    public ObservableCollection<string> TrustedWorkspaces { get; } = [];
    public ObservableCollection<string> SkillRoots { get; } = [];
    public ObservableCollection<SkillListEntry> Skills { get; } = [];
    public ObservableCollection<ToolLogEntry> ActivityLogs { get; } = [];
    public ICollectionView FilteredLogs { get; }
    public IReadOnlyList<string> LogFilters { get; } = ["all", "pending", "ok", "denied", "error", "timeout"];
    public IReadOnlyList<string> DiffModes { get; } = ["uncommitted", "last turns"];
    public IReadOnlyList<int> DiffTurnCounts { get; } = [1, 3, 5, 10, 25];

    #endregion

    #region Commands

    public AsyncRelayCommand NextStepCommand { get; }
    public RelayCommand PrevStepCommand { get; }
    public RelayCommand SelectIntegrationModeCommand { get; }
    public RelayCommand CopySchemaCommand { get; }
    public RelayCommand CopyMcpEndpointCommand { get; }
    public RelayCommand CopyInstructionsCommand { get; }
    public RelayCommand CopyAuthTokenCommand { get; }
    public RelayCommand CopyTunnelUrlCommand { get; }
    public AsyncRelayCommand ApplyTunnelSettingsCommand { get; }
    public AsyncRelayCommand InstallTailscaleCommand { get; }
    public RelayCommand AddWorkspaceCommand { get; }
    public RelayCommand AddSkillCommand { get; }
    public RelayCommand ViewSkillCommand { get; }
    public RelayCommand RemoveSkillCommand { get; }
    public RelayCommand TrustWholeSystemCommand { get; }
    public RelayCommand ViewAllWorkspacesCommand { get; }
    public RelayCommand ViewAllSkillsCommand { get; }
    public RelayCommand RemoveWorkspaceCommand { get; }
    public RelayCommand ReturnToSetupCommand { get; }
    public RelayCommand OpenGptBuilderCommand { get; }
    public RelayCommand ToggleActivityLogCommand { get; }
    public RelayCommand ViewDiffViewerCommand { get; }
    public RelayCommand ReturnToDashboardCommand { get; }
    public AsyncRelayCommand RefreshDiffCommand { get; }
    public RelayCommand CopyDiffCommand { get; }

    #endregion

    #region Step navigation

    public int CurrentStep
    {
        get => _currentStep;
        set
        {
            if (SetProperty(ref _currentStep, value))
            {
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(NextButtonText));
                OnPropertyChanged(nameof(ShowBackButton));
                OnPropertyChanged(nameof(ShowNextButton));
                NextStepCommand.RaiseCanExecuteChanged();
                PrevStepCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanGoNext => CurrentStep switch
    {
        StepSetup => CredentialsReady && RelayReady,
        StepReady => false,
        _ => true,
    };

    public bool CanGoBack => CurrentStep > StepSetup && CurrentStep < StepReady;

    public string NextButtonText => CurrentStep switch
    {
        StepSetup => TunnelReady ? "Continue" : "Continue without Funnel",
        StepWorkspaces => "Finish setup",
        _ => "Next",
    };

    public bool ShowBackButton => CanGoBack;

    public bool ShowNextButton => CurrentStep < StepReady;

    #endregion

    #region Setup state

    public bool CredentialsReady
    {
        get => _credentialsReady;
        private set
        {
            if (SetProperty(ref _credentialsReady, value))
            {
                RaiseNavigationChanged();
                TryAutoAdvanceFromSetupAfterTunnelReady();
            }
        }
    }

    public bool RelayReady
    {
        get => _relayReady;
        private set
        {
            if (SetProperty(ref _relayReady, value))
            {
                RaiseNavigationChanged();
                TryAutoAdvanceFromSetupAfterTunnelReady();
            }
        }
    }

    public bool TunnelReady
    {
        get => _tunnelReady;
        private set
        {
            if (SetProperty(ref _tunnelReady, value))
            {
                RaiseNavigationChanged();
                OnPropertyChanged(nameof(NextButtonText));
                TryAutoAdvanceFromSetupAfterTunnelReady();
            }
        }
    }

    public string TunnelUrl
    {
        get => _tunnelUrl;
        private set => SetProperty(ref _tunnelUrl, value);
    }

    #endregion

    #region Connection status

    public string RelayConnectionState
    {
        get => _relayConnectionState;
        private set => SetProperty(ref _relayConnectionState, value);
    }

    public string RelayConnectionMessage
    {
        get => _relayConnectionMessage;
        private set => SetProperty(ref _relayConnectionMessage, value);
    }

    public string LocalRelayState
    {
        get => _localRelayState;
        private set => SetProperty(ref _localRelayState, value);
    }

    public string LocalRelayMessage
    {
        get => _localRelayMessage;
        private set => SetProperty(ref _localRelayMessage, value);
    }

    public string TunnelState
    {
        get => _tunnelState;
        private set => SetProperty(ref _tunnelState, value);
    }

    public string TunnelMessage
    {
        get => _tunnelMessage;
        private set => SetProperty(ref _tunnelMessage, value);
    }

    /// <summary>Short label for the top bar (hostname when tunnel is running).</summary>
    public string TunnelBarText
    {
        get => _tunnelBarText;
        private set => SetProperty(ref _tunnelBarText, value);
    }

    #endregion

    #region Settings

    public string DeviceId
    {
        get => _deviceId;
        set => SetProperty(ref _deviceId, value);
    }

    public string TunnelModeSummary =>
        "Tailscale Funnel exposes your relay at a stable https://<device>.<tailnet>.ts.net URL.";

    public string GptApiToken
    {
        get => _gptApiToken;
        set
        {
            if (SetProperty(ref _gptApiToken, value))
            {
                NotifySchemaChanged();
            }
        }
    }

    public string IntegrationMode
    {
        get => _integrationMode;
        set
        {
            var normalized = string.Equals(value, "legacy_action", StringComparison.Ordinal)
                ? "legacy_action"
                : "mcp";
            if (SetProperty(ref _integrationMode, normalized))
            {
                NotifyIntegrationModeChanged();
                if (!_isLoadingState)
                {
                    PersistState();
                }
            }
        }
    }

    public bool IsMcpIntegrationSelected => string.Equals(IntegrationMode, "mcp", StringComparison.Ordinal);

    public bool IsLegacyIntegrationSelected => string.Equals(IntegrationMode, "legacy_action", StringComparison.Ordinal);

    public string StepCreateLabel => IsMcpIntegrationSelected ? "Create App (Beta)" : "Create GPT";

    public string StepConfigureLabel => IsMcpIntegrationSelected ? "Connect App (Beta)" : "Add Action";

    public string IntegrationModeTitle => IsMcpIntegrationSelected
        ? "ChatGPT App Beta (Apps SDK / MCP)"
        : "Custom GPT Action (Recommended)";

    public string IntegrationModeSummary => IsMcpIntegrationSelected
        ? "Beta. Use this only if you specifically want access to the Pro model through ChatGPT's Apps SDK connector flow. Otherwise, use the recommended Custom GPT Action."
        : "Recommended. Uses a Custom GPT Action with the OpenAPI schema and the same companion approvals.";

    public bool RequireApprovalForWrites
    {
        get => _requireApprovalForWrites;
        set
        {
            if (SetProperty(ref _requireApprovalForWrites, value) && !_isLoadingState)
            {
                PersistState();
            }
        }
    }

    public bool MultithreadedFileSearches
    {
        get => _multithreadedFileSearches;
        set
        {
            if (SetProperty(ref _multithreadedFileSearches, value) && !_isLoadingState)
            {
                PersistState();
            }
        }
    }

    public bool IsDarkMode
    {
        get => _isDarkMode;
        set
        {
            if (SetProperty(ref _isDarkMode, value))
            {
                ApplyTheme(value);
                OnPropertyChanged(nameof(CurrentStep));
                if (!_isLoadingState)
                {
                    PersistState();
                }
            }
        }
    }

    public bool ImportCodexCliWorkspaces
    {
        get => _importCodexCliWorkspaces;
        set
        {
            if (SetProperty(ref _importCodexCliWorkspaces, value))
            {
                if (value)
                {
                    ImportCodexCliWorkspacesNow();
                }

                if (!_isLoadingState)
                {
                    PersistState();
                }
            }
        }
    }

    public bool ImportCodexCliSkills
    {
        get => _importCodexCliSkills;
        set
        {
            if (SetProperty(ref _importCodexCliSkills, value))
            {
                if (value)
                {
                    ImportCodexCliSkillsNow();
                }

                if (!_isLoadingState)
                {
                    PersistState();
                }
            }
        }
    }

    public string CurrentWorkspace
    {
        get => _currentWorkspace;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : _pathPolicy.NormalizeWorkspaceRoot(value);
            if (SetProperty(ref _currentWorkspace, normalized))
            {
                if (!_isLoadingState)
                {
                    PersistState();
                }
            }
        }
    }

    public IReadOnlyList<string> CurrentWorkspaceChoices => TrustedWorkspaces.ToList();

    #endregion

    #region Computed content

    public string MinifiedOpenApiSchema
    {
        get
        {
            var url = ResolveSchemaRelayUrl();
            var json = _relayContentService.GetOpenApiJson(url);
            try
            {
                var obj = JsonSerializer.Deserialize<JsonElement>(json, JsonDefaults.Storage);
                return JsonSerializer.Serialize(obj, JsonDefaults.Transport);
            }
            catch
            {
                return json;
            }
        }
    }

    public string McpEndpointUrl
    {
        get
        {
            var baseUrl = ResolveSchemaRelayUrl();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = string.IsNullOrWhiteSpace(_relayUrl)
                    ? $"http://localhost:{LocalRelayPort}"
                    : _relayUrl;
            }

            return $"{baseUrl.TrimEnd('/')}/mcp";
        }
    }

    public string GptInstructions => RelayContentService.GptInstructions;

    public string SystemPromptSummary =>
        "Includes Portable Codex workspace rules plus Codex-compatible shell sessions, patch, skill, stdin, permission, and image-viewing guidance.";

    public string ActionSchemaSummary =>
        "Recommended Custom GPT Action schema. ChatGPT App Beta users should use the MCP endpoint above only when they want Pro model access.";

    public string McpEndpointSummary =>
        "Beta ChatGPT App connector endpoint. Use it only if you specifically want access to the Pro model through the app flow; otherwise, use the recommended Custom GPT Action.";

    public string InstructionsCopiedLabel
    {
        get => _instructionsCopiedLabel;
        private set => SetProperty(ref _instructionsCopiedLabel, value);
    }

    public string SchemaCopiedLabel
    {
        get => _schemaCopiedLabel;
        private set => SetProperty(ref _schemaCopiedLabel, value);
    }

    public string McpEndpointCopiedLabel
    {
        get => _mcpEndpointCopiedLabel;
        private set => SetProperty(ref _mcpEndpointCopiedLabel, value);
    }

    public string TokenCopiedLabel
    {
        get => _tokenCopiedLabel;
        private set => SetProperty(ref _tokenCopiedLabel, value);
    }

    public string TunnelCopiedLabel
    {
        get => _tunnelCopiedLabel;
        private set => SetProperty(ref _tunnelCopiedLabel, value);
    }

    #endregion

    #region Activity

    public string LogFilter
    {
        get => _logFilter;
        set
        {
            if (SetProperty(ref _logFilter, value))
            {
                FilteredLogs.Refresh();
            }
        }
    }

    public ToolLogEntry? SelectedLog
    {
        get => _selectedLog;
        set
        {
            if (SetProperty(ref _selectedLog, value))
            {
                OnPropertyChanged(nameof(SelectedLogDetail));
            }
        }
    }

    public string SelectedLogDetail => SelectedLog?.Detail ?? "Select an entry to see details.";

    public bool HasWorkspaces => TrustedWorkspaces.Count > 0;

    public string ViewAllWorkspacesButtonText => $"View all ({TrustedWorkspaces.Count})";

    public bool HasSkills => Skills.Count > 0;

    public string ViewAllSkillsButtonText => $"View all skills ({Skills.Count})";

    public string SkillSummaryText => Skills.Count == 0
        ? "No skills imported yet. Add a skill folder or sync Codex CLI skills."
        : $"{Skills.Count} skill(s) available via /skill-name.";

    public bool HasActivityLogs => ActivityLogs.Count > 0;

    public bool IsActivityLogExpanded
    {
        get => _isActivityLogExpanded;
        private set
        {
            if (SetProperty(ref _isActivityLogExpanded, value))
            {
                OnPropertyChanged(nameof(ActivityLogMaxHeight));
                OnPropertyChanged(nameof(ActivityLogToggleText));
            }
        }
    }

    public double ActivityLogMaxHeight => IsActivityLogExpanded ? 1200 : 260;

    public string ActivityLogToggleText => IsActivityLogExpanded ? "Show less" : "View more";

    public bool IsFileSearchInProgress
    {
        get => _isFileSearchInProgress;
        private set => SetProperty(ref _isFileSearchInProgress, value);
    }

    public double FileSearchProgressValue
    {
        get => _fileSearchProgressValue;
        private set => SetProperty(ref _fileSearchProgressValue, value);
    }

    public string FileSearchProgressText
    {
        get => _fileSearchProgressText;
        private set => SetProperty(ref _fileSearchProgressText, value);
    }

    public string DiffMode
    {
        get => _diffMode;
        set => SetProperty(ref _diffMode, value);
    }

    public int DiffTurnCount
    {
        get => _diffTurnCount;
        set => SetProperty(ref _diffTurnCount, value);
    }

    public string DiffText
    {
        get => _diffText;
        private set => SetProperty(ref _diffText, value);
    }

    public string DiffStatus
    {
        get => _diffStatus;
        private set => SetProperty(ref _diffStatus, value);
    }

    public string ErrorBanner
    {
        get => _errorBanner;
        private set => SetProperty(ref _errorBanner, value);
    }

    #endregion

    #region Lifecycle

    public async Task InitializeAsync()
    {
        var isReturningUser = !string.IsNullOrWhiteSpace(GptApiToken);

        CredentialsReady = !string.IsNullOrWhiteSpace(GptApiToken) &&
                           !string.IsNullOrWhiteSpace(DeviceId);

        if (!CredentialsReady)
        {
            var creds = _credentialService.GenerateCredentialSet();
            _deviceId = creds.DeviceId;
            _deviceToken = creds.DeviceToken;
            _deviceName = creds.DeviceName;
            _gptApiToken = creds.GptApiToken;
            _relayUrl = $"http://localhost:{LocalRelayPort}";

            OnPropertyChanged(nameof(DeviceId));
            OnPropertyChanged(nameof(GptApiToken));
            NotifySchemaChanged();
            PersistState();
            CredentialsReady = true;
        }

        if (string.IsNullOrWhiteSpace(_relayUrl))
        {
            _relayUrl = $"http://localhost:{LocalRelayPort}";
        }

        await _localRelayService.StartAsync(BuildCurrentSettings());
        _relayClientService.Connect();
        await _tunnelService.RefreshStatusAsync();
        ApplyTunnelSettingsCommand.RaiseCanExecuteChanged();
        CurrentStep = ResolveInitialStep(isReturningUser);
    }

    public async Task ShutdownAsync()
    {
        await _relayClientService.DisconnectAsync();
        await _localRelayService.StopAsync();
        await _tunnelService.StopAsync();
        _tunnelService.Dispose();
    }

    #endregion

    #region Navigation handlers

    private async Task NextStepAsync()
    {
        if (CurrentStep == StepSetup)
        {
            PersistState();
        }

        if (CurrentStep == StepWorkspaces)
        {
            PersistState();
            // Drop the device WebSocket before stopping Kestrel; otherwise StopAsync can wait a long time
            // while graceful shutdown drains the still-open /ws/device connection.
            await _relayClientService.DisconnectAsync();
            await _localRelayService.RestartAsync(BuildCurrentSettings());
            _relayClientService.Connect();
        }

        if (CurrentStep < StepReady)
        {
            CurrentStep++;
        }
    }

    private void PrevStep()
    {
        if (CurrentStep > StepSetup)
        {
            CurrentStep--;
        }
    }

    #endregion

    #region Workspace management

    private void AddWorkspace()
    {
        ClearError();
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select a trusted workspace root",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var normalized = _pathPolicy.NormalizeWorkspaceRoot(dialog.SelectedPath);
        if (TrustedWorkspaces.Any(w => string.Equals(w, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        TrustedWorkspaces.Add(normalized);
        NormalizeTrustedWorkspaces();
        if (string.IsNullOrWhiteSpace(CurrentWorkspace))
        {
            CurrentWorkspace = normalized;
        }
        PersistState();
    }

    private void AddSkill()
    {
        ClearError();
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select a skill folder that contains SKILL.md",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var normalized = Path.GetFullPath(dialog.SelectedPath);
        if (!File.Exists(Path.Combine(normalized, "SKILL.md")))
        {
            SetError("That folder does not contain SKILL.md.");
            return;
        }

        if (SkillRoots.Any(w => string.Equals(w, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        SkillRoots.Add(normalized);
        NormalizeSkillRoots();
        PersistState();
    }

    private void RemoveSkill(string? skillPath)
    {
        if (string.IsNullOrWhiteSpace(skillPath))
        {
            return;
        }

        var existing = SkillRoots.FirstOrDefault(
            root => string.Equals(root, skillPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SkillRoots.Remove(existing);
            PersistState();
        }
    }

    private void ViewSkill(object? parameter)
    {
        ClearError();
        if (parameter is not SkillListEntry skill || string.IsNullOrWhiteSpace(skill.Path))
        {
            return;
        }

        var skillFile = Path.Combine(skill.Path, "SKILL.md");
        if (!File.Exists(skillFile))
        {
            SetError($"SKILL.md was not found for {skill.Activation}.");
            return;
        }

        string markdown;
        try
        {
            markdown = File.ReadAllText(skillFile);
        }
        catch (Exception ex)
        {
            SetError($"Could not read {skill.Activation}: {ex.Message}");
            return;
        }

        ShowSkillMarkdownWindow(skill, skillFile, markdown);
    }

    private static void ShowSkillMarkdownWindow(SkillListEntry skill, string skillFile, string markdown)
    {
        var header = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        };

        var title = new System.Windows.Controls.TextBlock
        {
            Text = skill.Activation,
            FontSize = 18,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        };
        title.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "PrimaryFg");

        var path = new System.Windows.Controls.TextBlock
        {
            Text = skillFile,
            FontSize = 11,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas"),
            TextWrapping = System.Windows.TextWrapping.Wrap,
        };
        path.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TertiaryFg");

        header.Children.Add(title);
        header.Children.Add(path);

        var markdownBox = new System.Windows.Controls.TextBox
        {
            Text = markdown,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas"),
            FontSize = 12,
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(14, 12, 14, 12),
        };
        markdownBox.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "CodeBg");
        markdownBox.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "CodeFg");
        markdownBox.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "SubtleBorder");

        var content = new System.Windows.Controls.DockPanel
        {
            LastChildFill = true,
            Margin = new System.Windows.Thickness(18),
        };
        System.Windows.Controls.DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        content.Children.Add(header);
        content.Children.Add(markdownBox);

        var window = new System.Windows.Window
        {
            Title = $"{skill.Activation} SKILL.md",
            Width = 840,
            Height = 640,
            MinWidth = 520,
            MinHeight = 360,
            Owner = WpfApp.Current.MainWindow,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Content = content,
        };
        window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AppBg");
        window.ShowDialog();
    }

    private void ImportCodexCliWorkspacesNow()
    {
        ClearError();
        var added = false;
        try
        {
            foreach (var workspace in _codexCliWorkspaceService.GetWorkspaceRoots())
            {
                var normalized = _pathPolicy.NormalizeWorkspaceRoot(workspace);
                if (TrustedWorkspaces.Any(w => string.Equals(w, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                TrustedWorkspaces.Add(normalized);
                added = true;
            }
        }
        catch (Exception ex)
        {
            SetError($"Could not import Codex CLI workspaces: {ex.Message}");
            return;
        }

        if (added)
        {
            NormalizeTrustedWorkspaces();
        }

        if (string.IsNullOrWhiteSpace(CurrentWorkspace) && TrustedWorkspaces.Count > 0)
        {
            CurrentWorkspace = TrustedWorkspaces[0];
        }
    }

    private void ImportCodexCliSkillsNow()
    {
        ClearError();
        var added = false;
        try
        {
            foreach (var skillRoot in _codexCliWorkspaceService.GetSkillRoots())
            {
                var normalized = Path.GetFullPath(skillRoot);
                if (SkillRoots.Any(w => string.Equals(w, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                SkillRoots.Add(normalized);
                added = true;
            }
        }
        catch (Exception ex)
        {
            SetError($"Could not import Codex CLI skills: {ex.Message}");
            return;
        }

        if (added)
        {
            NormalizeSkillRoots();
        }
    }

    private void TrustWholeSystem()
    {
        ClearError();
        var result = WpfMessageBox.Show(
            "This will trust every ready drive root, such as C:\\, so Codex can access files anywhere under those drives. Write approval still applies when enabled. Continue?",
            "Allow full system file access",
            WpfMessageBoxButton.YesNo,
            WpfMessageBoxImage.Warning);
        if (result != WpfMessageBoxResult.Yes)
        {
            return;
        }

        foreach (var root in GetSystemRoots())
        {
            var normalized = _pathPolicy.NormalizeWorkspaceRoot(root);
            if (TrustedWorkspaces.Any(w => string.Equals(w, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            TrustedWorkspaces.Add(normalized);
        }

        NormalizeTrustedWorkspaces();
        PersistState();
    }

    private static IEnumerable<string> GetSystemRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return "/";
            yield break;
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }

    private void RemoveWorkspace(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return;
        }

        var existing = TrustedWorkspaces.FirstOrDefault(
            w => string.Equals(w, workspace, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            TrustedWorkspaces.Remove(existing);
            if (string.Equals(CurrentWorkspace, existing, StringComparison.OrdinalIgnoreCase))
            {
                CurrentWorkspace = TrustedWorkspaces.FirstOrDefault() ?? string.Empty;
            }
            PersistState();
        }
    }

    private void ViewAllWorkspaces()
    {
        var message = TrustedWorkspaces.Count == 0
            ? "No workspaces added."
            : string.Join(Environment.NewLine, TrustedWorkspaces);

        WpfMessageBox.Show(
            message,
            "Trusted workspaces",
            WpfMessageBoxButton.OK,
            WpfMessageBoxImage.Information);
    }

    private void ViewAllSkills()
    {
        var message = Skills.Count == 0
            ? "No skills added."
            : string.Join(Environment.NewLine, Skills.Select(skill => $"{skill.Activation} — {skill.Path}"));

        WpfMessageBox.Show(
            message,
            "Skills",
            WpfMessageBoxButton.OK,
            WpfMessageBoxImage.Information);
    }

    #endregion

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

    #region Clipboard helpers

    private void CopySchemaWithFeedback()
    {
        CopyText(MinifiedOpenApiSchema);
        SchemaCopiedLabel = "Copied!";
        ResetLabelAfterDelay(v => SchemaCopiedLabel = v, "Copy");
    }

    private void CopyMcpEndpointWithFeedback()
    {
        CopyText(McpEndpointUrl);
        McpEndpointCopiedLabel = "Copied!";
        ResetLabelAfterDelay(v => McpEndpointCopiedLabel = v, "Copy");
    }

    private void CopyInstructionsWithFeedback()
    {
        CopyText(RelayContentService.GptInstructions);
        InstructionsCopiedLabel = "Copied!";
        ResetLabelAfterDelay(v => InstructionsCopiedLabel = v, "Copy prompt");
    }

    private void CopyTokenWithFeedback()
    {
        CopyText(GptApiToken);
        TokenCopiedLabel = "Copied!";
        ResetLabelAfterDelay(v => TokenCopiedLabel = v, "Copy");
    }

    private void CopyTunnelWithFeedback()
    {
        CopyText(TunnelUrl);
        TunnelCopiedLabel = "Copied!";
        ResetLabelAfterDelay(v => TunnelCopiedLabel = v, "Copy");
    }

    private void SelectIntegrationMode(string? mode)
    {
        IntegrationMode = mode ?? "mcp";
    }

    private void CopyText(string? value)
    {
        ClearError();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            WpfClipboard.SetText(value);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private static void ResetLabelAfterDelay(Action<string> setter, string resetValue)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            await RunOnUiThreadAsync(() => setter(resetValue));
        });
    }

    #endregion

    #region Helpers

    private async Task ApplyTunnelSettingsAsync()
    {
        ClearError();

        if (!RelayReady)
        {
            await _localRelayService.RestartAsync(BuildCurrentSettings());
            _relayClientService.Connect();
            if (!RelayReady)
            {
                SetError(string.IsNullOrWhiteSpace(LocalRelayMessage)
                    ? "Local relay is not ready yet. Wait a moment and try again."
                    : LocalRelayMessage);
                return;
            }
        }

        PersistState();
        if (_tunnelService.IsRunning || !string.IsNullOrWhiteSpace(TunnelUrl))
        {
            await _tunnelService.StopAsync();
        }

        await _tunnelService.StartAsync(BuildTunnelLaunchOptions());
        NotifySchemaChanged();
        RefreshTunnelBarText();
        if (!TunnelReady && !string.IsNullOrWhiteSpace(TunnelMessage))
        {
            SetError(TunnelMessage);
        }
    }

    private async Task InstallTailscaleAsync()
    {
        ClearError();
        await _tunnelService.InstallTailscaleAsync();
    }

    private static void OpenGptBuilder()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://chatgpt.com",
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Best effort.
        }
    }

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
            _requireApprovalForWrites = settings.RequireApprovalForWrites;
            _multithreadedFileSearches = settings.MultithreadedFileSearches;
            _isDarkMode = settings.IsDarkMode;

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
            RequireApprovalForWrites = RequireApprovalForWrites,
            MultithreadedFileSearches = MultithreadedFileSearches,
            IsDarkMode = IsDarkMode,
        };
    }

    private static void ApplyRequestDefaults(ToolRequest request, CompanionSettings settings)
    {
        if (string.Equals(request.Tool, "search_files", StringComparison.Ordinal) && request.Multithreaded is null)
        {
            request.Multithreaded = settings.MultithreadedFileSearches;
        }
    }

    private static void ApplyTheme(bool isDarkMode)
    {
        try
        {
            ApplicationThemeManager.Apply(
                isDarkMode ? ApplicationTheme.Dark : ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.Mica,
                true);
        }
        catch
        {
            // Best effort: keep the app usable if WPF UI theme switching fails.
        }

        SetBrush("AppBg", isDarkMode ? "#0F172A" : "#F8FAFC");
        SetBrush("CardBg", isDarkMode ? "#111827" : "#FFFFFF");
        SetBrush("CardMutedBg", isDarkMode ? "#1E293B" : "#F9FAFB");
        SetBrush("HoverBg", isDarkMode ? "#1F2937" : "#F3F4F6");
        SetBrush("SelectedBg", isDarkMode ? "#172554" : "#EFF6FF");
        SetBrush("SelectedBorder", isDarkMode ? "#2563EB" : "#BFDBFE");
        SetBrush("PrimaryFg", isDarkMode ? "#F8FAFC" : "#111827");
        SetBrush("SecondaryFg", isDarkMode ? "#CBD5E1" : "#374151");
        SetBrush("MutedFg", isDarkMode ? "#94A3B8" : "#6B7280");
        SetBrush("TertiaryFg", isDarkMode ? "#64748B" : "#9CA3AF");
        SetBrush("SubtleBorder", isDarkMode ? "#334155" : "#E5E7EB");
        SetBrush("SoftBorder", isDarkMode ? "#1E293B" : "#F3F4F6");
        SetBrush("SurfaceBg", isDarkMode ? "#0F172A" : "#F9FAFB");
        SetBrush("CodeBg", isDarkMode ? "#020617" : "#F4F4F5");
        SetBrush("CodeFg", isDarkMode ? "#E5E7EB" : "#1F2937");
        SetBrush("SuccessSubtleBg", isDarkMode ? "#064E3B" : "#ECFDF5");
        SetBrush("SuccessSubtleBorder", isDarkMode ? "#059669" : "#A7F3D0");
        SetBrush("SuccessSubtleFg", isDarkMode ? "#A7F3D0" : "#047857");
        SetBrush("PendingSubtleBg", isDarkMode ? "#1F2937" : "#F9FAFB");
        SetBrush("WarningSubtleBg", isDarkMode ? "#451A03" : "#FFFBEB");
        SetBrush("WarningSubtleBorder", isDarkMode ? "#B45309" : "#FDE68A");
        SetBrush("WarningSubtleFg", isDarkMode ? "#FDE68A" : "#92400E");
        SetBrush("ErrorSubtleBg", isDarkMode ? "#450A0A" : "#FEF2F2");
        SetBrush("ErrorSubtleBorder", isDarkMode ? "#991B1B" : "#FECACA");
        SetBrush("ErrorSubtleFg", isDarkMode ? "#FCA5A5" : "#B91C1C");
    }

    private static void SetBrush(string key, string color)
    {
        if (WpfApp.Current?.Resources is not { } resources)
        {
            return;
        }

        var parsedColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
        if (resources[key] is System.Windows.Media.SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = parsedColor;
            return;
        }

        resources[key] = new System.Windows.Media.SolidColorBrush(parsedColor);
    }

    private void NormalizeTrustedWorkspaces()
    {
        var normalized = TrustedWorkspaces
            .Select(_pathPolicy.NormalizeWorkspaceRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        TrustedWorkspaces.Clear();
        foreach (var workspace in normalized)
        {
            TrustedWorkspaces.Add(workspace);
        }
    }

    private void NormalizeSkillRoots()
    {
        var normalized = SkillRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SkillRoots.Clear();
        foreach (var skillRoot in normalized)
        {
            SkillRoots.Add(skillRoot);
        }
    }

    private void RefreshSkillList()
    {
        Skills.Clear();
        foreach (var skill in _skillService.GetSkillList(BuildCurrentSettings()))
        {
            Skills.Add(skill);
        }

        OnPropertyChanged(nameof(HasSkills));
        OnPropertyChanged(nameof(ViewAllSkillsButtonText));
        OnPropertyChanged(nameof(SkillSummaryText));
    }

    private int GetLocalRelayPort()
    {
        if (Uri.TryCreate(_relayUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
        {
            return uri.Port;
        }

        return LocalRelayPort;
    }

    private TunnelLaunchOptions BuildTunnelLaunchOptions()
    {
        return new TunnelLaunchOptions
        {
            Mode = _tunnelMode,
            LocalPort = GetLocalRelayPort(),
            NamedTunnelName = string.Empty,
            NamedTunnelHostname = string.Empty,
        };
    }

    private void NotifySchemaChanged()
    {
        OnPropertyChanged(nameof(MinifiedOpenApiSchema));
        OnPropertyChanged(nameof(McpEndpointUrl));
        OnPropertyChanged(nameof(GptInstructions));
        OnPropertyChanged(nameof(SystemPromptSummary));
        OnPropertyChanged(nameof(ActionSchemaSummary));
        OnPropertyChanged(nameof(McpEndpointSummary));
    }

    private void NotifyIntegrationModeChanged()
    {
        OnPropertyChanged(nameof(IntegrationMode));
        OnPropertyChanged(nameof(IsMcpIntegrationSelected));
        OnPropertyChanged(nameof(IsLegacyIntegrationSelected));
        OnPropertyChanged(nameof(StepCreateLabel));
        OnPropertyChanged(nameof(StepConfigureLabel));
        OnPropertyChanged(nameof(IntegrationModeTitle));
        OnPropertyChanged(nameof(IntegrationModeSummary));
        NotifySchemaChanged();
    }

    private string? ResolveSchemaRelayUrl()
    {
        if (!string.IsNullOrWhiteSpace(TunnelUrl))
        {
            return TunnelUrl;
        }

        if (Uri.TryCreate(_relayUrl, UriKind.Absolute, out var uri) &&
            !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase))
        {
            return _relayUrl;
        }

        return null;
    }

    private void RaiseNavigationChanged()
    {
        OnPropertyChanged(nameof(CanGoNext));
        NextStepCommand.RaiseCanExecuteChanged();
        ApplyTunnelSettingsCommand.RaiseCanExecuteChanged();
    }

    private void TryAutoAdvanceFromSetupAfterTunnelReady()
    {
        if (CurrentStep != StepSetup || !CredentialsReady || !RelayReady || !TunnelReady)
        {
            return;
        }

        PersistState();
        CurrentStep = StepCreateGpt;
    }

    private int ResolveInitialStep(bool isReturningUser)
    {
        if (!CredentialsReady || !RelayReady)
        {
            return StepSetup;
        }

        if (!isReturningUser)
        {
            return CurrentStep;
        }

        return TrustedWorkspaces.Count == 0 ? StepWorkspaces : StepReady;
    }

    private void SyncCurrentStepWithState()
    {
        if (_isLoadingState)
        {
            return;
        }

        if (TrustedWorkspaces.Count == 0 && (CurrentStep == StepReady || CurrentStep == StepDiffViewer))
        {
            CurrentStep = StepWorkspaces;
        }
    }

    private bool FilterLogs(object obj)
    {
        if (obj is not ToolLogEntry entry)
        {
            return false;
        }

        return string.Equals(LogFilter, "all", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(entry.Status, LogFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void ToggleActivityLog()
    {
        IsActivityLogExpanded = !IsActivityLogExpanded;
    }

    private void OnToolProgress(ToolProgress progress)
    {
        if (!string.Equals(progress.Tool, "search_files", StringComparison.Ordinal))
        {
            return;
        }

        _ = RunOnUiThreadAsync(() =>
        {
            IsFileSearchInProgress = progress.Percent < 100;
            FileSearchProgressValue = progress.Percent;
            FileSearchProgressText = string.IsNullOrWhiteSpace(progress.Message)
                ? $"Searching files… {progress.Percent:0}%"
                : $"{progress.Message} ({progress.Percent:0}%)";
        });
    }

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

    private void SetError(string message) => ErrorBanner = message;

    private void ClearError() => ErrorBanner = string.Empty;

    private void RefreshTunnelBarText()
    {
        TunnelBarText = FormatTunnelBarText(TunnelState, TunnelMessage, TunnelUrl);
    }

    private static string FormatTunnelBarText(string state, string message, string tunnelUrl)
    {
        if (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase))
        {
            var href = !string.IsNullOrWhiteSpace(tunnelUrl) ? tunnelUrl : message;
            if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
            {
                return uri.Host;
            }

            return "Funnel live";
        }

        return state.ToLowerInvariant() switch
        {
            "starting" => "Starting Funnel…",
            "approval_required" => "Awaiting Funnel approval",
            "login_required" => "Tailscale sign-in required",
            "not_found" => "Tailscale not installed",
            "stopped" => "No public Funnel URL",
            "error" => ShortenForStatusBar(message),
            _ => string.IsNullOrWhiteSpace(message) ? "Funnel" : ShortenForStatusBar(message),
        };
    }

    private static string ShortenForStatusBar(string? text, int max = 44)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)] + "…";
    }

    private static Task RunOnUiThreadAsync(Action action)
    {
        if (WpfApp.Current.Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return WpfApp.Current.Dispatcher.InvokeAsync(action).Task;
    }

    private static Task<T> RunOnUiThreadAsync<T>(Func<T> action)
    {
        if (WpfApp.Current.Dispatcher.CheckAccess())
        {
            return Task.FromResult(action());
        }

        return WpfApp.Current.Dispatcher.InvokeAsync(action).Task;
    }

    #endregion
}
