using PortableCodex.Native.Utils;

namespace PortableCodex.Native.ViewModels;

/// <summary>
/// One node in the setup wizard stepper. The view binds a single templated
/// ItemsControl to a collection of these instead of hand-duplicating markup
/// per step, so adding/relabeling a step is a data change, not layout surgery.
/// </summary>
public sealed class WizardStepItem : ObservableObject
{
    private string _label = string.Empty;
    private string _state = "upcoming";
    private bool _connectorComplete;

    public WizardStepItem(int index, string number, bool isCheckmark, bool showConnector)
    {
        Index = index;
        Number = number;
        IsCheckmark = isCheckmark;
        ShowConnector = showConnector;
    }

    public int Index { get; }

    public string Number { get; }

    public bool IsCheckmark { get; }

    public bool ShowConnector { get; }

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    /// <summary>"complete", "active", or "upcoming".</summary>
    public string State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    public bool ConnectorComplete
    {
        get => _connectorComplete;
        set => SetProperty(ref _connectorComplete, value);
    }
}
