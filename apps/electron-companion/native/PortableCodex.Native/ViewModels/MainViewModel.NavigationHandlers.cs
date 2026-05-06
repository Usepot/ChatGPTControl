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
    #region Navigation handlers

    private async Task NextStepAsync()
    {
        if (CurrentStep == StepSetup)
        {
            PersistState();
        }

        if (CurrentStep == StepWorkspaces)
        {
            PersistState();
            // Drop the device WebSocket before stopping Kestrel; otherwise StopAsync can wait a long time
            // while graceful shutdown drains the still-open /ws/device connection.
            await _relayClientService.DisconnectAsync();
            await _localRelayService.RestartAsync(BuildCurrentSettings());
            _relayClientService.Connect();
        }

        if (CurrentStep < StepReady)
        {
            CurrentStep++;
        }
    }

    private void PrevStep()
    {
        if (CurrentStep > StepSetup)
        {
            CurrentStep--;
        }
    }

    #endregion
}
