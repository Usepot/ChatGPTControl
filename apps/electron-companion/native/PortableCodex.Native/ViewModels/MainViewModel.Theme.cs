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
                Wpf.Ui.Controls.WindowBackdropType.None,
                true);
        }
        catch
        {
            // Best effort: keep the app usable if WPF UI theme switching fails.
        }

        // Single source of truth for the live palette. Every token the views
        // reference is set here so runtime styling matches the design-time
        // resources. Dark mode follows the diff viewer's charcoal palette:
        // one deep background with only slight sidebar/panel separation.

        // Accent — soft diff-viewer blue in dark mode.
        SetBrush("AccentBrush", isDarkMode ? "#73C2FF" : "#7C3AED");
        SetBrush("AccentHoverBrush", isDarkMode ? "#7EC7FF" : "#8B5CF6");
        SetBrush("AccentPressedBrush", isDarkMode ? "#63A7DC" : "#6D28D9");
        SetBrush("AccentSubtleBg", isDarkMode ? "#2673C2FF" : "#F1ECFE");
        SetBrush("OnAccentFg", "#FFFFFF");
        SetBrush("SuccessBrush", isDarkMode ? "#4ADE80" : "#059669");
        SetBrush("ErrorBrush", isDarkMode ? "#FCA5A5" : "#E11D48");

        // Surfaces — same charcoal family, separated by very slight shifts.
        SetBrush("AppBg", isDarkMode ? "#242424" : "#F7F7F8");
        SetBrush("PanelBg", isDarkMode ? "#232323" : "#FFFFFF");
        SetBrush("CardBg", isDarkMode ? "#262626" : "#FFFFFF");
        SetBrush("CardMutedBg", isDarkMode ? "#2A2A2E" : "#F4F4F5");
        SetBrush("ElevatedBg", isDarkMode ? "#303032" : "#FFFFFF");
        SetBrush("HoverBg", isDarkMode ? "#2D2D2F" : "#F0F0F1");
        SetBrush("SelectedBg", isDarkMode ? "#29323A" : "#F1ECFE");
        SetBrush("SelectedBorder", isDarkMode ? "#4D73C2FF" : "#C4B5FD");
        SetBrush("SurfaceBg", isDarkMode ? "#242424" : "#F7F7F8");

        // Text — stepped contrast.
        SetBrush("PrimaryFg", isDarkMode ? "#F0F0F0" : "#18181B");
        SetBrush("SecondaryFg", isDarkMode ? "#A1A1AA" : "#3F3F46");
        SetBrush("MutedFg", isDarkMode ? "#8F8F96" : "#71717A");
        SetBrush("TertiaryFg", isDarkMode ? "#71717A" : "#A1A1AA");

        // Borders.
        SetBrush("SubtleBorder", isDarkMode ? "#343434" : "#E4E4E7");
        SetBrush("SoftBorder", isDarkMode ? "#373737" : "#EFEFF1");
        SetBrush("DividerBrush", isDarkMode ? "#343434" : "#EAEAEC");

        // Code.
        SetBrush("CodeBg", isDarkMode ? "#222222" : "#F4F4F5");
        SetBrush("CodeFg", isDarkMode ? "#F0F0F0" : "#27272A");

        // Semantic state tints.
        SetBrush("SuccessSubtleBg", isDarkMode ? "#1F22C55E" : "#ECFDF5");
        SetBrush("SuccessSubtleBorder", isDarkMode ? "#4022C55E" : "#A7F3D0");
        SetBrush("SuccessSubtleFg", isDarkMode ? "#4ADE80" : "#047857");
        SetBrush("PendingSubtleBg", isDarkMode ? "#2A2A2E" : "#F4F4F5");
        SetBrush("WarningSubtleBg", isDarkMode ? "#1FEAB308" : "#FFFBEB");
        SetBrush("WarningSubtleBorder", isDarkMode ? "#40EAB308" : "#FDE68A");
        SetBrush("WarningSubtleFg", isDarkMode ? "#FDE68A" : "#92400E");
        SetBrush("ErrorSubtleBg", isDarkMode ? "#1FEF4444" : "#FEF2F2");
        SetBrush("ErrorSubtleBorder", isDarkMode ? "#40EF4444" : "#FECACA");
        SetBrush("ErrorSubtleFg", isDarkMode ? "#FCA5A5" : "#B91C1C");

        // Step indicator.
        SetBrush("StepActiveBg", isDarkMode ? "#73C2FF" : "#7C3AED");
        SetBrush("StepCompleteBg", isDarkMode ? "#22C55E" : "#059669");
        SetBrush("StepInactiveBg", isDarkMode ? "#2A2A2E" : "#E4E4E7");
        SetBrush("StepLineActive", isDarkMode ? "#73C2FF" : "#7C3AED");
        SetBrush("StepLineInactive", isDarkMode ? "#343434" : "#E4E4E7");
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
