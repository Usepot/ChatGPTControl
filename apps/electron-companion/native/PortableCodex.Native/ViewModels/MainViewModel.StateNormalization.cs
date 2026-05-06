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
    private void NormalizeTrustedWorkspaces()
    {
        var normalized = TrustedWorkspaces
            .Select(_pathPolicy.NormalizeWorkspaceRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        TrustedWorkspaces.Clear();
        foreach (var workspace in normalized)
        {
            TrustedWorkspaces.Add(workspace);
        }
    }

    private void NormalizeSkillRoots()
    {
        var normalized = SkillRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SkillRoots.Clear();
        foreach (var skillRoot in normalized)
        {
            SkillRoots.Add(skillRoot);
        }
    }

    private void RefreshSkillList()
    {
        Skills.Clear();
        foreach (var skill in _skillService.GetSkillList(BuildCurrentSettings()))
        {
            Skills.Add(skill);
        }

        OnPropertyChanged(nameof(HasSkills));
        OnPropertyChanged(nameof(ViewAllSkillsButtonText));
        OnPropertyChanged(nameof(SkillSummaryText));
    }
}
