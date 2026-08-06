using System.Diagnostics;
using PortableCodex.Native.Models;
using PortableCodex.Native.Services;

namespace PortableCodex.Core.Services;

public interface IAppPaths
{
    string DataDirectory { get; }
    string StateFilePath { get; }
    string WebViewDataDirectory { get; }
}

public sealed class DefaultAppPaths : IAppPaths
{
    public DefaultAppPaths(string productName = "PortableCodex")
    {
        var appData = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        DataDirectory = Path.Combine(
            string.IsNullOrWhiteSpace(appData) ? AppContext.BaseDirectory : appData,
            productName);
        StateFilePath = Path.Combine(DataDirectory, "portable-codex-state.json");
        WebViewDataDirectory = Path.Combine(DataDirectory, "webview");
    }

    public string DataDirectory { get; }
    public string StateFilePath { get; }
    public string WebViewDataDirectory { get; }
}

public interface IProcessLauncher
{
    bool Open(string target);
}

public sealed class DefaultProcessLauncher : IProcessLauncher
{
    public bool Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>UI boundary for copying secrets without making the core depend on a desktop toolkit.</summary>
public interface IClipboardService
{
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>UI boundary for choosing a trusted workspace directory.</summary>
public interface IWorkspacePicker
{
    Task<string?> PickWorkspaceAsync(CancellationToken cancellationToken = default);
}

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    bool RedirectOutput = true);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Process execution boundary for platform adapters and test doubles.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

public sealed class DefaultProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                WorkingDirectory = request.WorkingDirectory ?? string.Empty,
                UseShellExecute = false,
                RedirectStandardOutput = request.RedirectOutput,
                RedirectStandardError = request.RedirectOutput,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in request.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdoutTask = request.RedirectOutput
            ? process.StandardOutput.ReadToEndAsync(cancellationToken)
            : Task.FromResult(string.Empty);
        var stderrTask = request.RedirectOutput
            ? process.StandardError.ReadToEndAsync(cancellationToken)
            : Task.FromResult(string.Empty);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}

/// <summary>Small seam around Tailscale/Funnel so the shell can swap in a native adapter later.</summary>
public interface ITunnelProvider : IAsyncDisposable
{
    bool IsRunning { get; }
    Task<string?> StartAsync(TunnelLaunchOptions options);
    Task RefreshStatusAsync();
    Task StopAsync();
    Task InstallAsync();
}

public sealed class TunnelServiceAdapter : ITunnelProvider
{
    private readonly TunnelService _service;

    public TunnelServiceAdapter(TunnelService service)
    {
        _service = service;
    }

    public bool IsRunning => _service.IsRunning;

    public Task<string?> StartAsync(TunnelLaunchOptions options) => _service.StartAsync(options);

    public Task RefreshStatusAsync() => _service.RefreshStatusAsync();

    public Task StopAsync() => _service.StopAsync();

    public Task InstallAsync() => _service.InstallTailscaleAsync();

    public ValueTask DisposeAsync()
    {
        _service.Dispose();
        return ValueTask.CompletedTask;
    }
}

public interface IPlatformCapabilities
{
    bool SupportsDesktopCapture { get; }
    bool SupportsDesktopAutomation { get; }
    bool SupportsEmbeddedChat { get; }
}

public sealed class DefaultPlatformCapabilities : IPlatformCapabilities
{
    public bool SupportsDesktopCapture => OperatingSystem.IsWindows();
    public bool SupportsDesktopAutomation => OperatingSystem.IsWindows();
    public bool SupportsEmbeddedChat => OperatingSystem.IsWindows();
}

public interface IApprovalService
{
    Task<bool> ApproveAsync(ToolRequest request, string summary, CancellationToken cancellationToken = default);
}

public sealed class DelegateApprovalService : IApprovalService
{
    private readonly Func<ToolRequest, string, CancellationToken, Task<bool>> _handler;

    public DelegateApprovalService(Func<ToolRequest, string, CancellationToken, Task<bool>> handler)
    {
        _handler = handler;
    }

    public Task<bool> ApproveAsync(ToolRequest request, string summary, CancellationToken cancellationToken = default)
    {
        return _handler(request, summary, cancellationToken);
    }
}

public sealed class NeverApproveService : IApprovalService
{
    public Task<bool> ApproveAsync(ToolRequest request, string summary, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}

public sealed record CompanionStatus(string Area, string State, string Message);
