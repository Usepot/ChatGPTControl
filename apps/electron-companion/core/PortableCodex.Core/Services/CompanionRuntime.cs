using System.Text.Json;
using System.Text.Json.Nodes;
using PortableCodex.Native.Models;
using PortableCodex.Native.Services;
using PortableCodex.Native.Utils;

namespace PortableCodex.Core.Services;

/// <summary>
/// UI-independent orchestration for the always-on local companion. The legacy WPF app can continue
/// to construct the original services while the Avalonia shell consumes this runtime.
/// </summary>
public sealed class CompanionRuntime : IAsyncDisposable
{
    private const int MaxLogEntries = 250;
    private const int LocalRelayPort = 8787;
    private readonly object _sync = new();
    private readonly CredentialService _credentialService = new();
    private readonly PathPolicyService _pathPolicy = new();
    private readonly RelayContentService _relayContentService = new();
    private readonly CodexCliWorkspaceService _codexCliWorkspaceService = new();
    private readonly SkillService _skillService = new();
    private readonly SettingsStore _settingsStore;
    private readonly FileToolService _fileToolService;
    private readonly LocalRelayServerService _localRelayService;
    private readonly RelayClientService _relayClientService;
    private readonly ITunnelProvider _tunnelProvider;
    private readonly IApprovalService _approvalService;
    private PersistedState _state;
    private bool _initialized;

    public CompanionRuntime(
        IAppPaths? paths = null,
        IApprovalService? approvalService = null,
        ITunnelProvider? tunnelProvider = null,
        IPlatformCapabilities? platformCapabilities = null)
    {
        Paths = paths ?? new DefaultAppPaths();
        Capabilities = platformCapabilities ?? new DefaultPlatformCapabilities();
        _approvalService = approvalService ?? new NeverApproveService();
        _settingsStore = new SettingsStore(_credentialService, Paths.StateFilePath);
        _state = _settingsStore.Load();
        _fileToolService = new FileToolService(_pathPolicy, progress =>
        {
            StatusChanged?.Invoke(new CompanionStatus(
                "search",
                "running",
                progress.Message));
        });
        _localRelayService = new LocalRelayServerService(
            status => StatusChanged?.Invoke(new CompanionStatus("local-relay", status.State, status.Message)),
            OnRelayTerminalDispatch);
        _relayClientService = new RelayClientService(
            GetRelayConnectionSettings,
            HandleToolRequestAsync,
            status => StatusChanged?.Invoke(new CompanionStatus("relay", status.State, status.Message)));
        _tunnelProvider = tunnelProvider ?? new TunnelServiceAdapter(new TunnelService(status =>
            StatusChanged?.Invoke(new CompanionStatus("tunnel", status.State, status.Message))));
    }

    public IAppPaths Paths { get; }
    public IPlatformCapabilities Capabilities { get; }
    public CompanionSettings Settings => _state.Settings;
    public IReadOnlyList<ToolLogEntry> Logs => _state.Logs;
    public bool IsInitialized => _initialized;
    public bool IsLocalRelayRunning => _localRelayService.IsRunning;
    public bool IsTunnelRunning => _tunnelProvider.IsRunning;

    public event Action<CompanionStatus>? StatusChanged;
    public event Action<ToolLogEntry>? LogChanged;
    public event Func<ToolRequest, string, CancellationToken, Task<bool>>? ApprovalRequested;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(Paths.DataDirectory);
        ImportConfiguredRoots();
        await _localRelayService.StartAsync(Settings);
        cancellationToken.ThrowIfCancellationRequested();
        await _tunnelProvider.RefreshStatusAsync();
        cancellationToken.ThrowIfCancellationRequested();
        _relayClientService.Connect();
        _initialized = true;
        Save();
    }

    public async Task ShutdownAsync()
    {
        await _relayClientService.DisconnectAsync();
        await _localRelayService.StopAsync();
        await _tunnelProvider.StopAsync();
        _initialized = false;
        Save();
    }

    public async Task ReconnectAsync()
    {
        await _localRelayService.RestartAsync(Settings);
        await _relayClientService.ReconnectAsync();
    }

    public async Task<string?> StartTunnelAsync()
    {
        return await _tunnelProvider.StartAsync(new TunnelLaunchOptions
        {
            Mode = Settings.TunnelMode,
            LocalPort = LocalRelayPort,
            NamedTunnelName = Settings.NamedTunnelName,
            NamedTunnelHostname = Settings.NamedTunnelHostname,
        });
    }

    public Task StopTunnelAsync() => _tunnelProvider.StopAsync();

    public Task RefreshTunnelStatusAsync() => _tunnelProvider.RefreshStatusAsync();

    public Task InstallTailscaleAsync() => _tunnelProvider.InstallAsync();

    public IReadOnlyList<SkillListEntry> GetSkills() => _skillService.GetSkillList(Settings);

    public string GetGptInstructions() => RelayContentService.GptInstructions;

    public string GetOpenApiJson(string? relayUrl) => _relayContentService.GetOpenApiJson(relayUrl);

    public void AddWorkspace(string workspaceRoot)
    {
        var normalized = _pathPolicy.NormalizeWorkspaceRoot(workspaceRoot);
        if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"Workspace does not exist: {workspaceRoot}");
        }

        lock (_sync)
        {
            if (!Settings.TrustedWorkspaces.Any(root =>
                    string.Equals(
                        _pathPolicy.NormalizeWorkspaceRoot(root),
                        normalized,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            {
                Settings.TrustedWorkspaces.Add(normalized);
            }

            if (string.IsNullOrWhiteSpace(Settings.CurrentWorkspace))
            {
                Settings.CurrentWorkspace = normalized;
            }
        }

        Save();
    }

    public void RemoveWorkspace(string workspaceRoot)
    {
        var normalized = _pathPolicy.NormalizeWorkspaceRoot(workspaceRoot);
        lock (_sync)
        {
            Settings.TrustedWorkspaces.RemoveAll(root => string.Equals(
                _pathPolicy.NormalizeWorkspaceRoot(root),
                normalized,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

            if (string.Equals(Settings.CurrentWorkspace, normalized, StringComparison.OrdinalIgnoreCase))
            {
                Settings.CurrentWorkspace = Settings.TrustedWorkspaces.FirstOrDefault() ?? string.Empty;
            }
        }

        Save();
    }

    public void SetCurrentWorkspace(string? workspaceRoot)
    {
        var normalized = string.IsNullOrWhiteSpace(workspaceRoot)
            ? string.Empty
            : _pathPolicy.NormalizeWorkspaceRoot(workspaceRoot);
        if (!string.IsNullOrWhiteSpace(normalized) && !Settings.TrustedWorkspaces.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected workspace is not trusted");
        }

        Settings.CurrentWorkspace = normalized;
        Save();
    }

    public void SetRelayUrl(string? relayUrl)
    {
        Settings.RelayUrl = (relayUrl ?? string.Empty).Trim().TrimEnd('/');
        Save();
    }

    public void SetRequireApproval(bool requireApproval)
    {
        Settings.RequireApprovalForWrites = requireApproval;
        Save();
    }

    public IReadOnlyList<string> GrantFullAccess()
    {
        var roots = GetSystemRoots()
            .Where(Directory.Exists)
            .Select(_pathPolicy.NormalizeWorkspaceRoot)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();

        lock (_sync)
        {
            foreach (var root in roots)
            {
                if (!Settings.TrustedWorkspaces.Contains(root, OperatingSystem.IsWindows()
                        ? StringComparer.OrdinalIgnoreCase
                        : StringComparer.Ordinal))
                {
                    Settings.TrustedWorkspaces.Add(root);
                }
            }

            if (string.IsNullOrWhiteSpace(Settings.CurrentWorkspace))
            {
                Settings.CurrentWorkspace = roots.FirstOrDefault() ?? string.Empty;
            }
        }

        Save();
        return Settings.TrustedWorkspaces.ToArray();
    }

    public void SetSetupCompleted(bool completed)
    {
        Settings.SetupCompleted = completed;
        Save();
    }

    public void Save()
    {
        lock (_sync)
        {
            _settingsStore.Save(_state);
        }
    }

    private CompanionSettings GetRelayConnectionSettings()
    {
        var settings = Settings;
        if (!string.IsNullOrWhiteSpace(settings.RelayUrl))
        {
            return settings;
        }

        return new CompanionSettings
        {
            RelayUrl = $"http://127.0.0.1:{LocalRelayPort}",
            DeviceId = settings.DeviceId,
            DeviceToken = settings.DeviceToken,
            DeviceName = settings.DeviceName,
        };
    }

    private void ImportConfiguredRoots()
    {
        if (Settings.ImportCodexCliWorkspaces)
        {
            foreach (var root in _codexCliWorkspaceService.GetWorkspaceRoots())
            {
                if (!Settings.TrustedWorkspaces.Contains(root, StringComparer.OrdinalIgnoreCase))
                {
                    Settings.TrustedWorkspaces.Add(_pathPolicy.NormalizeWorkspaceRoot(root));
                }
            }
        }

        if (Settings.ImportCodexCliSkills)
        {
            foreach (var root in _codexCliWorkspaceService.GetSkillRoots())
            {
                if (!Settings.SkillRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
                {
                    Settings.SkillRoots.Add(root);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(Settings.CurrentWorkspace))
        {
            Settings.CurrentWorkspace = Settings.TrustedWorkspaces.FirstOrDefault() ?? string.Empty;
        }
    }

    private static IEnumerable<string> GetSystemRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return Path.GetPathRoot(Environment.CurrentDirectory) ?? Path.DirectorySeparatorChar.ToString();
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

    private async Task<ToolResponse> HandleToolRequestAsync(ToolRequest request)
    {
        var settings = Settings;
        ApplyRequestDefaults(request, settings);
        var log = new ToolLogEntry
        {
            RequestId = request.RequestId,
            Tool = request.Tool,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            WorkspaceRoot = request.WorkspaceRoot,
            AffectedPaths = GetAffectedPaths(request),
            Summary = _fileToolService.SummarizeRequestForLog(request),
            Status = "pending",
            Approval = NeedsApproval(settings, request) ? "pending" : "not_required",
        };
        AddOrReplaceLog(log);

        ToolResponse response;
        try
        {
            response = request.Tool switch
            {
                "list_trusted_workspaces" => Ok(request.RequestId, new
                {
                    workspaces = settings.TrustedWorkspaces,
                    currentWorkspace = settings.CurrentWorkspace,
                }),
                "get_gpt_instructions" => Ok(request.RequestId, new { instructions = RelayContentService.GptInstructions }),
                "list_skills" => _skillService.ListSkills(request, settings),
                "get_skill" => await _skillService.GetSkillAsync(request, settings),
                "request_permissions" => RequestPermissionsResponse(request),
                _ => await _fileToolService.ExecuteAsync(
                    request,
                    new ToolExecutionContext
                    {
                        Settings = settings,
                        ApproveWriteAsync = (toolRequest, summary) => ApproveAsync(toolRequest, summary),
                    }),
            };
        }
        catch (Exception exception)
        {
            response = Error(request.RequestId, "COMPANION_RUNTIME_ERROR", exception.Message);
        }

        var updated = new ToolLogEntry
        {
            RequestId = log.RequestId,
            Tool = log.Tool,
            CreatedAt = log.CreatedAt,
            CompletedAt = DateTime.UtcNow.ToString("O"),
            WorkspaceRoot = log.WorkspaceRoot,
            AffectedPaths = log.AffectedPaths,
            Summary = log.Summary,
            Status = response.Status,
            Approval = log.Approval == "pending"
                ? (response.Status == "denied" ? "denied" : "approved")
                : log.Approval,
            Detail = response.Error is null ? response.Result?.ToJsonString(JsonDefaults.Storage) : $"{response.Error.Code}: {response.Error.Message}",
        };
        AddOrReplaceLog(updated);
        return response;
    }

    private Task<bool> ApproveAsync(ToolRequest request, string summary)
    {
        var handlers = ApprovalRequested?.GetInvocationList();
        if (handlers is null || handlers.Length == 0)
        {
            return _approvalService.ApproveAsync(request, summary);
        }

        return ((Func<ToolRequest, string, CancellationToken, Task<bool>>)handlers[0])(request, summary, CancellationToken.None);
    }

    private void OnRelayTerminalDispatch(ToolRequest request, ToolResponse response)
    {
        AddOrReplaceLog(new ToolLogEntry
        {
            RequestId = request.RequestId,
            Tool = request.Tool,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            CompletedAt = DateTime.UtcNow.ToString("O"),
            WorkspaceRoot = request.WorkspaceRoot,
            AffectedPaths = GetAffectedPaths(request),
            Summary = _fileToolService.SummarizeRequestForLog(request),
            Status = response.Status,
            Approval = "not_required",
            Detail = response.Error is null ? response.Result?.ToJsonString(JsonDefaults.Storage) : $"{response.Error.Code}: {response.Error.Message}",
        });
    }

    private void AddOrReplaceLog(ToolLogEntry entry)
    {
        lock (_sync)
        {
            var existingIndex = _state.Logs.FindIndex(item => item.RequestId == entry.RequestId);
            if (existingIndex >= 0)
            {
                _state.Logs[existingIndex] = entry;
            }
            else
            {
                _state.Logs.Insert(0, entry);
            }

            if (_state.Logs.Count > MaxLogEntries)
            {
                _state.Logs.RemoveRange(MaxLogEntries, _state.Logs.Count - MaxLogEntries);
            }
        }

        LogChanged?.Invoke(entry);
        Save();
    }

    private static void ApplyRequestDefaults(ToolRequest request, CompanionSettings settings)
    {
        if (request.Tool == "search_files" && request.Multithreaded is null)
        {
            request.Multithreaded = settings.MultithreadedFileSearches;
        }

        if (ToolUsesWorkspace(request.Tool) && string.IsNullOrWhiteSpace(request.WorkspaceRoot))
        {
            request.WorkspaceRoot = settings.CurrentWorkspace;
        }
    }

    private static bool ToolUsesWorkspace(string tool)
    {
        return tool is not ("list_trusted_workspaces" or "get_gpt_instructions" or "list_skills" or "get_skill" or "write_stdin" or "request_permissions" or "view_desktop" or "screenshot_desktop" or "click_desktop");
    }

    private static bool NeedsApproval(CompanionSettings settings, ToolRequest request)
    {
        return settings.RequireApprovalForWrites && ProtocolConstants.WriteTools.Contains(request.Tool);
    }

    private static List<string> GetAffectedPaths(ToolRequest request)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            paths.Add(request.Path);
        }

        if (request.Operations is { Count: > 0 })
        {
            paths.AddRange(request.Operations.Select(operation => operation.Find));
        }

        return paths.Distinct(StringComparer.Ordinal).ToList();
    }

    private static ToolResponse RequestPermissionsResponse(ToolRequest request)
    {
        return Error(request.RequestId, "REQUEST_PERMISSIONS_UNSUPPORTED", "Portable Codex uses trusted workspaces and companion approvals instead of dynamic sandbox permission escalation.");
    }

    private static ToolResponse Ok(string requestId, object result) => new()
    {
        RequestId = requestId,
        Status = "ok",
        Result = JsonSerializer.SerializeToNode(result, JsonDefaults.Transport),
    };

    private static ToolResponse Error(string requestId, string code, string message) => new()
    {
        RequestId = requestId,
        Status = "error",
        Error = new ToolError { Code = code, Message = message },
    };

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        await _tunnelProvider.DisposeAsync();
    }
}
