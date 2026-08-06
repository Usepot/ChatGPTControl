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
    private async Task ApplyTunnelSettingsAsync()
    {
        ClearError();

        if (!RelayReady)
        {
            await _localRelayService.RestartAsync(BuildCurrentSettings());
            _relayClientService.Connect();
            if (!RelayReady)
            {
                SetError(string.IsNullOrWhiteSpace(LocalRelayMessage)
                    ? "Local relay is not ready yet. Wait a moment and try again."
                    : LocalRelayMessage);
                return;
            }
        }

        PersistState();
        if (_tunnelService.IsRunning || !string.IsNullOrWhiteSpace(TunnelUrl))
        {
            await _tunnelService.StopAsync();
        }

        await _tunnelService.StartAsync(BuildTunnelLaunchOptions());
        NotifySchemaChanged();
        RefreshTunnelBarText();
        if (!TunnelReady && !string.IsNullOrWhiteSpace(TunnelMessage))
        {
            SetError(TunnelMessage);
        }
    }

    private async Task InstallTailscaleAsync()
    {
        ClearError();
        await _tunnelService.InstallTailscaleAsync();
    }

    private static void OpenGptBuilder()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://chatgpt.com/gpts/editor",
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Best effort.
        }
    }
}
