using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PortableCodex.Core.Services;
using PortableCodex.Desktop.ViewModels;

namespace PortableCodex.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IWorkspacePicker _workspacePicker;
    private readonly IClipboardService _clipboardService;

    public MainWindow()
    {
        InitializeComponent();
        _workspacePicker = new AvaloniaWorkspacePicker(this);
        _clipboardService = new AvaloniaClipboardService(this);
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        Opened += OnOpened;
        Closing += OnClosing;
        _viewModel.WorkspacePickerRequested += OnWorkspacePickerRequested;
        _viewModel.CopyRequested += OnCopyRequested;
        _viewModel.ApprovalDialogRequested += OnApprovalDialogRequested;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        await _viewModel.ShutdownAsync();
    }

    private async void OnWorkspacePickerRequested()
    {
        var path = await _workspacePicker.PickWorkspaceAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _viewModel.AddWorkspace(path);
        }
    }

    private async void OnCopyRequested(string value)
    {
        await _clipboardService.SetTextAsync(value);
    }

    private async Task<bool> OnApprovalDialogRequested(PortableCodex.Native.Models.ToolRequest request, string summary)
    {
        var dialog = new ApprovalWindow(summary);
        return await dialog.ShowDialog<bool?>(this) == true;
    }

    private sealed class AvaloniaWorkspacePicker : IWorkspacePicker
    {
        private readonly Window _window;

        public AvaloniaWorkspacePicker(Window window)
        {
            _window = window;
        }

        public async Task<string?> PickWorkspaceAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a trusted workspace",
                AllowMultiple = false,
            });
            cancellationToken.ThrowIfCancellationRequested();
            return folders.FirstOrDefault()?.Path.LocalPath;
        }
    }

    private sealed class AvaloniaClipboardService : IClipboardService
    {
        private readonly Window _window;

        public AvaloniaClipboardService(Window window)
        {
            _window = window;
        }

        public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_window.Clipboard is not null)
            {
                await _window.Clipboard.SetTextAsync(text);
            }
        }
    }
}
