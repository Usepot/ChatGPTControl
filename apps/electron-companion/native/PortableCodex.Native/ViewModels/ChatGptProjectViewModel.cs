using System.Collections.ObjectModel;
using System.IO;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;

namespace PortableCodex.Native.ViewModels;

public sealed class ChatGptProjectViewModel : ObservableObject
{
    public const string DefaultProjectUrl = "https://chatgpt.com/";

    private string _name = string.Empty;
    private string _projectUrl = DefaultProjectUrl;
    private bool _isExpanded;

    public ChatGptProjectViewModel(string workspacePath)
    {
        WorkspacePath = workspacePath;
        Name = DeriveDefaultName(workspacePath);
        Threads.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasThreads));
            OnPropertyChanged(nameof(ThreadCountLabel));
        };
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandGlyph));
            }
        }
    }

    public bool HasThreads => Threads.Count > 0;

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    public string ThreadCountLabel => Threads.Count == 1 ? "1 chat" : $"{Threads.Count} chats";

    public string WorkspacePath { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, string.IsNullOrWhiteSpace(value) ? DeriveDefaultName(WorkspacePath) : value.Trim());
    }

    public string ProjectUrl
    {
        get => _projectUrl;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            SetProperty(ref _projectUrl, string.IsNullOrWhiteSpace(trimmed) ? DefaultProjectUrl : trimmed);
        }
    }

    public ObservableCollection<ChatGptThreadViewModel> Threads { get; } = [];

    public bool HasCustomUrl => !string.Equals(ProjectUrl, DefaultProjectUrl, StringComparison.OrdinalIgnoreCase);

    public string DisplayTitle => string.IsNullOrWhiteSpace(Name) ? WorkspacePath : Name;

    public ChatGptProjectMeta ToModel()
    {
        return new ChatGptProjectMeta
        {
            WorkspacePath = WorkspacePath,
            Name = Name,
            ProjectUrl = ProjectUrl,
            Threads = Threads.Select(t => t.ToModel()).ToList(),
        };
    }

    public static ChatGptProjectViewModel FromModel(ChatGptProjectMeta meta)
    {
        var vm = new ChatGptProjectViewModel(meta.WorkspacePath)
        {
            Name = string.IsNullOrWhiteSpace(meta.Name) ? DeriveDefaultName(meta.WorkspacePath) : meta.Name,
            ProjectUrl = string.IsNullOrWhiteSpace(meta.ProjectUrl) ? DefaultProjectUrl : meta.ProjectUrl,
        };

        foreach (var thread in meta.Threads ?? [])
        {
            if (string.IsNullOrWhiteSpace(thread.Url))
            {
                continue;
            }

            vm.Threads.Add(ChatGptThreadViewModel.FromModel(thread));
        }

        return vm;
    }

    private static string DeriveDefaultName(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return "Workspace";
        }

        try
        {
            var trimmed = workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(leaf) ? trimmed : leaf;
        }
        catch
        {
            return workspacePath;
        }
    }
}

public sealed class ChatGptThreadViewModel : ObservableObject
{
    private string _title = string.Empty;
    private string _lastVisitedAt = string.Empty;

    public ChatGptThreadViewModel(string url)
    {
        Url = url;
    }

    public string Url { get; }

    public string Title
    {
        get => _title;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            SetProperty(ref _title, string.IsNullOrWhiteSpace(trimmed) ? "New chat" : trimmed);
            OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string LastVisitedAt
    {
        get => _lastVisitedAt;
        set => SetProperty(ref _lastVisitedAt, value ?? string.Empty);
    }

    public string DisplayLabel => string.IsNullOrWhiteSpace(Title) ? "New chat" : Title;

    public ChatGptThread ToModel() => new()
    {
        Url = Url,
        Title = Title,
        LastVisitedAt = LastVisitedAt,
    };

    public static ChatGptThreadViewModel FromModel(ChatGptThread model) => new(model.Url)
    {
        Title = model.Title,
        LastVisitedAt = model.LastVisitedAt,
    };
}
