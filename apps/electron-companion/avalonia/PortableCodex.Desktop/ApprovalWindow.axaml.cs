using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PortableCodex.Desktop;

public partial class ApprovalWindow : Window
{
    public ApprovalWindow() : this("Review the requested command or file change.")
    {
    }

    public ApprovalWindow(string summary)
        : this(
            "One action needs your approval",
            summary,
            "This request came through the authenticated relay and will run inside your trusted workspace.",
            "Approve")
    {
    }

    public ApprovalWindow(string heading, string summary, string detail, string approveLabel)
    {
        InitializeComponent();
        DataContext = new ApprovalViewModel(heading, summary, detail, approveLabel);
    }

    private void OnDenyClick(object? sender, RoutedEventArgs e) => Close(false);
    private void OnApproveClick(object? sender, RoutedEventArgs e) => Close(true);

    private sealed record ApprovalViewModel(string Heading, string Summary, string Detail, string ApproveLabel);
}
