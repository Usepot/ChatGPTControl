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
}
