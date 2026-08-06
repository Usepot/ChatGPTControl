using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using PortableCodex.Core.Services;
using PortableCodex.Native.Models;

namespace PortableCodex.Desktop.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly CompanionRuntime _runtime;
    private readonly IProcessLauncher _processLauncher = new DefaultProcessLauncher();
    private string _currentWorkspace = string.Empty;
    private string _localRelayState = "stopped";
    private string _localRelayMessage = "Local relay is stopped";
    private string _tunnelState = "stopped";
    private string _tunnelMessage = "Tailscale Funnel is not connected";
    private string _tunnelUrl = string.Empty;
    private string _banner = string.Empty;
    private ToolLogEntry? _selectedActivity;
    private bool _isBusy;
    private bool _initialized;
    private bool _setupCompleted;
    private int _setupStep;
    private bool _gptSetupAcknowledged;

    public MainWindowViewModel()
    {
        _runtime = new CompanionRuntime();
        _runtime.StatusChanged += OnStatusChanged;
        _runtime.LogChanged += OnLogChanged;
        _runtime.ApprovalRequested += OnApprovalRequested;

        _currentWorkspace = _runtime.Settings.CurrentWorkspace;
        _setupCompleted = _runtime.Settings.SetupCompleted;
        _setupStep = _setupCompleted ? 4 : (_runtime.Settings.TrustedWorkspaces.Count > 0 ? 2 : 1);
        _gptSetupAcknowledged = _setupCompleted;
        foreach (var workspace in _runtime.Settings.TrustedWorkspaces)
        {
            Workspaces.Add(workspace);
        }

        foreach (var log in _runtime.Logs)
        {
            Activity.Insert(0, log);
        }

        RefreshSkills();
        SelectedActivity = Activity.FirstOrDefault();

        ConnectTailscaleCommand = new AsyncCommand(ConnectTailscaleAsync, () => !IsBusy);
        InstallTailscaleCommand = new AsyncCommand(InstallTailscaleAsync, () => !IsBusy);
        StopTailscaleCommand = new AsyncCommand(StopTailscaleAsync, () => !IsBusy && _runtime.IsTunnelRunning);
        AddWorkspaceCommand = new SimpleCommand(() => WorkspacePickerRequested?.Invoke());
        RemoveWorkspaceCommand = new SimpleCommand(parameter =>
        {
            if (parameter is string path)
            {
                _runtime.RemoveWorkspace(path);
                Workspaces.Remove(path);
                CurrentWorkspace = _runtime.Settings.CurrentWorkspace;
                OnPropertyChanged(nameof(HasWorkspaces));
                OnPropertyChanged(nameof(HasNoWorkspaces));
                OnPropertyChanged(nameof(CanGoNext));
            }
        });
        RefreshSkillsCommand = new SimpleCommand(RefreshSkills);
        CopyDeviceIdCommand = new SimpleCommand(() => CopyRequested?.Invoke(DeviceId));
        CopyDeviceTokenCommand = new SimpleCommand(() => CopyRequested?.Invoke(DeviceToken));
        FullAccessCommand = new AsyncCommand(GrantFullAccessAsync, () => !IsBusy);
        NextSetupCommand = new SimpleCommand(AdvanceSetup);
        BackSetupCommand = new SimpleCommand(() => SetupStep--);
        RestartSetupCommand = new SimpleCommand(RestartSetup);
        AcknowledgeGptSetupCommand = new SimpleCommand(AcknowledgeGptSetup);
        OpenGptBuilderCommand = new SimpleCommand(OpenGptBuilder);
        CopyGptInstructionsCommand = new SimpleCommand(() => CopyRequested?.Invoke(GptInstructions));
        CopyGptSchemaCommand = new SimpleCommand(() => CopyRequested?.Invoke(CustomGptSchema));
        CopyGptTokenCommand = new SimpleCommand(() => CopyRequested?.Invoke(GptApiToken));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? WorkspacePickerRequested;
    public event Action<string>? CopyRequested;
    public event Func<ToolRequest, string, Task<bool>>? ApprovalDialogRequested;
    public event Func<Task<bool>>? FullAccessRequested;

    public ObservableCollection<string> Workspaces { get; } = [];
    public ObservableCollection<ToolLogEntry> Activity { get; } = [];
    public ObservableCollection<SkillListEntry> Skills { get; } = [];

    public ICommand ConnectTailscaleCommand { get; }
    public ICommand InstallTailscaleCommand { get; }
    public ICommand StopTailscaleCommand { get; }
    public ICommand AddWorkspaceCommand { get; }
    public ICommand RemoveWorkspaceCommand { get; }
    public ICommand RefreshSkillsCommand { get; }
    public ICommand CopyDeviceIdCommand { get; }
    public ICommand CopyDeviceTokenCommand { get; }
    public ICommand FullAccessCommand { get; }
    public ICommand NextSetupCommand { get; }
    public ICommand BackSetupCommand { get; }
    public ICommand RestartSetupCommand { get; }
    public ICommand AcknowledgeGptSetupCommand { get; }
    public ICommand OpenGptBuilderCommand { get; }
    public ICommand CopyGptInstructionsCommand { get; }
    public ICommand CopyGptSchemaCommand { get; }
    public ICommand CopyGptTokenCommand { get; }

    public string CurrentWorkspace
    {
        get => _currentWorkspace;
        set
        {
            if (!SetProperty(ref _currentWorkspace, value))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    _runtime.SetCurrentWorkspace(value);
                }
                catch (Exception exception)
                {
                    Banner = exception.Message;
                }
            }
        }
    }

    public bool RequireApproval
    {
        get => _runtime.Settings.RequireApprovalForWrites;
        set
        {
            if (value == _runtime.Settings.RequireApprovalForWrites)
            {
                return;
            }

            _runtime.SetRequireApproval(value);
            OnPropertyChanged();
        }
    }

    public string DeviceId => _runtime.Settings.DeviceId;
    public string DeviceToken => _runtime.Settings.DeviceToken;
    public string DeviceName => string.IsNullOrWhiteSpace(_runtime.Settings.DeviceName) ? "This device" : _runtime.Settings.DeviceName;
    public string DataDirectory => _runtime.Paths.DataDirectory;

    public string LocalRelayState { get => _localRelayState; private set => SetProperty(ref _localRelayState, value); }
    public string LocalRelayMessage { get => _localRelayMessage; private set => SetProperty(ref _localRelayMessage, value); }
    public string TunnelState { get => _tunnelState; private set => SetProperty(ref _tunnelState, value); }
    public string TunnelMessage { get => _tunnelMessage; private set => SetProperty(ref _tunnelMessage, value); }
    public string TunnelUrl { get => _tunnelUrl; private set => SetProperty(ref _tunnelUrl, value); }
    public string Banner
    {
        get => _banner;
        private set
        {
            if (SetProperty(ref _banner, value))
            {
                OnPropertyChanged(nameof(HasBanner));
            }
        }
    }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool SetupCompleted
    {
        get => _setupCompleted;
        private set
        {
            if (SetProperty(ref _setupCompleted, value))
            {
                OnPropertyChanged(nameof(IsWizardVisible));
                OnPropertyChanged(nameof(IsDashboardVisible));
                OnPropertyChanged(nameof(SetupHeadline));
                OnPropertyChanged(nameof(SetupMessage));
            }
        }
    }
    public bool IsWizardVisible => !SetupCompleted;
    public bool IsDashboardVisible => SetupCompleted;
    public int SetupStep
    {
        get => _setupStep;
        private set
        {
            var clamped = Math.Clamp(value, 1, 4);
            if (SetProperty(ref _setupStep, clamped))
            {
                OnPropertyChanged(nameof(IsAccessStep));
                OnPropertyChanged(nameof(IsTunnelStep));
                OnPropertyChanged(nameof(IsGptStep));
                OnPropertyChanged(nameof(IsReadyStep));
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(SetupProgress));
                OnPropertyChanged(nameof(SetupStepTitle));
                OnPropertyChanged(nameof(SetupStepDescription));
                OnPropertyChanged(nameof(SetupContinueLabel));
            }
        }
    }
    public bool IsAccessStep => SetupStep == 1;
    public bool IsTunnelStep => SetupStep == 2;
    public bool IsGptStep => SetupStep == 3;
    public bool IsReadyStep => SetupStep == 4;
    public bool CanGoBack => SetupStep > 1;
    public bool CanGoNext => SetupStep switch
    {
        1 => HasWorkspaces,
        3 => GptSetupAcknowledged,
        _ => true,
    };
    public string SetupProgress => $"Step {SetupStep} of 4";
    public string SetupContinueLabel => IsReadyStep ? "Start using bridge" : "Continue";
    public string SetupStepTitle => SetupStep switch
    {
        1 => "Choose the file access boundary",
        2 => "Connect this machine",
        3 => "Set up your Custom GPT",
        _ => "Your bridge is ready",
    };
    public string SetupStepDescription => SetupStep switch
    {
        1 => "Decide where incoming requests are allowed to work. You can grant the whole machine or keep them inside one folder.",
        2 => "Tailscale Funnel gives your hosted connector a secure path to this local bridge. You can finish this later if you are only testing locally.",
        3 => "Open the GPT editor, click Configure, and follow the guided instructions to add the prompt, Code Interpreter capability, Action schema, and bearer authentication.",
        _ => "The local relay is running in the background. Keep this window minimized; it will ask when a request needs your attention.",
    };
    public bool GptSetupAcknowledged
    {
        get => _gptSetupAcknowledged;
        private set
        {
            if (SetProperty(ref _gptSetupAcknowledged, value))
            {
                OnPropertyChanged(nameof(CanGoNext));
            }
        }
    }
    public string GptApiToken => _runtime.Settings.GptApiToken;
    public string GptInstructions => _runtime.GetGptInstructions();
    public string CustomGptActionUrl => HasCustomGptUrl
        ? TunnelUrl.TrimEnd('/')
        : "Connect Tailscale to generate the secure Action URL.";
    public string CustomGptSchema => _runtime.GetOpenApiJson(HasCustomGptUrl ? TunnelUrl : null);
    public bool HasCustomGptUrl => !string.IsNullOrWhiteSpace(TunnelUrl);
    public string GptSetupActionLabel => HasCustomGptUrl ? "I’ve saved the GPT" : "I’ll finish this later";
    public bool HasWorkspaces => Workspaces.Count > 0;
    public bool HasNoWorkspaces => !HasWorkspaces;
    public bool HasActivity => Activity.Count > 0;
    public bool HasNoActivity => !HasActivity;
    public bool HasSkills => Skills.Count > 0;
    public bool HasNoSkills => !HasSkills;
    public ToolLogEntry? SelectedActivity
    {
        get => _selectedActivity;
        private set
        {
            if (SetProperty(ref _selectedActivity, value))
            {
                OnPropertyChanged(nameof(HasSelectedActivity));
                OnPropertyChanged(nameof(SelectedActivityDetail));
            }
        }
    }
    public bool HasSelectedActivity => SelectedActivity is not null;
    public string SelectedActivityDetail => SelectedActivity?.Detail ?? "The latest request has no additional output.";
    public bool HasBanner => !string.IsNullOrWhiteSpace(Banner);
    public string SetupHeadline => SetupCompleted
        ? "Your local bridge is ready."
        : "Let’s get your local bridge ready.";
    public string SetupMessage => SetupCompleted
        ? "The companion can stay in the background. It will surface approvals and connection problems when they need your attention."
        : "A few quick choices will set the access boundary and the connection this machine uses.";

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await _runtime.InitializeAsync();
            SyncWorkspacesFromRuntime();
            if (!SetupCompleted && SetupStep == 1 && HasWorkspaces)
            {
                SetupStep = 2;
            }
            _initialized = true;
            Banner = "Local bridge started. You can leave this window open or minimize it.";
        }
        catch (Exception exception)
        {
            Banner = $"Could not start the local bridge: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    public void AddWorkspace(string path)
    {
        try
        {
            _runtime.AddWorkspace(path);
            SyncWorkspacesFromRuntime();
            CurrentWorkspace = _runtime.Settings.CurrentWorkspace;
            Banner = "Workspace trusted. Requests will stay inside this folder.";
        }
        catch (Exception exception)
        {
            Banner = exception.Message;
        }
    }

    private void SyncWorkspacesFromRuntime()
    {
        Workspaces.Clear();
        foreach (var workspace in _runtime.Settings.TrustedWorkspaces)
        {
            Workspaces.Add(workspace);
        }

        OnPropertyChanged(nameof(HasWorkspaces));
        OnPropertyChanged(nameof(HasNoWorkspaces));
        OnPropertyChanged(nameof(CanGoNext));
    }

    private void AdvanceSetup()
    {
        if (SetupStep == 1 && !HasWorkspaces)
        {
            Banner = "Choose a workspace or allow full access before continuing.";
            return;
        }

        if (SetupStep < 4)
        {
            SetupStep++;
            return;
        }

        _runtime.SetSetupCompleted(true);
        SetupCompleted = true;
        Banner = "Setup complete. The bridge is running in the background.";
    }

    private void RestartSetup()
    {
        _runtime.SetSetupCompleted(false);
        SetupCompleted = false;
        SetupStep = HasWorkspaces ? 2 : 1;
        GptSetupAcknowledged = false;
        Banner = "Setup is ready to walk through again.";
    }

    private void AcknowledgeGptSetup()
    {
        GptSetupAcknowledged = true;
        Banner = HasCustomGptUrl
            ? "Custom GPT setup marked complete. Continue when you have saved it in ChatGPT."
            : "Custom GPT setup will be ready after Tailscale is connected. You can finish it from setup again.";
    }

    private void OpenGptBuilder()
    {
        if (!_processLauncher.Open("https://chatgpt.com/gpts/editor"))
        {
            Banner = "Could not open the ChatGPT GPT builder in your browser.";
        }
    }

    private async Task GrantFullAccessAsync()
    {
        var handler = FullAccessRequested;
        if (handler is null || !await handler())
        {
            return;
        }

        SetBusy(true);
        try
        {
            _runtime.GrantFullAccess();
            SyncWorkspacesFromRuntime();
            CurrentWorkspace = _runtime.Settings.CurrentWorkspace;
            Banner = "Full access enabled. Requests can work across this machine.";

            // Full access completes the first wizard step. Move forward only
            // after the permission dialog was confirmed and the runtime grant
            // succeeded; failed or cancelled requests remain on this step.
            if (!SetupCompleted && SetupStep == 1)
            {
                SetupStep = 2;
            }
        }
        catch (Exception exception)
        {
            Banner = $"Could not enable full access: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task<bool> RequestApprovalAsync(ToolRequest request, string summary)
    {
        var handler = ApprovalDialogRequested;
        return handler is null ? false : await handler(request, summary);
    }

    public async Task ShutdownAsync()
    {
        await _runtime.DisposeAsync();
    }

    private async Task ConnectTailscaleAsync()
    {
        SetBusy(true);
        try
        {
            var publicUrl = await _runtime.StartTunnelAsync();

            // A URL is returned only once Funnel is actually running. This
            // keeps the wizard on the Tailscale step for login, admin, or
            // Funnel-approval flows that still need user action.
            if (!SetupCompleted && SetupStep == 2 && !string.IsNullOrWhiteSpace(publicUrl))
            {
                SetupStep = 3;
            }
        }
        catch (Exception exception)
        {
            Banner = $"Tailscale could not start: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StopTailscaleAsync()
    {
        SetBusy(true);
        try
        {
            await _runtime.StopTunnelAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task InstallTailscaleAsync()
    {
        SetBusy(true);
        try
        {
            await _runtime.InstallTailscaleAsync();
        }
        catch (Exception exception)
        {
            Banner = $"Could not open the Tailscale installer: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnStatusChanged(CompanionStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (status.Area)
            {
                case "local-relay":
                    LocalRelayState = status.State;
                    LocalRelayMessage = status.Message;
                    break;
                case "tunnel":
                    TunnelState = status.State;
                    TunnelMessage = status.Message;
                    TunnelUrl = string.Equals(status.State, "running", StringComparison.OrdinalIgnoreCase) &&
                                Uri.TryCreate(status.Message, UriKind.Absolute, out var tunnelUri) &&
                                string.Equals(tunnelUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                        ? status.Message.TrimEnd('/')
                        : string.Empty;
                    OnPropertyChanged(nameof(HasCustomGptUrl));
                    OnPropertyChanged(nameof(CustomGptActionUrl));
                    OnPropertyChanged(nameof(CustomGptSchema));
                    OnPropertyChanged(nameof(GptSetupActionLabel));

                    // Status updates also cover a Funnel that was already running
                    // or became ready during background polling.
                    if (!SetupCompleted && SetupStep == 2 &&
                        string.Equals(status.State, "running", StringComparison.OrdinalIgnoreCase))
                    {
                        SetupStep = 3;
                    }
                    break;
            }

            OnPropertyChanged(nameof(SetupHeadline));
            OnPropertyChanged(nameof(SetupMessage));
        });
    }

    private void OnLogChanged(ToolLogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var existing = Activity.FirstOrDefault(log => log.RequestId == entry.RequestId);
            if (existing is not null)
            {
                var index = Activity.IndexOf(existing);
                Activity[index] = entry;
            }
            else
            {
                Activity.Insert(0, entry);
            }

            SelectedActivity = entry;

            while (Activity.Count > 100)
            {
                Activity.RemoveAt(Activity.Count - 1);
            }

            OnPropertyChanged(nameof(HasActivity));
            OnPropertyChanged(nameof(HasNoActivity));
        });
    }

    private void RefreshSkills()
    {
        Skills.Clear();
        foreach (var skill in _runtime.GetSkills())
        {
            Skills.Add(skill);
        }

        OnPropertyChanged(nameof(HasSkills));
        OnPropertyChanged(nameof(HasNoSkills));
    }

    private Task<bool> OnApprovalRequested(ToolRequest request, string summary, CancellationToken cancellationToken)
    {
        var handler = ApprovalDialogRequested;
        return handler is null ? Task.FromResult(false) : handler(request, summary);
    }

    private void SetBusy(bool value)
    {
        IsBusy = value;
        if (ConnectTailscaleCommand is AsyncCommand connect)
        {
            connect.RaiseCanExecuteChanged();
        }

        if (InstallTailscaleCommand is AsyncCommand install)
        {
            install.RaiseCanExecuteChanged();
        }

        if (StopTailscaleCommand is AsyncCommand stop)
        {
            stop.RaiseCanExecuteChanged();
        }

        if (FullAccessCommand is AsyncCommand fullAccess)
        {
            fullAccess.RaiseCanExecuteChanged();
        }
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
    }

    private sealed class SimpleCommand : ICommand
    {
        private readonly Action<object?> _execute;

        public SimpleCommand(Action execute) : this(_ => execute()) { }
        public SimpleCommand(Action<object?> execute) { _execute = execute; }
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
    }

    private sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool> _canExecute;
        private bool _running;

        public AsyncCommand(Func<Task> execute, Func<bool> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => !_running && _canExecute();
        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _running = true;
            RaiseCanExecuteChanged();
            try { await _execute(); }
            finally
            {
                _running = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
