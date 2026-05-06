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
    private void RaiseNavigationChanged()
    {
        OnPropertyChanged(nameof(CanGoNext));
        NextStepCommand.RaiseCanExecuteChanged();
        ApplyTunnelSettingsCommand.RaiseCanExecuteChanged();
    }

    private void TryAutoAdvanceFromSetupAfterTunnelReady()
    {
        if (CurrentStep != StepSetup || !CredentialsReady || !RelayReady || !TunnelReady)
        {
            return;
        }

        PersistState();
        CurrentStep = StepCreateGpt;
    }

    private int ResolveInitialStep(bool isReturningUser)
    {
        if (!CredentialsReady || !RelayReady)
        {
            return StepSetup;
        }

        if (!isReturningUser)
        {
            return CurrentStep;
        }

        return TrustedWorkspaces.Count == 0 ? StepWorkspaces : StepReady;
    }

    private void SyncCurrentStepWithState()
    {
        if (_isLoadingState)
        {
            return;
        }

        if (TrustedWorkspaces.Count == 0 && (CurrentStep == StepReady || CurrentStep == StepDiffViewer))
        {
            CurrentStep = StepWorkspaces;
        }
    }
}
