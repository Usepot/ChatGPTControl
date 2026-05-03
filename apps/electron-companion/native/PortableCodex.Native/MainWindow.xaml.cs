using System.Windows;
using System.Windows.Input;
using PortableCodex.Native.ViewModels;

namespace PortableCodex.Native;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        StateChanged += (_, _) => UpdateChromeMaximizeGlyph();
        UpdateChromeMaximizeGlyph();
    }

    protected override async void OnClosed(EventArgs e)
    {
        await _viewModel.ShutdownAsync();
        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private void UpdateChromeMaximizeGlyph()
    {
        if (ChromeMaximizeButton == null)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            ChromeMaximizeButton.Content = "\uE923";
            ChromeMaximizeButton.ToolTip = "Restore down";
        }
        else
        {
            ChromeMaximizeButton.Content = "\uE922";
            ChromeMaximizeButton.ToolTip = "Maximize";
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2)
        {
            return;
        }

        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void ChromeMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void ChromeMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void ChromeClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
