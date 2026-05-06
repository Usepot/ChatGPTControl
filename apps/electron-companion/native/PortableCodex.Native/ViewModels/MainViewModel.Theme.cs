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
    private static void ApplyTheme(bool isDarkMode)
    {
        try
        {
            ApplicationThemeManager.Apply(
                isDarkMode ? ApplicationTheme.Dark : ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.Mica,
                true);
        }
        catch
        {
            // Best effort: keep the app usable if WPF UI theme switching fails.
        }

        SetBrush("AppBg", isDarkMode ? "#0F172A" : "#F8FAFC");
        SetBrush("CardBg", isDarkMode ? "#111827" : "#FFFFFF");
        SetBrush("CardMutedBg", isDarkMode ? "#1E293B" : "#F9FAFB");
        SetBrush("HoverBg", isDarkMode ? "#1F2937" : "#F3F4F6");
        SetBrush("SelectedBg", isDarkMode ? "#172554" : "#EFF6FF");
        SetBrush("SelectedBorder", isDarkMode ? "#2563EB" : "#BFDBFE");
        SetBrush("PrimaryFg", isDarkMode ? "#F8FAFC" : "#111827");
        SetBrush("SecondaryFg", isDarkMode ? "#CBD5E1" : "#374151");
        SetBrush("MutedFg", isDarkMode ? "#94A3B8" : "#6B7280");
        SetBrush("TertiaryFg", isDarkMode ? "#64748B" : "#9CA3AF");
        SetBrush("SubtleBorder", isDarkMode ? "#334155" : "#E5E7EB");
        SetBrush("SoftBorder", isDarkMode ? "#1E293B" : "#F3F4F6");
        SetBrush("SurfaceBg", isDarkMode ? "#0F172A" : "#F9FAFB");
        SetBrush("CodeBg", isDarkMode ? "#020617" : "#F4F4F5");
        SetBrush("CodeFg", isDarkMode ? "#E5E7EB" : "#1F2937");
        SetBrush("SuccessSubtleBg", isDarkMode ? "#064E3B" : "#ECFDF5");
        SetBrush("SuccessSubtleBorder", isDarkMode ? "#059669" : "#A7F3D0");
        SetBrush("SuccessSubtleFg", isDarkMode ? "#A7F3D0" : "#047857");
        SetBrush("PendingSubtleBg", isDarkMode ? "#1F2937" : "#F9FAFB");
        SetBrush("WarningSubtleBg", isDarkMode ? "#451A03" : "#FFFBEB");
        SetBrush("WarningSubtleBorder", isDarkMode ? "#B45309" : "#FDE68A");
        SetBrush("WarningSubtleFg", isDarkMode ? "#FDE68A" : "#92400E");
        SetBrush("ErrorSubtleBg", isDarkMode ? "#450A0A" : "#FEF2F2");
        SetBrush("ErrorSubtleBorder", isDarkMode ? "#991B1B" : "#FECACA");
        SetBrush("ErrorSubtleFg", isDarkMode ? "#FCA5A5" : "#B91C1C");
    }

    private static void SetBrush(string key, string color)
    {
        if (WpfApp.Current?.Resources is not { } resources)
        {
            return;
        }

        var parsedColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
        if (resources[key] is System.Windows.Media.SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = parsedColor;
            return;
        }

        resources[key] = new System.Windows.Media.SolidColorBrush(parsedColor);
    }
}
