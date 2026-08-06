using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PortableCodex.Desktop;

public partial class ApprovalWindow : Window
{
    public ApprovalWindow() : this("Review the requested command or file change.")
    {
    }

    public ApprovalWindow(string summary)
    {
        InitializeComponent();
        DataContext = new ApprovalViewModel(summary);
    }

    private void OnDenyClick(object? sender, RoutedEventArgs e) => Close(false);
    private void OnApproveClick(object? sender, RoutedEventArgs e) => Close(true);

    private sealed record ApprovalViewModel(string Summary);
}
