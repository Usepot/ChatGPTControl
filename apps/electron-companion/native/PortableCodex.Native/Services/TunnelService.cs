using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PortableCodex.Native.Models;

namespace PortableCodex.Native.Services;

public sealed partial class TunnelService : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TunnelReadyTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan TunnelProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TunnelHealthInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FunnelCommandTimeout = TimeSpan.FromSeconds(3);
    private const int MaxConsecutiveHealthFailures = 3;

    private readonly Action<TunnelStatus> _onStatusChanged;
    private readonly object _sync = new();
    private CancellationTokenSource? _approvalMonitorCts;
    private Task? _approvalMonitorTask;
    private CancellationTokenSource? _healthMonitorCts;
    private Task? _healthMonitorTask;
    private string _publicUrl = string.Empty;
    private string _lastApprovalUrlOpened = string.Empty;
    private bool _isRunning;
    private int _lastLocalPort = 8787;

    public TunnelService(Action<TunnelStatus> onStatusChanged)
    {
        _onStatusChanged = onStatusChanged;
    }

    public string PublicUrl => _publicUrl;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _isRunning;
            }
        }
    }

    public async Task<string?> StartAsync(TunnelLaunchOptions options)
    {
        if (IsRunning && !string.IsNullOrWhiteSpace(_publicUrl))
        {
            return _publicUrl;
        }

        StopApprovalMonitor();
        _lastLocalPort = options.LocalPort > 0 ? options.LocalPort : 8787;
        _lastApprovalUrlOpened = string.Empty;
        SetStatus("starting", "Checking Tailscale installation...");

        var exe = FindTailscale();
        if (exe is null)
        {
            SetStatus("not_found", "Tailscale CLI not found. Install Tailscale and sign in, then click Connect to Tailscale.");
            return null;
        }

        var readiness = await EnsureTailscaleReadyAsync(exe);
        if (!readiness.Ready)
        {
            SetStatus(readiness.State, readiness.Message);
            return null;
        }

        var startResult = await StartFunnelAndResolveUrlAsync(exe, _lastLocalPort, readiness.DeviceDnsName);
        if (startResult.ApprovalPending)
        {
            StartApprovalMonitor(exe, _lastLocalPort, readiness.DeviceDnsName);
            return null;
        }

        if (string.IsNullOrWhiteSpace(startResult.PublicUrl))
        {
            return null;
        }

        if (!await ActivateTunnelAsync(startResult.PublicUrl))
        {
            return null;
        }

        return _publicUrl;
    }

    public async Task RefreshStatusAsync()
    {
        if (IsRunning && !string.IsNullOrWhiteSpace(_publicUrl))
        {
            SetStatus("running", _publicUrl);
            return;
        }

        var exe = FindTailscale();
        if (exe is null)
        {
            SetStatus("not_found", "Tailscale CLI not found. Install Tailscale to connect.");
            return;
        }

        var readiness = await EnsureTailscaleReadyAsync(exe);
        if (!readiness.Ready)
        {
            SetStatus(readiness.State, readiness.Message);
            return;
        }

        SetStatus("stopped", "Tailscale is ready. Click Connect to Tailscale.");
    }

    public async Task StopAsync()
    {
        StopApprovalMonitor();
        StopHealthMonitor();

        var shouldDisableFunnel = IsRunning || !string.IsNullOrWhiteSpace(_publicUrl);
        if (!shouldDisableFunnel)
        {
            lock (_sync)
            {
                _isRunning = false;
            }

            _publicUrl = string.Empty;
            SetStatus("stopped", "Tailscale is ready. Click Connect to Tailscale.");
            return;
        }

        SetStatus("starting", "Stopping existing Tailscale Funnel...");

        var exe = FindTailscale();
        if (!string.IsNullOrWhiteSpace(exe))
        {
            await DisableFunnelAsync(exe);
        }

        lock (_sync)
        {
            _isRunning = false;
        }

        _publicUrl = string.Empty;
        SetStatus("stopped", "Funnel stopped");
    }

    public void Dispose()
    {
        StopHealthMonitor();
    }

    private async Task<TailscaleReadiness> EnsureTailscaleReadyAsync(string exe)
    {
        var result = await RunTailscaleCommandAsync(exe, "status --json", createNoWindow: true);
        var output = string.Join(Environment.NewLine, result.Stdout, result.Stderr).Trim();
        if (result.ExitCode != 0)
        {
            if (LooksLikeLoginRequired(output))
            {
                return new TailscaleReadiness(
                    false,
                    "login_required",
                    "Sign in to Tailscale to continue. If prompted, run `tailscale up` in your terminal.");
            }

            return new TailscaleReadiness(
                false,
                "error",
                FirstNonEmptyLine(result.Stderr, result.Stdout) ?? "Unable to query Tailscale status.");
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Stdout);
            var root = doc.RootElement;
            var backendState = root.TryGetProperty("BackendState", out var stateElement)
                ? stateElement.GetString()
                : null;
            if (!string.Equals(backendState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                var message = string.Equals(backendState, "NeedsMachineAuth", StringComparison.OrdinalIgnoreCase)
                    ? "Approve this device in your Tailscale admin console, then click Connect to Tailscale."
                    : "Sign in to Tailscale and make sure this device joins your tailnet.";
                return new TailscaleReadiness(false, "login_required", message);
            }

            var deviceDnsName = string.Empty;
            if (root.TryGetProperty("Self", out var self) &&
                self.ValueKind == JsonValueKind.Object &&
                self.TryGetProperty("DNSName", out var dnsNameElement))
            {
                deviceDnsName = NormalizeTsNetHost(dnsNameElement.GetString());
            }

            if (string.IsNullOrWhiteSpace(deviceDnsName))
            {
                return new TailscaleReadiness(
                    false,
                    "login_required",
                    "Tailscale is running but this device is not fully joined to a tailnet yet.");
            }

            return new TailscaleReadiness(true, "starting", "Tailscale ready", deviceDnsName);
        }
        catch (JsonException)
        {
            if (LooksLikeLoginRequired(output))
            {
                return new TailscaleReadiness(
                    false,
                    "login_required",
                    "Sign in to Tailscale and make sure this device joins your tailnet.");
            }

            return new TailscaleReadiness(
                false,
                "error",
                "Tailscale returned an unexpected status format.");
        }
    }

    private async Task<FunnelStartResult> StartFunnelAndResolveUrlAsync(string exe, int localPort, string deviceDnsName)
    {
        var deadline = DateTime.UtcNow + StartupTimeout;
        var command = $"funnel --bg localhost:{localPort}";
        var waitingMessage = "Waiting for Tailscale Funnel approval...";

        while (DateTime.UtcNow < deadline)
        {
            var discoveredUrl = await TryGetFunnelUrlFromStatusAsync(exe, deviceDnsName);
            if (!string.IsNullOrWhiteSpace(discoveredUrl))
            {
                return new FunnelStartResult(discoveredUrl, ApprovalPending: false);
            }

            var result = await RunTailscaleCommandAsync(
                exe,
                command,
                createNoWindow: true,
                timeout: FunnelCommandTimeout,
                stopEarlyWhen: (stdout, stderr) =>
                {
                    var output = string.Join(Environment.NewLine, stdout, stderr);
                    return !string.IsNullOrWhiteSpace(ExtractTsNetUrl(stdout, stderr)) ||
                           LooksLikeApprovalPending(output) ||
                           LooksLikeLoginRequired(output);
                });
            var output = string.Join(Environment.NewLine, result.Stdout, result.Stderr);
            var directUrl = ExtractTsNetUrl(result.Stdout, result.Stderr);
            if (!string.IsNullOrWhiteSpace(directUrl))
            {
                return new FunnelStartResult(directUrl, ApprovalPending: false);
            }

            if (LooksLikeApprovalPending(output))
            {
                var approvalUrl = ExtractApprovalUrl(output);
                if (!string.IsNullOrWhiteSpace(approvalUrl) &&
                    !string.Equals(_lastApprovalUrlOpened, approvalUrl, StringComparison.OrdinalIgnoreCase))
                {
                    OpenUrl(approvalUrl);
                    _lastApprovalUrlOpened = approvalUrl;
                }

                waitingMessage = string.IsNullOrWhiteSpace(approvalUrl)
                    ? "Waiting for Funnel approval in your browser..."
                    : $"Approve Funnel in browser: {approvalUrl}";
                SetStatus("approval_required", waitingMessage);
                return new FunnelStartResult(null, ApprovalPending: true);
            }

            discoveredUrl = await TryGetFunnelUrlFromStatusAsync(exe, deviceDnsName);
            if (!string.IsNullOrWhiteSpace(discoveredUrl))
            {
                return new FunnelStartResult(discoveredUrl, ApprovalPending: false);
            }

            if (LooksLikeLoginRequired(output))
            {
                SetStatus("login_required", "Sign in to Tailscale to continue, then click Connect to Tailscale again.");
                return new FunnelStartResult(null, ApprovalPending: false);
            }

            if (result.ExitCode != 0)
            {
                var detail = FirstNonEmptyLine(result.Stderr, result.Stdout);
                waitingMessage = string.IsNullOrWhiteSpace(detail)
                    ? waitingMessage
                    : detail;
                SetStatus("starting", $"Waiting for Funnel URL... {Shorten(waitingMessage)}");
            }
            else
            {
                SetStatus("starting", "Configuring Tailscale Funnel...");
            }

            await Task.Delay(StartupPollInterval);
        }

        SetStatus("error", "Timed out waiting for Tailscale Funnel URL. Complete approval and click Connect to Tailscale again.");
        return new FunnelStartResult(null, ApprovalPending: false);
    }

    private static async Task<string?> TryGetFunnelUrlFromStatusAsync(string exe, string fallbackHost)
    {
        var statusResult = await RunTailscaleCommandAsync(exe, "funnel status --json", createNoWindow: true);
        var statusOutput = string.Join(Environment.NewLine, statusResult.Stdout, statusResult.Stderr);
        var url = ExtractTsNetUrl(statusOutput);
        if (!string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (statusResult.ExitCode == 0 &&
            !string.IsNullOrWhiteSpace(fallbackHost) &&
            statusOutput.Contains(fallbackHost, StringComparison.OrdinalIgnoreCase))
        {
            return $"https://{fallbackHost}";
        }

        var textResult = await RunTailscaleCommandAsync(exe, "funnel status", createNoWindow: true);
        return ExtractTsNetUrl(textResult.Stdout, textResult.Stderr);
    }

    private async Task DisableFunnelAsync(string exe)
    {
        var candidates = new[]
        {
            "funnel --https=443 off",
            "funnel --bg off",
            "funnel reset",
            "serve reset",
        };

        foreach (var command in candidates)
        {
            try
            {
                var result = await RunTailscaleCommandAsync(exe, command, createNoWindow: true);
                if (result.ExitCode == 0)
                {
                    return;
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private async Task<bool> WaitForTunnelReadyAsync(string publicUrl)
    {
        var deadline = DateTime.UtcNow + TunnelReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await ProbeTunnelAsync(publicUrl))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return false;
    }

    private async Task MonitorTunnelHealthAsync(string publicUrl, CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TunnelHealthInterval, cancellationToken);
                if (await ProbeTunnelAsync(publicUrl, cancellationToken))
                {
                    consecutiveFailures = 0;
                    continue;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Treat the same as a failed probe below.
            }

            consecutiveFailures++;
            if (consecutiveFailures >= MaxConsecutiveHealthFailures)
            {
                HandleTunnelStopped("Funnel is no longer reachable");
                return;
            }
        }
    }

    private static async Task<bool> ProbeTunnelAsync(string publicUrl, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient
        {
            Timeout = TunnelProbeTimeout,
        };

        try
        {
            using var response = await client.GetAsync(
                new Uri(new Uri(publicUrl.TrimEnd('/') + "/"), "health"),
                cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void HandleTunnelStopped(string message)
    {
        StopHealthMonitor();
        lock (_sync)
        {
            _isRunning = false;
        }

        _publicUrl = string.Empty;
        SetStatus("stopped", message);
    }

    private void StartHealthMonitor(string publicUrl)
    {
        StopHealthMonitor();

        var cts = new CancellationTokenSource();
        _healthMonitorCts = cts;
        _healthMonitorTask = Task.Run(() => MonitorTunnelHealthAsync(publicUrl, cts.Token));
    }

    private void StopHealthMonitor()
    {
        var cts = _healthMonitorCts;
        _healthMonitorCts = null;
        _healthMonitorTask = null;

        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    public async Task InstallTailscaleAsync()
    {
        SetStatus("starting", "Downloading Tailscale installer...");
        
        try
        {
            var installerPath = Path.Combine(Path.GetTempPath(), "tailscale-setup.exe");
            
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                var response = await client.GetAsync(
                    "https://pkgs.tailscale.com/stable/tailscale-setup-latest.exe",
                    HttpCompletionOption.ResponseContentRead);
                
                if (!response.IsSuccessStatusCode)
                {
                    SetStatus("error", "Failed to download Tailscale installer. Visit https://tailscale.com/download to install manually.");
                    return;
                }

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await contentStream.CopyToAsync(fileStream);
                }
            }

            SetStatus("starting", "Running Tailscale installer...");
            
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true,
                    CreateNoWindow = false,
                },
            };

            process.Start();
            await process.WaitForExitAsync();

            await Task.Delay(2000);
            
            var exe = FindTailscale();
            if (exe is not null)
            {
                SetStatus("login_required", "Tailscale installed! Sign in to Tailscale and click Connect to Tailscale.");
            }
            else
            {
                SetStatus("error", "Tailscale installer completed but CLI not found. Restart the app and try again.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("error", $"Installation failed: {ex.Message}. Visit https://tailscale.com/download to install manually.");
        }
        finally
        {
            try
            {
                var installerPath = Path.Combine(Path.GetTempPath(), "tailscale-setup.exe");
                if (File.Exists(installerPath))
                {
                    File.Delete(installerPath);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static string? FindTailscale()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tailscale.exe"),
        };

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(dir, "tailscale.exe"));
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Tailscale",
            "tailscale.exe"));

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Tailscale IPN",
            "tailscale.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string NormalizeTsNetHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["https://".Length..];
        }
        else if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["http://".Length..];
        }

        normalized = normalized.Trim().Trim('/').TrimEnd('.');
        var colon = normalized.IndexOf(':');
        if (colon > -1)
        {
            normalized = normalized[..colon];
        }

        return normalized.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
            ? normalized.ToLowerInvariant()
            : string.Empty;
    }

    private static string? ExtractTsNetUrl(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var urlMatch = TsNetUrlRegex().Match(value);
            if (urlMatch.Success)
            {
                var match = urlMatch.Value.Trim().TrimEnd('.', ',', ';');
                if (Uri.TryCreate(match, UriKind.Absolute, out var uri))
                {
                    return $"{uri.Scheme}://{uri.Host}".TrimEnd('/');
                }
            }

            var hostMatch = TsNetHostRegex().Match(value);
            if (hostMatch.Success)
            {
                var host = NormalizeTsNetHost(hostMatch.Value);
                if (!string.IsNullOrWhiteSpace(host))
                {
                    return $"https://{host}";
                }
            }
        }

        return null;
    }

    private static bool LooksLikeLoginRequired(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return LoginRequiredRegex().IsMatch(value);
    }

    private static bool LooksLikeApprovalPending(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return ApprovalPendingRegex().IsMatch(value);
    }

    private static string? ExtractApprovalUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = ApprovalUrlRegex().Match(value);
        return match.Success ? match.Value.TrimEnd('.', ',', ';') : null;
    }

    private static string Shorten(string? text, int max = 80)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)] + "…";
    }

    private static async Task<CommandResult> RunTailscaleCommandAsync(
        string exe,
        string arguments,
        bool createNoWindow,
        TimeSpan? timeout = null,
        Func<string, string, bool>? stopEarlyWhen = null)
    {
        var effectiveTimeout = timeout ?? CommandTimeout;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = createNoWindow,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputMatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new object();

        void HandleLine(StringBuilder buffer, string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (sync)
            {
                buffer.AppendLine(line);
                if (stopEarlyWhen?.Invoke(stdout.ToString(), stderr.ToString()) == true)
                {
                    outputMatched.TrySetResult();
                }
            }
        }

        process.OutputDataReceived += (_, args) => HandleLine(stdout, args.Data);
        process.ErrorDataReceived += (_, args) => HandleLine(stderr, args.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var exitTask = process.WaitForExitAsync();
        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);

        try
        {
            var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
            var completedTask = stopEarlyWhen is null
                ? await Task.WhenAny(exitTask, timeoutTask)
                : await Task.WhenAny(exitTask, timeoutTask, outputMatched.Task);

            if (completedTask == outputMatched.Task)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
            else if (completedTask == timeoutTask)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best effort cleanup.
                }

                return new CommandResult
                {
                    ExitCode = -1,
                    Stdout = stdout.ToString(),
                    Stderr = string.IsNullOrWhiteSpace(stderr.ToString())
                        ? $"Timed out after {effectiveTimeout.TotalSeconds:0} seconds running: tailscale {arguments}"
                        : stderr.ToString(),
                };
            }

            await exitTask;
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort cleanup.
            }

            return new CommandResult
            {
                ExitCode = -1,
                Stdout = stdout.ToString(),
                Stderr = string.IsNullOrWhiteSpace(stderr.ToString())
                    ? $"Timed out after {effectiveTimeout.TotalSeconds:0} seconds running: tailscale {arguments}"
                    : stderr.ToString(),
            };
        }

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            Stdout = stdout.ToString(),
            Stderr = stderr.ToString(),
        };
    }

    private static string? FirstNonEmptyLine(params string[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var line = value
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return null;
    }

    private void SetStatus(string state, string message)
    {
        _onStatusChanged(new TunnelStatus { State = state, Message = message });
    }

    private async Task<bool> ActivateTunnelAsync(string publicUrl)
    {
        SetStatus("starting", "Validating Tailscale Funnel...");
        if (!await WaitForTunnelReadyAsync(publicUrl))
        {
            await StopAsync();
            SetStatus(
                "approval_required",
                "Funnel URL detected but not reachable yet. Approve Funnel in Tailscale and wait a moment.");
            return false;
        }

        lock (_sync)
        {
            _isRunning = true;
        }

        _publicUrl = publicUrl;
        StartHealthMonitor(publicUrl);
        SetStatus("running", _publicUrl);
        return true;
    }

    private void StartApprovalMonitor(string exe, int localPort, string deviceDnsName)
    {
        StopApprovalMonitor();
        var cts = new CancellationTokenSource();
        _approvalMonitorCts = cts;
        _approvalMonitorTask = Task.Run(() => MonitorApprovalAsync(exe, localPort, deviceDnsName, cts.Token));
    }

    private void StopApprovalMonitor()
    {
        var cts = _approvalMonitorCts;
        _approvalMonitorCts = null;
        _approvalMonitorTask = null;

        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private async Task MonitorApprovalAsync(string exe, int localPort, string deviceDnsName, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + StartupTimeout;
        var command = $"funnel --bg localhost:{localPort}";

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var discoveredUrl = await TryGetFunnelUrlFromStatusAsync(exe, deviceDnsName);
            if (!string.IsNullOrWhiteSpace(discoveredUrl))
            {
                if (await ActivateTunnelAsync(discoveredUrl))
                {
                    return;
                }
            }

            var result = await RunTailscaleCommandAsync(
                exe,
                command,
                createNoWindow: true,
                timeout: FunnelCommandTimeout,
                stopEarlyWhen: (stdout, stderr) =>
                {
                    var output = string.Join(Environment.NewLine, stdout, stderr);
                    return !string.IsNullOrWhiteSpace(ExtractTsNetUrl(stdout, stderr)) ||
                           LooksLikeApprovalPending(output) ||
                           LooksLikeLoginRequired(output);
                });
            var output = string.Join(Environment.NewLine, result.Stdout, result.Stderr);
            var directUrl = ExtractTsNetUrl(result.Stdout, result.Stderr);
            if (!string.IsNullOrWhiteSpace(directUrl))
            {
                if (await ActivateTunnelAsync(directUrl))
                {
                    return;
                }
            }

            if (LooksLikeLoginRequired(output))
            {
                SetStatus("login_required", "Sign in to Tailscale to continue, then click Connect to Tailscale again.");
                return;
            }

            if (LooksLikeApprovalPending(output))
            {
                var approvalUrl = ExtractApprovalUrl(output);
                if (!string.IsNullOrWhiteSpace(approvalUrl) &&
                    !string.Equals(_lastApprovalUrlOpened, approvalUrl, StringComparison.OrdinalIgnoreCase))
                {
                    OpenUrl(approvalUrl);
                    _lastApprovalUrlOpened = approvalUrl;
                }

                var waitingMessage = string.IsNullOrWhiteSpace(approvalUrl)
                    ? "Waiting for Funnel approval in your browser..."
                    : $"Approve Funnel in browser: {approvalUrl}";
                SetStatus("approval_required", waitingMessage);
            }

            try
            {
                await Task.Delay(StartupPollInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            SetStatus("error", "Timed out waiting for Tailscale Funnel approval. Click Connect to Tailscale to try again.");
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best effort.
        }
    }

    private sealed class CommandResult
    {
        public int ExitCode { get; init; }

        public string Stdout { get; init; } = string.Empty;

        public string Stderr { get; init; } = string.Empty;
    }

    private sealed record TailscaleReadiness(bool Ready, string State, string Message, string DeviceDnsName = "");
    private sealed record FunnelStartResult(string? PublicUrl, bool ApprovalPending);

    [GeneratedRegex(@"https://[a-z0-9][a-z0-9\-\.]*\.ts\.net(?:[^\s""'<>]*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TsNetUrlRegex();

    [GeneratedRegex(@"[a-z0-9][a-z0-9\-]*(?:\.[a-z0-9\-]+)*\.ts\.net\.?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TsNetHostRegex();

    [GeneratedRegex(@"https://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ApprovalUrlRegex();

    [GeneratedRegex(@"approve|approval|enable funnel|tailnet admin|funnel is not enabled on your tailnet|login\.tailscale\.com/f/funnel", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ApprovalPendingRegex();

    [GeneratedRegex(@"not logged in|needslogin|needs machine auth|tailscale up|login", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LoginRequiredRegex();
}
