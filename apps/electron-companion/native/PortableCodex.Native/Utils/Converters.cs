using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;

namespace PortableCodex.Native.Utils;

public sealed class StepVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int current && parameter is string s && int.TryParse(s, out var target))
        {
            return current == target ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StepBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Completed = Freeze(new SolidColorBrush(WpfColor.FromRgb(16, 185, 129)));
    private static readonly SolidColorBrush Active = Freeze(new SolidColorBrush(WpfColor.FromRgb(59, 130, 246)));
    private static readonly SolidColorBrush Upcoming = Freeze(new SolidColorBrush(WpfColor.FromRgb(209, 213, 219)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int current && parameter is string s && int.TryParse(s, out var step))
        {
            if (step < current) return Completed;
            if (step == current) return Active;
            return Upcoming;
        }

        return Upcoming;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

public sealed class StepForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush ActiveOrDone = Freeze(new SolidColorBrush(WpfColor.FromRgb(17, 24, 39)));
    private static readonly SolidColorBrush Inactive = Freeze(new SolidColorBrush(WpfColor.FromRgb(156, 163, 175)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int current && parameter is string s && int.TryParse(s, out var step))
        {
            return step <= current ? ActiveOrDone : Inactive;
        }

        return Inactive;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

public sealed class StepLineBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Done = Freeze(new SolidColorBrush(WpfColor.FromRgb(16, 185, 129)));
    private static readonly SolidColorBrush Pending = Freeze(new SolidColorBrush(WpfColor.FromRgb(229, 231, 235)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int current && parameter is string s && int.TryParse(s, out var afterStep))
        {
            return current > afterStep ? Done : Pending;
        }

        return Pending;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StatusDotBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Connected = Freeze(new SolidColorBrush(WpfColor.FromRgb(16, 185, 129)));
    private static readonly SolidColorBrush Connecting = Freeze(new SolidColorBrush(WpfColor.FromRgb(245, 158, 11)));
    private static readonly SolidColorBrush Error = Freeze(new SolidColorBrush(WpfColor.FromRgb(239, 68, 68)));
    private static readonly SolidColorBrush Offline = Freeze(new SolidColorBrush(WpfColor.FromRgb(156, 163, 175)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString()?.ToLowerInvariant() switch
        {
            "connected" or "running" => Connected,
            "connecting" or "starting" or "approval_required" => Connecting,
            "error" or "not_found" or "login_required" => Error,
            _ => Offline,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

public sealed class StateVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var currentState = value?.ToString() ?? string.Empty;
        var targetState = parameter?.ToString() ?? string.Empty;
        return string.Equals(currentState, targetState, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseStateVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var currentState = value?.ToString() ?? string.Empty;
        var targetState = parameter?.ToString() ?? string.Empty;
        return string.Equals(currentState, targetState, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
