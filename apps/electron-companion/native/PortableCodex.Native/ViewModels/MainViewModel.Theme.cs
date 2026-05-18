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

        // Single source of truth for the live palette. Every token the views
        // reference is set here so the design never drifts back to a partial
        // override (which previously left a purple accent fighting a blue
        // selection state). One violet accent, opaque neutral surfaces.

        // Accent — identical hue in both modes, single source of brand color.
        SetBrush("AccentBrush", isDarkMode ? "#8B5CF6" : "#7C3AED");
        SetBrush("AccentHoverBrush", isDarkMode ? "#A78BFA" : "#8B5CF6");
        SetBrush("AccentPressedBrush", isDarkMode ? "#7C3AED" : "#6D28D9");
        SetBrush("AccentSubtleBg", isDarkMode ? "#2A2440" : "#F1ECFE");
        SetBrush("OnAccentFg", "#FFFFFF");
        SetBrush("SuccessBrush", isDarkMode ? "#34D399" : "#059669");
        SetBrush("ErrorBrush", isDarkMode ? "#FB7185" : "#E11D48");

        // Surfaces — opaque, neutral zinc, layered by elevation.
        SetBrush("AppBg", isDarkMode ? "#0B0B0D" : "#F7F7F8");
        SetBrush("PanelBg", isDarkMode ? "#121214" : "#FFFFFF");
        SetBrush("CardBg", isDarkMode ? "#161618" : "#FFFFFF");
        SetBrush("CardMutedBg", isDarkMode ? "#1C1C1F" : "#F4F4F5");
        SetBrush("ElevatedBg", isDarkMode ? "#202023" : "#FFFFFF");
        SetBrush("HoverBg", isDarkMode ? "#232327" : "#F0F0F1");
        SetBrush("SelectedBg", isDarkMode ? "#241E33" : "#F1ECFE");
        SetBrush("SelectedBorder", isDarkMode ? "#4C3A7A" : "#C4B5FD");
        SetBrush("SurfaceBg", isDarkMode ? "#0B0B0D" : "#F7F7F8");

        // Text — stepped contrast.
        SetBrush("PrimaryFg", isDarkMode ? "#FAFAFA" : "#18181B");
        SetBrush("SecondaryFg", isDarkMode ? "#C8C8CC" : "#3F3F46");
        SetBrush("MutedFg", isDarkMode ? "#9A9AA2" : "#71717A");
        SetBrush("TertiaryFg", isDarkMode ? "#74747C" : "#A1A1AA");

        // Borders.
        SetBrush("SubtleBorder", isDarkMode ? "#2A2A2E" : "#E4E4E7");
        SetBrush("SoftBorder", isDarkMode ? "#1F1F22" : "#EFEFF1");
        SetBrush("DividerBrush", isDarkMode ? "#232327" : "#EAEAEC");

        // Code.
        SetBrush("CodeBg", isDarkMode ? "#0E0E10" : "#F4F4F5");
        SetBrush("CodeFg", isDarkMode ? "#D4D4D8" : "#27272A");

        // Semantic state tints.
        SetBrush("SuccessSubtleBg", isDarkMode ? "#14271F" : "#ECFDF5");
        SetBrush("SuccessSubtleBorder", isDarkMode ? "#1F4D3A" : "#A7F3D0");
        SetBrush("SuccessSubtleFg", isDarkMode ? "#6EE7B7" : "#047857");
        SetBrush("PendingSubtleBg", isDarkMode ? "#1C1C1F" : "#F4F4F5");
        SetBrush("WarningSubtleBg", isDarkMode ? "#2A2113" : "#FFFBEB");
        SetBrush("WarningSubtleBorder", isDarkMode ? "#4D3B14" : "#FDE68A");
        SetBrush("WarningSubtleFg", isDarkMode ? "#FCD34D" : "#92400E");
        SetBrush("ErrorSubtleBg", isDarkMode ? "#2A1620" : "#FEF2F2");
        SetBrush("ErrorSubtleBorder", isDarkMode ? "#4D2230" : "#FECACA");
        SetBrush("ErrorSubtleFg", isDarkMode ? "#FDA4AF" : "#B91C1C");

        // Step indicator.
        SetBrush("StepActiveBg", isDarkMode ? "#8B5CF6" : "#7C3AED");
        SetBrush("StepCompleteBg", isDarkMode ? "#34D399" : "#059669");
        SetBrush("StepInactiveBg", isDarkMode ? "#2E2E33" : "#E4E4E7");
        SetBrush("StepLineActive", isDarkMode ? "#8B5CF6" : "#7C3AED");
        SetBrush("StepLineInactive", isDarkMode ? "#2A2A2E" : "#E4E4E7");
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
