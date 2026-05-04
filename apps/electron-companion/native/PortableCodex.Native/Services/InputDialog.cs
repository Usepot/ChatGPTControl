using System.Windows;
using System.Windows.Controls;

namespace PortableCodex.Native.Services;

/// <summary>
/// A minimal code-only WPF dialog that prompts the user to enter a single line of text.
/// </summary>
internal sealed class InputDialog : Window
{
    private readonly TextBox _textBox;

    public InputDialog(string title, string prompt)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current?.MainWindow;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(label, 0);
        grid.Children.Add(label);

        _textBox = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 12),
            AcceptsReturn = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetRow(_textBox, 1);
        grid.Children.Add(_textBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetRow(buttonPanel, 2);
        grid.Children.Add(buttonPanel);

        var okButton = new Button
        {
            Content = "OK",
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };
        okButton.Click += (_, _) => { DialogResult = true; };
        buttonPanel.Children.Add(okButton);

        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 80,
            IsCancel = true,
        };
        cancelButton.Click += (_, _) => { DialogResult = false; };
        buttonPanel.Children.Add(cancelButton);

        Content = grid;
    }

    /// <summary>Gets the text the user entered, or an empty string if the dialog was cancelled.</summary>
    public string ResponseText => _textBox.Text ?? string.Empty;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _textBox.Focus();
    }
}
