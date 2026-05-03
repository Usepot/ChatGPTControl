using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;

namespace PortableCodex.Native.Services;

public sealed class SettingsStore
{
    private readonly string _filePath;
    private readonly CredentialService _credentialService;

    public SettingsStore(CredentialService credentialService)
    {
        _credentialService = credentialService;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PortableCodex");
        _filePath = Path.Combine(root, "portable-codex-state.json");
    }

    public PersistedState Load()
    {
        var fallback = _credentialService.CreateDefaultState();
        if (!File.Exists(_filePath))
        {
            return fallback;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var parsed = JsonSerializer.Deserialize<PersistedState>(json, JsonDefaults.Transport);
            var parsedNode = JsonNode.Parse(json);
            var hasApprovalSetting = parsedNode?["settings"]?["requireApprovalForWrites"] is not null;
            var hasCodexImportSetting = parsedNode?["settings"]?["importCodexCliWorkspaces"] is not null;
            var hasMultithreadedSearchSetting = parsedNode?["settings"]?["multithreadedFileSearches"] is not null;
            var hasDarkModeSetting = parsedNode?["settings"]?["isDarkMode"] is not null;
            if (parsed is null)
            {
                return fallback;
            }

            var loadedSettings = parsed.Settings ?? new CompanionSettings();
            return new PersistedState
            {
                Settings = _credentialService.CreateSuggestedSettings(new CompanionSettings
                {
                    TunnelMode = loadedSettings.TunnelMode ?? "tailscale_funnel",
                    NamedTunnelName = loadedSettings.NamedTunnelName ?? string.Empty,
                    NamedTunnelHostname = loadedSettings.NamedTunnelHostname ?? string.Empty,
                    RelayUrl = loadedSettings.RelayUrl ?? string.Empty,
                    DeviceId = loadedSettings.DeviceId ?? string.Empty,
                    DeviceToken = loadedSettings.DeviceToken ?? string.Empty,
                    DeviceName = loadedSettings.DeviceName ?? string.Empty,
                    GptApiToken = loadedSettings.GptApiToken ?? string.Empty,
                    TrustedWorkspaces = loadedSettings.TrustedWorkspaces ?? [],
                    CurrentWorkspace = loadedSettings.CurrentWorkspace ?? string.Empty,
                    ImportCodexCliWorkspaces = hasCodexImportSetting && loadedSettings.ImportCodexCliWorkspaces,
                    RequireApprovalForWrites = hasApprovalSetting
                        ? loadedSettings.RequireApprovalForWrites
                        : true,
                    MultithreadedFileSearches = hasMultithreadedSearchSetting && loadedSettings.MultithreadedFileSearches,
                    IsDarkMode = hasDarkModeSetting
                        ? loadedSettings.IsDarkMode
                        : true,
                }),
                Logs = parsed.Logs ?? [],
            };
        }
        catch
        {
            return fallback;
        }
    }

    public void Save(PersistedState state)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(state, JsonDefaults.Storage);
        File.WriteAllText(_filePath, json);
    }
}
