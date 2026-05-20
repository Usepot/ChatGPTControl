using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
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
    private static readonly SolidColorBrush Completed = Freeze(new SolidColorBrush(WpfColor.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly SolidColorBrush Active = Freeze(new SolidColorBrush(WpfColor.FromRgb(0x73, 0xC2, 0xFF)));
    private static readonly SolidColorBrush Upcoming = Freeze(new SolidColorBrush(WpfColor.FromRgb(0x2A, 0x2A, 0x2E)));

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
    private static readonly SolidColorBrush ActiveOrDoneFallback = Freeze(new SolidColorBrush(WpfColor.FromRgb(244, 244, 245)));
    private static readonly SolidColorBrush InactiveFallback = Freeze(new SolidColorBrush(WpfColor.FromRgb(113, 113, 122)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int current && parameter is string s && int.TryParse(s, out var step))
        {
            return step <= current
                ? GetThemeBrush("PrimaryFg", ActiveOrDoneFallback)
                : GetThemeBrush("TertiaryFg", InactiveFallback);
        }

        return GetThemeBrush("TertiaryFg", InactiveFallback);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush GetThemeBrush(string key, SolidColorBrush fallback)
    {
        return System.Windows.Application.Current?.Resources[key] as SolidColorBrush ?? fallback;
    }
}

public sealed class StepLineBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Done = Freeze(new SolidColorBrush(WpfColor.FromRgb(0x73, 0xC2, 0xFF)));
    private static readonly SolidColorBrush Pending = Freeze(new SolidColorBrush(WpfColor.FromRgb(0x34, 0x34, 0x34)));

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
    private static readonly SolidColorBrush Connected = Freeze(new SolidColorBrush(WpfColor.FromRgb(0x4A, 0xDE, 0x80)));
    private static readonly SolidColorBrush Connecting = Freeze(new SolidColorBrush(WpfColor.FromRgb(0xFD, 0xE6, 0x8A)));
    private static readonly SolidColorBrush Error = Freeze(new SolidColorBrush(WpfColor.FromRgb(0xFC, 0xA5, 0xA5)));
    private static readonly SolidColorBrush Offline = Freeze(new SolidColorBrush(WpfColor.FromRgb(0x71, 0x71, 0x7A)));

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

/// <summary>
/// Turns a unified diff string into a FlowDocument with subtle color-coding:
/// hunk headers in accent, additions in green, deletions in rose, and file
/// headers in muted white. Intentionally renders the text monospaced so the
/// diff still reads like the raw output, just easier on the eyes.
/// </summary>
public sealed class DiffToFlowDocumentConverter : IValueConverter
{
    private static readonly SolidColorBrush AddedFg = Freeze(new SolidColorBrush(WpfColor.FromRgb(0x6E, 0xE7, 0xB7)));
    private static readonly SolidColorBrush RemovedFg = Freeze(new SolidColorBrush(WpfColor.FromRgb(0xFD, 0xA4, 0xAF)));
    private static readonly SolidColorBrush HunkFg = Freeze(new SolidColorBrush(WpfColor.FromRgb(0xC4, 0xB5, 0xFD)));
    private static readonly SolidColorBrush FileFg = Freeze(new SolidColorBrush(WpfColor.FromRgb(0xE5, 0xE5, 0xE5)));
    private static readonly SolidColorBrush MetaFg = Freeze(new SolidColorBrush(WpfColor.FromRgb(0x88, 0x88, 0x88)));
    private static readonly SolidColorBrush BodyFg = Freeze(new SolidColorBrush(WpfColor.FromRgb(0xD4, 0xD4, 0xD4)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 12,
            LineHeight = 17,
        };

        var text = value as string ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return doc;
        }

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = 17,
        };

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var run = new Run(line) { Foreground = ColorFor(line) };
            paragraph.Inlines.Add(run);
            if (i < lines.Length - 1)
            {
                paragraph.Inlines.Add(new LineBreak());
            }
        }

        doc.Blocks.Add(paragraph);
        return doc;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static System.Windows.Media.Brush ColorFor(string line)
    {
        if (string.IsNullOrEmpty(line)) return BodyFg;
        if (line.StartsWith("@@", StringComparison.Ordinal)) return HunkFg;
        if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)) return FileFg;
        if (line.StartsWith("diff ", StringComparison.Ordinal) ||
            line.StartsWith("index ", StringComparison.Ordinal) ||
            line.StartsWith("new file", StringComparison.Ordinal) ||
            line.StartsWith("deleted file", StringComparison.Ordinal) ||
            line.StartsWith("similarity ", StringComparison.Ordinal) ||
            line.StartsWith("rename ", StringComparison.Ordinal) ||
            line.StartsWith("Binary ", StringComparison.Ordinal))
        {
            return MetaFg;
        }
        if (line.StartsWith('+')) return AddedFg;
        if (line.StartsWith('-')) return RemovedFg;
        return BodyFg;
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Returns true when the bound string is non-empty AND is not the default idle
/// placeholder. Used to swap between the empty-state illustration and the diff
/// body in DiffViewerView.
/// </summary>
public sealed class DiffHasContentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Heuristic: real diffs include at least one hunk header or a +/- line.
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal)) return true;
            if (line.Length > 0 && (line[0] == '+' || line[0] == '-') &&
                !line.StartsWith("+++", StringComparison.Ordinal) &&
                !line.StartsWith("---", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
