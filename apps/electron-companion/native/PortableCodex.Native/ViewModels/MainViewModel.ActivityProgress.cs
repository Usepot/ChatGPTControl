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
}
