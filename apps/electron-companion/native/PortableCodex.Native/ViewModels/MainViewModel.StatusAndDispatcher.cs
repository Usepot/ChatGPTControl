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
}
