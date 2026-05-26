using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;
using WpfApp = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;

namespace PortableCodex.Native.ViewModels;

public sealed partial class MainViewModel
{
    public const int StepChatGpt = 6;

    public ObservableCollection<ChatGptProjectViewModel> ChatGptProjects { get; } = [];

    private ChatGptProjectViewModel? _activeChatGptProject;
    private ChatGptThreadViewModel? _activeChatGptThread;
    private string _pendingWebViewUrl = ChatGptProjectViewModel.DefaultProjectUrl;

    public ChatGptProjectViewModel? ActiveChatGptProject
    {
        get => _activeChatGptProject;
        private set
        {
            if (SetProperty(ref _activeChatGptProject, value))
            {
                OnPropertyChanged(nameof(ActiveChatGptProjectTitle));
                OnPropertyChanged(nameof(HasActiveChatGptProject));
                if (value is not null)
                {
                    if (!string.IsNullOrWhiteSpace(value.WorkspacePath) &&
                        !string.Equals(CurrentWorkspace, value.WorkspacePath, StringComparison.OrdinalIgnoreCase))
                    {
                        CurrentWorkspace = value.WorkspacePath;
                    }
                }
            }
        }
    }

    public ChatGptThreadViewModel? ActiveChatGptThread
    {
        get => _activeChatGptThread;
        private set => SetProperty(ref _activeChatGptThread, value);
    }

    /// <summary>URL the WebView should navigate to next. Bumped whenever the user picks a
    /// project / thread / new chat. The view listens for changes and calls CoreWebView2.Navigate.</summary>
    public string PendingWebViewUrl
    {
        get => _pendingWebViewUrl;
        private set
        {
            if (!SetProperty(ref _pendingWebViewUrl, value))
            {
                OnPropertyChanged();
            }
        }
    }

    public bool HasActiveChatGptProject => ActiveChatGptProject is not null;

    public string ActiveChatGptProjectTitle => ActiveChatGptProject?.DisplayTitle ?? "No project selected";

    public bool HasAnyChatGptThreads => ChatGptProjects.Any(p => p.Threads.Count > 0);

    public RelayCommand OpenChatGptCommand { get; private set; } = null!;
    public RelayCommand CloseChatGptCommand { get; private set; } = null!;
    public RelayCommand SelectChatGptProjectCommand { get; private set; } = null!;
    public RelayCommand NewChatGptThreadCommand { get; private set; } = null!;
    public RelayCommand OpenChatGptThreadCommand { get; private set; } = null!;
    public RelayCommand RemoveChatGptThreadCommand { get; private set; } = null!;
    public RelayCommand SetChatGptProjectUrlCommand { get; private set; } = null!;
    public RelayCommand ToggleChatGptProjectExpandedCommand { get; private set; } = null!;

    public string ChatGptPinnedUrl
    {
        get => _chatGptPinnedUrl;
        private set
        {
            var trimmed = (value ?? string.Empty).Trim();
            SetProperty(ref _chatGptPinnedUrl, string.IsNullOrWhiteSpace(trimmed)
                ? ChatGptProjectViewModel.DefaultProjectUrl
                : trimmed);
        }
    }

    private void InitializeChatGptCommands()
    {
        ChatGptProjects.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not null)
            {
                foreach (var item in args.NewItems)
                {
                    if (item is ChatGptProjectViewModel proj)
                    {
                        proj.Threads.CollectionChanged += OnAnyProjectThreadsChanged;
                    }
                }
            }
            OnPropertyChanged(nameof(HasAnyChatGptThreads));
        };
        foreach (var proj in ChatGptProjects)
        {
            proj.Threads.CollectionChanged += OnAnyProjectThreadsChanged;
        }

        OpenChatGptCommand = new RelayCommand(OpenChatGptView);
        CloseChatGptCommand = new RelayCommand(() => CurrentStep = StepReady);
        SelectChatGptProjectCommand = new RelayCommand(param => SelectChatGptProject(param as ChatGptProjectViewModel));
        NewChatGptThreadCommand = new RelayCommand(StartNewChatGptThread);
        OpenChatGptThreadCommand = new RelayCommand(param => OpenChatGptThread(param as ChatGptThreadViewModel));
        RemoveChatGptThreadCommand = new RelayCommand(param => RemoveChatGptThread(param as ChatGptThreadViewModel));
        SetChatGptProjectUrlCommand = new RelayCommand(param => SetChatGptProjectUrl(param as ChatGptProjectViewModel));
        ToggleChatGptProjectExpandedCommand = new RelayCommand(param => ToggleChatGptProjectExpanded(param as ChatGptProjectViewModel));
    }

    private void OpenChatGptView()
    {
        if (TrustedWorkspaces.Count == 0)
        {
            WpfMessageBox.Show(
                "Add a trusted workspace first — each ChatGPT project is tied to one.",
                "ChatGPT",
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Information);
            CurrentStep = StepWorkspaces;
            return;
        }

        SyncChatGptProjectsWithWorkspaces();

        if (ActiveChatGptProject is null)
        {
            var preferred = ChatGptProjects.FirstOrDefault(p =>
                string.Equals(p.WorkspacePath, CurrentWorkspace, StringComparison.OrdinalIgnoreCase))
                ?? ChatGptProjects.FirstOrDefault();
            if (preferred is not null)
            {
                SelectChatGptProject(preferred);
            }
        }
        else
        {
            // Re-navigate so the WebView picks up the active project when returning to the page.
            PendingWebViewUrl = ResolveActiveUrl();
        }

        CurrentStep = StepChatGpt;
    }

    private void SelectChatGptProject(ChatGptProjectViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        ActiveChatGptProject = project;
        project.IsExpanded = true;
        ActiveChatGptThread = null;
        PendingWebViewUrl = ChatGptPinnedUrl;
    }

    private void StartNewChatGptThread()
    {
        if (ActiveChatGptProject is null)
        {
            return;
        }

        ActiveChatGptThread = null;
        PendingWebViewUrl = ChatGptPinnedUrl;
    }

    private void OpenChatGptThread(ChatGptThreadViewModel? thread)
    {
        if (thread is null || string.IsNullOrWhiteSpace(thread.Url))
        {
            return;
        }

        var owner = ChatGptProjects.FirstOrDefault(p => p.Threads.Contains(thread));
        if (owner is not null && !ReferenceEquals(ActiveChatGptProject, owner))
        {
            ActiveChatGptProject = owner;
            owner.IsExpanded = true;
        }

        ActiveChatGptThread = thread;
        PendingWebViewUrl = thread.Url;
    }

    private void RemoveChatGptThread(ChatGptThreadViewModel? thread)
    {
        if (thread is null || ActiveChatGptProject is null)
        {
            return;
        }

        ActiveChatGptProject.Threads.Remove(thread);
        if (ReferenceEquals(ActiveChatGptThread, thread))
        {
            ActiveChatGptThread = null;
        }

        PersistState();
    }

    private void SetChatGptProjectUrl(ChatGptProjectViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        var dialog = new System.Windows.Window
        {
            Title = "Pin ChatGPT URL",
            Width = 520,
            Height = 200,
            MinWidth = 360,
            MinHeight = 180,
            Owner = WpfApp.Current.MainWindow,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
        };
        dialog.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AppBg");

        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(18) };

        var label = new System.Windows.Controls.TextBlock
        {
            Text = "Paste the ChatGPT URL that new chats should open across all workspaces.\nFor a ChatGPT Project, use its /g/g-... or /project/... link.",
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
        };
        label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SecondaryFg");

        var input = new System.Windows.Controls.TextBox
        {
            Text = ChatGptPinnedUrl,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas"),
            FontSize = 12,
            Padding = new System.Windows.Thickness(8, 6, 8, 6),
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        };
        input.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "CardMutedBg");
        input.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "PrimaryFg");
        input.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "SubtleBorder");

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", Margin = new System.Windows.Thickness(0, 0, 8, 0) };
        cancel.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
        var save = new Wpf.Ui.Controls.Button { Content = "Save" };
        save.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) =>
        {
            ChatGptPinnedUrl = input.Text;
            PersistState();
            if (ReferenceEquals(ActiveChatGptProject, project))
            {
                PendingWebViewUrl = ChatGptPinnedUrl;
            }
            dialog.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        panel.Children.Add(label);
        panel.Children.Add(input);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    /// <summary>Called by ChatGptView when a navigation or title change fires. Updates the matching
    /// thread (across all projects) in place; creates a new thread only when the URL is a
    /// chat permalink that no project owns yet.</summary>
    public void OnChatGptNavigated(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!IsChatGptChatUrl(url))
        {
            // Non-chat URLs (project pages, home, etc.) — don't touch any thread title.
            return;
        }

        var canonical = url.Split('?', 2)[0].Split('#', 2)[0];

        foreach (var project in ChatGptProjects)
        {
            var match = project.Threads.FirstOrDefault(t =>
                string.Equals(t.Url, canonical, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                match.Title = SanitizeTitle(title);
            }
            match.LastVisitedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            PersistState();
            return;
        }

        if (ActiveChatGptProject is null)
        {
            return;
        }

        var entry = new ChatGptThreadViewModel(canonical)
        {
            Title = SanitizeTitle(title),
            LastVisitedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        ActiveChatGptProject.Threads.Insert(0, entry);
        ActiveChatGptThread = entry;
        ActiveChatGptProject.IsExpanded = true;
        PersistState();
    }

    private void ToggleChatGptProjectExpanded(ChatGptProjectViewModel? project)
    {
        if (project is null)
        {
            return;
        }

        project.IsExpanded = !project.IsExpanded;
    }

    private void OnAnyProjectThreadsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasAnyChatGptThreads));
    }

    private string ResolveActiveUrl()
    {
        if (ActiveChatGptThread is not null && !string.IsNullOrWhiteSpace(ActiveChatGptThread.Url))
        {
            return ActiveChatGptThread.Url;
        }

        if (!string.IsNullOrWhiteSpace(ChatGptPinnedUrl))
        {
            return ChatGptPinnedUrl;
        }

        return ChatGptProjectViewModel.DefaultProjectUrl;
    }

    private void SyncChatGptProjectsWithWorkspaces()
    {
        var keep = new HashSet<string>(TrustedWorkspaces, StringComparer.OrdinalIgnoreCase);

        for (var i = ChatGptProjects.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(ChatGptProjects[i].WorkspacePath))
            {
                if (ReferenceEquals(ActiveChatGptProject, ChatGptProjects[i]))
                {
                    ActiveChatGptProject = null;
                    ActiveChatGptThread = null;
                }
                ChatGptProjects.RemoveAt(i);
            }
        }

        foreach (var workspace in TrustedWorkspaces)
        {
            if (!ChatGptProjects.Any(p => string.Equals(p.WorkspacePath, workspace, StringComparison.OrdinalIgnoreCase)))
            {
                ChatGptProjects.Add(new ChatGptProjectViewModel(workspace));
            }
        }
    }

    private void LoadChatGptProjectsFromSettings(IEnumerable<ChatGptProjectMeta> projects)
    {
        ChatGptProjects.Clear();
        foreach (var meta in projects)
        {
            if (string.IsNullOrWhiteSpace(meta.WorkspacePath))
            {
                continue;
            }
            ChatGptProjects.Add(ChatGptProjectViewModel.FromModel(meta));
        }

        SyncChatGptProjectsWithWorkspaces();
    }

    private static bool IsChatGptChatUrl(string url)
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

    private static string SanitizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "New chat";
        }

        var trimmed = title.Trim();
        var suffix = " | ChatGPT";
        if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^suffix.Length].TrimEnd();
        }
        return string.IsNullOrWhiteSpace(trimmed) ? "New chat" : trimmed;
    }
}
