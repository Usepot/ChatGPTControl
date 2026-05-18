using System.Collections.ObjectModel;

namespace PortableCodex.Native.ViewModels;

public sealed partial class MainViewModel
{
    public ObservableCollection<WizardStepItem> WizardSteps { get; } = new()
    {
        new WizardStepItem(StepSetup, "1", isCheckmark: false, showConnector: true),
        new WizardStepItem(StepCreateGpt, "2", isCheckmark: false, showConnector: true),
        new WizardStepItem(StepConfigureAction, "3", isCheckmark: false, showConnector: true),
        new WizardStepItem(StepWorkspaces, "4", isCheckmark: false, showConnector: true),
        new WizardStepItem(StepReady, "5", isCheckmark: true, showConnector: false),
    };

    private void RefreshWizardSteps()
    {
        foreach (var step in WizardSteps)
        {
            step.Label = step.Index switch
            {
                StepSetup => "Setup",
                StepCreateGpt => StepCreateLabel,
                StepConfigureAction => StepConfigureLabel,
                StepWorkspaces => "Workspaces",
                _ => "Ready",
            };

            step.State = step.Index < CurrentStep
                ? "complete"
                : step.Index == CurrentStep
                    ? "active"
                    : "upcoming";

            step.ConnectorComplete = CurrentStep > step.Index;
        }
    }
}
