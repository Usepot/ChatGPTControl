using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PortableCodex.Native.ViewModels;

namespace PortableCodex.Native.Views;

public partial class ChatGptView : System.Windows.Controls.UserControl
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromMinutes(5);
    private const string DefaultChatGptUrl = "https://chatgpt.com/";

    private MainViewModel? _vm;
    private CoreWebView2Environment? _env;
    private bool _envInitStarted;
    private bool _envReady;
    private string? _pendingUrl;

    private readonly Dictionary<string, WebViewEntry> _threadCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WebViewEntry> _draftPerProject = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<WebViewEntry>> _draftCreating = new(StringComparer.OrdinalIgnoreCase);
    private WebViewEntry? _active;
    private DispatcherTimer? _idleTimer;

    public ChatGptView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnVisibilityChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = e.NewValue as MainViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private async void OnVisibilityChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || _envInitStarted)
        {
            return;
        }

        _envInitStarted = true;
        try
        {
            await EnsureEnvironmentAsync();
            StartIdleTimer();
            var url = _pendingUrl ?? _vm?.PendingWebViewUrl;
            if (!string.IsNullOrWhiteSpace(url))
            {
                await ShowOrCreateAsync(url);
                _pendingUrl = null;
            }
            PreWarmDraftForActiveProject();
        }
        catch (Exception ex)
        {
            ShowInitError(ex);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _idleTimer?.Stop();
    }

    private async Task EnsureEnvironmentAsync()
    {
        if (_envReady)
        {
            return;
        }

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PortableCodex",
            "webview2");
        Directory.CreateDirectory(userDataFolder);

        _env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
        _envReady = true;
    }

    private void StartIdleTimer()
    {
        if (_idleTimer is not null)
        {
            return;
        }

        _idleTimer = new DispatcherTimer { Interval = IdleSweepInterval };
        _idleTimer.Tick += OnIdleTick;
        _idleTimer.Start();
    }

    private async void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null || !string.Equals(e.PropertyName, nameof(MainViewModel.PendingWebViewUrl), StringComparison.Ordinal))
        {
            return;
        }

        var target = _vm.PendingWebViewUrl;
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        if (!_envReady)
        {
            _pendingUrl = target;
            return;
        }

        try
        {
            await ShowOrCreateAsync(target);
            PreWarmDraftForActiveProject();
        }
        catch (Exception ex)
        {
            ShowInitError(ex);
        }
    }

    private async Task ShowOrCreateAsync(string target)
    {
        if (IsThreadUrl(target))
        {
            var key = Canonicalize(target);
            if (_threadCache.TryGetValue(key, out var cached))
            {
                ActivateEntry(cached);
                return;
            }

            var created = await CreateEntryAsync();
            _threadCache[key] = created;
            ActivateEntry(created);
            NavigateEntry(created, target);
            return;
        }

        var workspace = _vm?.ActiveChatGptProject?.WorkspacePath ?? string.Empty;
        var draft = await GetOrCreateDraftAsync(workspace, target);
        ActivateEntry(draft);
    }

    private async Task<WebViewEntry> GetOrCreateDraftAsync(string workspace, string url)
    {
        if (_draftPerProject.TryGetValue(workspace, out var existing))
        {
            NavigateEntry(existing, url);
            return existing;
        }
        if (_draftCreating.TryGetValue(workspace, out var pending))
        {
            return await pending;
        }

        async Task<WebViewEntry> CreateAndTrack()
        {
            var entry = await CreateEntryAsync();
            _draftPerProject[workspace] = entry;
            NavigateEntry(entry, url);
            return entry;
        }

        var task = CreateAndTrack();
        _draftCreating[workspace] = task;
        try
        {
            return await task;
        }
        finally
        {
            _draftCreating.Remove(workspace);
        }
    }

    private async Task<WebViewEntry> CreateEntryAsync()
    {
        var wv = new WebView2
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
            Visibility = Visibility.Hidden,
        };
        WebViewHost.Children.Add(wv);
        await wv.EnsureCoreWebView2Async(_env);

        var core = wv.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;

        var entry = new WebViewEntry(wv);
        core.NavigationCompleted += (_, _) => OnNavCompleted(entry);
        core.SourceChanged += (_, _) => OnSourceChanged(entry);
        core.DocumentTitleChanged += (_, _) => OnTitleChanged(entry);
        return entry;
    }

    private void NavigateEntry(WebViewEntry entry, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            var current = entry.WebView.CoreWebView2?.Source;
            if (string.Equals(current, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            entry.WebView.CoreWebView2?.Navigate(uri.AbsoluteUri);
        }
        catch
        {
            // Best-effort; navigation failures surface via the WebView itself.
        }
    }

    private void ActivateEntry(WebViewEntry entry)
    {
        if (_active is not null && !ReferenceEquals(_active, entry))
        {
            _active.WebView.Visibility = Visibility.Hidden;
        }
        entry.WebView.Visibility = Visibility.Visible;
        entry.LastActiveAt = DateTime.UtcNow;
        _active = entry;
    }

    private void OnNavCompleted(WebViewEntry entry)
    {
        if (_vm is null)
        {
            return;
        }

        var url = entry.WebView.CoreWebView2?.Source ?? string.Empty;
        var title = entry.WebView.CoreWebView2?.DocumentTitle ?? string.Empty;
        PromoteDraftIfNeeded(entry, url);
        entry.LastActiveAt = DateTime.UtcNow;
        _vm.OnChatGptNavigated(url, title);
    }

    private void OnSourceChanged(WebViewEntry entry)
    {
        if (_vm is null)
        {
            return;
        }

        var url = entry.WebView.CoreWebView2?.Source ?? string.Empty;
        var title = entry.WebView.CoreWebView2?.DocumentTitle ?? string.Empty;
        PromoteDraftIfNeeded(entry, url);
        entry.LastActiveAt = DateTime.UtcNow;
        _vm.OnChatGptNavigated(url, title);
    }

    private void OnTitleChanged(WebViewEntry entry)
    {
        if (_vm is null)
        {
            return;
        }

        var url = entry.WebView.CoreWebView2?.Source ?? string.Empty;
        var title = entry.WebView.CoreWebView2?.DocumentTitle ?? string.Empty;
        PromoteDraftIfNeeded(entry, url);
        entry.LastActiveAt = DateTime.UtcNow;
        _vm.OnChatGptNavigated(url, title);
    }

    private void PromoteDraftIfNeeded(WebViewEntry entry, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !IsThreadUrl(url))
        {
            return;
        }

        string? draftWorkspace = null;
        foreach (var kv in _draftPerProject)
        {
            if (ReferenceEquals(kv.Value, entry))
            {
                draftWorkspace = kv.Key;
                break;
            }
        }
        if (draftWorkspace is null)
        {
            return;
        }

        var key = Canonicalize(url);
        if (_threadCache.TryGetValue(key, out var existing) && !ReferenceEquals(existing, entry))
        {
            if (ReferenceEquals(_active, entry))
            {
                ActivateEntry(existing);
            }
            RemoveAndDispose(entry);
        }
        else
        {
            _threadCache[key] = entry;
        }
        _draftPerProject.Remove(draftWorkspace);

        // Pre-warm a replacement draft so the next "+ new chat" is instant.
        PreWarmDraftFor(draftWorkspace);
    }

    private void PreWarmDraftForActiveProject()
    {
        var workspace = _vm?.ActiveChatGptProject?.WorkspacePath;
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            PreWarmDraftFor(workspace);
        }
    }

    private void PreWarmDraftFor(string workspacePath)
    {
        if (_vm is null || !_envReady || string.IsNullOrWhiteSpace(workspacePath))
        {
            return;
        }
        if (_draftPerProject.ContainsKey(workspacePath) || _draftCreating.ContainsKey(workspacePath))
        {
            return;
        }

        var project = _vm.ChatGptProjects.FirstOrDefault(p =>
            string.Equals(p.WorkspacePath, workspacePath, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            return;
        }

        var url = !string.IsNullOrWhiteSpace(project.ProjectUrl) ? project.ProjectUrl : DefaultChatGptUrl;
        _ = SafePreWarmAsync(workspacePath, url);
    }

    private async Task SafePreWarmAsync(string workspace, string url)
    {
        try
        {
            await GetOrCreateDraftAsync(workspace, url);
        }
        catch
        {
            // Best-effort; if pre-warm fails the next click will create on demand.
        }
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;

        var staleThreads = new List<string>();
        foreach (var kv in _threadCache)
        {
            if (ReferenceEquals(_active, kv.Value)) continue;
            if (kv.Value.LastActiveAt < cutoff) staleThreads.Add(kv.Key);
        }
        foreach (var k in staleThreads)
        {
            var entry = _threadCache[k];
            _threadCache.Remove(k);
            RemoveAndDispose(entry);
        }

        var staleDrafts = new List<string>();
        foreach (var kv in _draftPerProject)
        {
            if (ReferenceEquals(_active, kv.Value)) continue;
            if (kv.Value.LastActiveAt < cutoff) staleDrafts.Add(kv.Key);
        }
        foreach (var k in staleDrafts)
        {
            var entry = _draftPerProject[k];
            _draftPerProject.Remove(k);
            RemoveAndDispose(entry);
        }
    }

    private void RemoveAndDispose(WebViewEntry entry)
    {
        try
        {
            WebViewHost.Children.Remove(entry.WebView);
            entry.WebView.Dispose();
        }
        catch
        {
            // Best-effort.
        }
    }

    private static bool IsThreadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Host.EndsWith("chatgpt.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith("chat.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "c", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(segments[i + 1]))
            {
                return true;
            }
        }
        return false;
    }

    private static string Canonicalize(string url)
    {
        var q = url.IndexOf('?');
        if (q >= 0) url = url[..q];
        var h = url.IndexOf('#');
        if (h >= 0) url = url[..h];
        return url.TrimEnd('/');
    }

    private void OnQuickAddClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        if (_vm.ChatGptProjects.Count == 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "No trusted workspaces yet — use the folder icon to add one.",
                IsEnabled = false,
            });
        }
        else
        {
            foreach (var project in _vm.ChatGptProjects)
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = project.DisplayTitle,
                    ToolTip = project.WorkspacePath,
                    Tag = project,
                };
                item.Click += (_, _) => _vm.SelectChatGptProjectCommand.Execute(project);
                menu.Items.Add(item);
            }
        }

        menu.IsOpen = true;
    }

    private void OnReloadClick(object sender, RoutedEventArgs e) => _active?.WebView.CoreWebView2?.Reload();

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_active?.WebView.CoreWebView2?.CanGoBack == true)
        {
            _active.WebView.CoreWebView2.GoBack();
        }
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (_active?.WebView.CoreWebView2?.CanGoForward == true)
        {
            _active.WebView.CoreWebView2.GoForward();
        }
    }

    private void ShowInitError(Exception ex)
    {
        var message = ex.Message;
        System.Windows.MessageBox.Show(
            "Failed to initialize the ChatGPT browser.\n\n" + message +
            "\n\nMake sure the Microsoft Edge WebView2 runtime is installed.",
            "ChatGPT view",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    }

    private sealed class WebViewEntry
    {
        public WebView2 WebView { get; }
        public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;
        public WebViewEntry(WebView2 webView) { WebView = webView; }
    }
}
