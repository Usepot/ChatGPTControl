using System.Windows;
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
}
