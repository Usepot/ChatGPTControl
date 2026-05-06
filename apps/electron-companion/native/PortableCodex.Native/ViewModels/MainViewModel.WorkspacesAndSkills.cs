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
    #region Workspace management

    private void AddWorkspace()
    {
        ClearError();
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select a trusted workspace root",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var normalized = _pathPolicy.NormalizeWorkspaceRoot(dialog.SelectedPath);
        if (TrustedWorkspaces.Any(w => string.Equals(w, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        TrustedWorkspaces.Add(normalized);
        NormalizeTrustedWorkspaces();
        if (string.IsNullOrWhiteSpace(CurrentWorkspace))
        {
            CurrentWorkspace = normalized;
        }
        PersistState();
    }

    private void AddSkill()
    {
        ClearError();
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select a skill folder that contains SKILL.md",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var normalized = Path.GetFullPath(dialog.SelectedPath);
        if (!File.Exists(Path.Combine(normalized, "SKILL.md")))
        {
            SetError("That folder does not contain SKILL.md.");
            return;
        }

        if (SkillRoots.Any(w => string.Equals(w, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        SkillRoots.Add(normalized);
        NormalizeSkillRoots();
        PersistState();
    }

    private void RemoveSkill(string? skillPath)
    {
        if (string.IsNullOrWhiteSpace(skillPath))
        {
            return;
        }

        var existing = SkillRoots.FirstOrDefault(
            root => string.Equals(root, skillPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SkillRoots.Remove(existing);
            PersistState();
        }
    }

    private void ViewSkill(object? parameter)
    {
        ClearError();
        if (parameter is not SkillListEntry skill || string.IsNullOrWhiteSpace(skill.Path))
        {
            return;
        }

        var skillFile = Path.Combine(skill.Path, "SKILL.md");
        if (!File.Exists(skillFile))
        {
            SetError($"SKILL.md was not found for {skill.Activation}.");
            return;
        }

        string markdown;
        try
        {
            markdown = File.ReadAllText(skillFile);
        }
        catch (Exception ex)
        {
            SetError($"Could not read {skill.Activation}: {ex.Message}");
            return;
        }

        ShowSkillMarkdownWindow(skill, skillFile, markdown);
    }

    private static void ShowSkillMarkdownWindow(SkillListEntry skill, string skillFile, string markdown)
    {
        var header = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        };

        var title = new System.Windows.Controls.TextBlock
        {
            Text = skill.Activation,
            FontSize = 18,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        };
        title.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "PrimaryFg");

        var path = new System.Windows.Controls.TextBlock
        {
            Text = skillFile,
            FontSize = 11,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas"),
            TextWrapping = System.Windows.TextWrapping.Wrap,
        };
        path.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TertiaryFg");

        header.Children.Add(title);
        header.Children.Add(path);

        var markdownBox = new System.Windows.Controls.TextBox
        {
            Text = markdown,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas"),
            FontSize = 12,
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(14, 12, 14, 12),
        };
        markdownBox.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "CodeBg");
        markdownBox.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "CodeFg");
        markdownBox.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "SubtleBorder");

        var content = new System.Windows.Controls.DockPanel
        {
            LastChildFill = true,
            Margin = new System.Windows.Thickness(18),
        };
        System.Windows.Controls.DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        content.Children.Add(header);
        content.Children.Add(markdownBox);

        var window = new System.Windows.Window
        {
            Title = $"{skill.Activation} SKILL.md",
            Width = 840,
            Height = 640,
            MinWidth = 520,
            MinHeight = 360,
            Owner = WpfApp.Current.MainWindow,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Content = content,
        };
        window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AppBg");
        window.ShowDialog();
    }

    private void ImportCodexCliWorkspacesNow()
    {
        ClearError();
        var added = false;
        try
        {
            foreach (var workspace in _codexCliWorkspaceService.GetWorkspaceRoots())
            {
                var normalized = _pathPolicy.NormalizeWorkspaceRoot(workspace);
                if (TrustedWorkspaces.Any(w => string.Equals(w, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                TrustedWorkspaces.Add(normalized);
                added = true;
            }
        }
        catch (Exception ex)
        {
            SetError($"Could not import Codex CLI workspaces: {ex.Message}");
            return;
        }

        if (added)
        {
            NormalizeTrustedWorkspaces();
        }

        if (string.IsNullOrWhiteSpace(CurrentWorkspace) && TrustedWorkspaces.Count > 0)
        {
            CurrentWorkspace = TrustedWorkspaces[0];
        }
    }

    private void ImportCodexCliSkillsNow()
    {
        ClearError();
        var added = false;
        try
        {
            foreach (var skillRoot in _codexCliWorkspaceService.GetSkillRoots())
            {
                var normalized = Path.GetFullPath(skillRoot);
                if (SkillRoots.Any(w => string.Equals(w, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                SkillRoots.Add(normalized);
                added = true;
            }
        }
        catch (Exception ex)
        {
            SetError($"Could not import Codex CLI skills: {ex.Message}");
            return;
        }

        if (added)
        {
            NormalizeSkillRoots();
        }
    }

    private void TrustWholeSystem()
    {
        ClearError();
        var result = WpfMessageBox.Show(
            "This will trust every ready drive root, such as C:\\, so Codex can access files anywhere under those drives. Write approval still applies when enabled. Continue?",
            "Allow full system file access",
            WpfMessageBoxButton.YesNo,
            WpfMessageBoxImage.Warning);
        if (result != WpfMessageBoxResult.Yes)
        {
            return;
        }

        foreach (var root in GetSystemRoots())
        {
            var normalized = _pathPolicy.NormalizeWorkspaceRoot(root);
            if (TrustedWorkspaces.Any(w => string.Equals(w, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            TrustedWorkspaces.Add(normalized);
        }

        NormalizeTrustedWorkspaces();
        PersistState();
    }

    private static IEnumerable<string> GetSystemRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return "/";
            yield break;
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }

    private void RemoveWorkspace(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return;
        }

        var existing = TrustedWorkspaces.FirstOrDefault(
            w => string.Equals(w, workspace, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            TrustedWorkspaces.Remove(existing);
            if (string.Equals(CurrentWorkspace, existing, StringComparison.OrdinalIgnoreCase))
            {
                CurrentWorkspace = TrustedWorkspaces.FirstOrDefault() ?? string.Empty;
            }
            PersistState();
        }
    }

    private void ViewAllWorkspaces()
    {
        var message = TrustedWorkspaces.Count == 0
            ? "No workspaces added."
            : string.Join(Environment.NewLine, TrustedWorkspaces);

        WpfMessageBox.Show(
            message,
            "Trusted workspaces",
            WpfMessageBoxButton.OK,
            WpfMessageBoxImage.Information);
    }

    private void ViewAllSkills()
    {
        var message = Skills.Count == 0
            ? "No skills added."
            : string.Join(Environment.NewLine, Skills.Select(skill => $"{skill.Activation} — {skill.Path}"));

        WpfMessageBox.Show(
            message,
            "Skills",
            WpfMessageBoxButton.OK,
            WpfMessageBoxImage.Information);
    }

    #endregion
}
