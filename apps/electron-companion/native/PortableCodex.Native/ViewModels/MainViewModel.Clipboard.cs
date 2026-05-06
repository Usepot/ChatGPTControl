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
    #region Clipboard helpers

    private void CopySchemaWithFeedback()
    {
        CopyText(MinifiedOpenApiSchema);
        SchemaCopiedLabel = "Copied!";
        ResetLabelAfterDelay(v => SchemaCopiedLabel = v, "Copy");
    }

    private void CopyMcpEndpointWithFeedback()
    {
        CopyText(McpEndpointUrl);
        McpEndpointCopiedLabel = "Copied!";
        ResetLabelAfterDelay(v => McpEndpointCopiedLabel = v, "Copy");
    }

    private void CopyInstructionsWithFeedback()
    {
        CopyText(RelayContentService.GptInstructions);
        InstructionsCopiedLabel = "Copied!";
        ResetLabelAfterDelay(v => InstructionsCopiedLabel = v, "Copy prompt");
    }

    private void CopyTokenWithFeedback()
    {
        CopyText(GptApiToken);
        TokenCopiedLabel = "Copied!";
        ResetLabelAfterDelay(v => TokenCopiedLabel = v, "Copy");
    }

    private void CopyTunnelWithFeedback()
    {
        CopyText(TunnelUrl);
        TunnelCopiedLabel = "Copied!";
        ResetLabelAfterDelay(v => TunnelCopiedLabel = v, "Copy");
    }

    private void SelectIntegrationMode(string? mode)
    {
        IntegrationMode = mode ?? "mcp";
    }

    private void CopyText(string? value)
    {
        ClearError();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            WpfClipboard.SetText(value);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private static void ResetLabelAfterDelay(Action<string> setter, string resetValue)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            await RunOnUiThreadAsync(() => setter(resetValue));
        });
    }

    #endregion
}
