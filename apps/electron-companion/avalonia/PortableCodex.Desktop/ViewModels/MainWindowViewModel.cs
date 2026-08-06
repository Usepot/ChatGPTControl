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
    private string _relayUrl = string.Empty;
    private string _currentWorkspace = string.Empty;
    private string _relayState = "disconnected";
    private string _relayMessage = "Waiting for local relay";
    private string _localRelayState = "stopped";
    private string _localRelayMessage = "Local relay is stopped";
    private string _tunnelState = "stopped";
    private string _tunnelMessage = "Tailscale Funnel is not connected";
    private string _banner = string.Empty;
    private ToolLogEntry? _selectedActivity;
    private bool _isBusy;
    private bool _initialized;

    public MainWindowViewModel()
    {
        _runtime = new CompanionRuntime();
        _runtime.StatusChanged += OnStatusChanged;
        _runtime.LogChanged += OnLogChanged;
        _runtime.ApprovalRequested += OnApprovalRequested;

        _relayUrl = _runtime.Settings.RelayUrl;
        _currentWorkspace = _runtime.Settings.CurrentWorkspace;
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

        SaveCommand = new AsyncCommand(SaveSettingsAsync, () => !IsBusy);
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
            }
        });
        RefreshSkillsCommand = new SimpleCommand(RefreshSkills);
        CopyDeviceIdCommand = new SimpleCommand(() => CopyRequested?.Invoke(DeviceId));
        CopyDeviceTokenCommand = new SimpleCommand(() => CopyRequested?.Invoke(DeviceToken));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? WorkspacePickerRequested;
    public event Action<string>? CopyRequested;
    public event Func<ToolRequest, string, Task<bool>>? ApprovalDialogRequested;

    public ObservableCollection<string> Workspaces { get; } = [];
    public ObservableCollection<ToolLogEntry> Activity { get; } = [];
    public ObservableCollection<SkillListEntry> Skills { get; } = [];

    public ICommand SaveCommand { get; }
    public ICommand ConnectTailscaleCommand { get; }
    public ICommand InstallTailscaleCommand { get; }
    public ICommand StopTailscaleCommand { get; }
    public ICommand AddWorkspaceCommand { get; }
    public ICommand RemoveWorkspaceCommand { get; }
    public ICommand RefreshSkillsCommand { get; }
    public ICommand CopyDeviceIdCommand { get; }
    public ICommand CopyDeviceTokenCommand { get; }

    public string RelayUrl
    {
        get => _relayUrl;
        set => SetProperty(ref _relayUrl, value);
    }

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

    public string RelayState { get => _relayState; private set => SetProperty(ref _relayState, value); }
    public string RelayMessage { get => _relayMessage; private set => SetProperty(ref _relayMessage, value); }
    public string LocalRelayState { get => _localRelayState; private set => SetProperty(ref _localRelayState, value); }
    public string LocalRelayMessage { get => _localRelayMessage; private set => SetProperty(ref _localRelayMessage, value); }
    public string TunnelState { get => _tunnelState; private set => SetProperty(ref _tunnelState, value); }
    public string TunnelMessage { get => _tunnelMessage; private set => SetProperty(ref _tunnelMessage, value); }
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
    public string SetupHeadline => string.IsNullOrWhiteSpace(RelayUrl)
        ? "Let’s get your local bridge ready."
        : "Your local bridge is configured.";
    public string SetupMessage => string.IsNullOrWhiteSpace(RelayUrl)
        ? "Add the hosted relay URL, choose a trusted workspace, and connect Tailscale when you’re ready to expose this machine."
        : "The companion can stay in the background. It will surface approvals and connection problems when they need your attention.";

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
            if (!Workspaces.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                Workspaces.Add(_runtime.Settings.TrustedWorkspaces.First(root => string.Equals(root, path, StringComparison.OrdinalIgnoreCase)));
            }

            CurrentWorkspace = _runtime.Settings.CurrentWorkspace;
            OnPropertyChanged(nameof(HasWorkspaces));
            OnPropertyChanged(nameof(HasNoWorkspaces));
            Banner = "Workspace trusted. Requests will stay inside this folder.";
        }
        catch (Exception exception)
        {
            Banner = exception.Message;
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

    private async Task SaveSettingsAsync()
    {
        SetBusy(true);
        try
        {
            _runtime.SetRelayUrl(RelayUrl);
            await _runtime.ReconnectAsync();
            Banner = "Settings saved. The relay connection is refreshing now.";
        }
        catch (Exception exception)
        {
            Banner = $"Could not save settings: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ConnectTailscaleAsync()
    {
        SetBusy(true);
        try
        {
            await _runtime.StartTunnelAsync();
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
                case "relay":
                    RelayState = status.State;
                    RelayMessage = status.Message;
                    break;
                case "local-relay":
                    LocalRelayState = status.State;
                    LocalRelayMessage = status.Message;
                    break;
                case "tunnel":
                    TunnelState = status.State;
                    TunnelMessage = status.Message;
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
        if (SaveCommand is AsyncCommand save)
        {
            save.RaiseCanExecuteChanged();
        }

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
