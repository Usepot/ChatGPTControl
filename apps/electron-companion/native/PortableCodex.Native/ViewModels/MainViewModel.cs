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

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogEntries = 250;
    private const int LocalRelayPort = 8787;

    public const int StepSetup = 0;
    public const int StepCreateGpt = 1;
    public const int StepConfigureAction = 2;
    public const int StepWorkspaces = 3;
    public const int StepReady = 4;
    public const int StepDiffViewer = 5;
    // StepChatGpt = 6 lives in MainViewModel.ChatGpt.cs to keep the partial cohesive.

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
    private bool _requireApprovalForWrites;
    private bool _multithreadedFileSearches;
    private bool _isDarkMode = true;
    private bool _setupCompleted;
    private string _chatGptPinnedUrl = ChatGptProjectViewModel.DefaultProjectUrl;
    private bool _isNormalizingTrustedWorkspaces;

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
        InitializeChatGptCommands();

        FilteredLogs = CollectionViewSource.GetDefaultView(ActivityLogs);
        FilteredLogs.Filter = FilterLogs;
        TrustedWorkspaces.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasWorkspaces));
            OnPropertyChanged(nameof(ViewAllWorkspacesButtonText));
            OnPropertyChanged(nameof(CurrentWorkspaceChoices));
            if (_isNormalizingTrustedWorkspaces)
            {
                return;
            }

            if (!_isLoadingState &&
                !string.IsNullOrWhiteSpace(CurrentWorkspace) &&
                !TrustedWorkspaces.Any(w => string.Equals(w, CurrentWorkspace, StringComparison.OrdinalIgnoreCase)))
            {
                CurrentWorkspace = string.Empty;
            }

            SyncChatGptProjectsWithWorkspaces();
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
        RefreshWizardSteps();
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
                RefreshWizardSteps();
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
        StepDiffViewer => false,
        StepChatGpt => false,
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
                OnPropertyChanged(nameof(ShowTunnelRecovery));
                OnPropertyChanged(nameof(NextButtonText));
                TryAutoAdvanceFromSetupAfterTunnelReady();
            }
        }
    }

    public bool ShowTunnelRecovery => !TunnelReady;

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
        private set
        {
            if (SetProperty(ref _tunnelState, value))
            {
                OnPropertyChanged(nameof(TunnelReconnectButtonText));
            }
        }
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

    public string TunnelReconnectButtonText => TunnelState.ToLowerInvariant() switch
    {
        "approval_required" => "Resume Funnel approval",
        "login_required" => "Connect to Tailscale",
        "not_found" => "Check Tailscale",
        "starting" => "Starting Funnel…",
        "error" => "Try Funnel again",
        _ => "Turn Funnel back on",
    };

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





}
