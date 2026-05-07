using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PortableCodex.Native.Models;

namespace PortableCodex.Native.Services;

public sealed partial class CredentialService
{
    public CredentialSet GenerateCredentialSet(string? deviceName = null)
    {
        var normalizedName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName.Trim();
        var slug = SlugifyDeviceName(normalizedName);
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "portable-codex";
        }

        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return new CredentialSet
        {
            DeviceId = $"pc-{slug}-{suffix}",
            DeviceToken = CreateToken("pct_"),
            DeviceName = normalizedName,
            GptApiToken = CreateToken("pc_"),
        };
    }

    public CompanionSettings CreateSuggestedSettings(CompanionSettings? overrides = null)
    {
        var generated = GenerateCredentialSet();
        var settings = new CompanionSettings
        {
            TunnelMode = "tailscale_funnel",
            NamedTunnelName = string.Empty,
            NamedTunnelHostname = string.Empty,
            RelayUrl = string.Empty,
            DeviceId = generated.DeviceId,
            DeviceToken = generated.DeviceToken,
            DeviceName = generated.DeviceName,
            GptApiToken = generated.GptApiToken,
            TrustedWorkspaces = [],
            CurrentWorkspace = string.Empty,
            ImportCodexCliWorkspaces = false,
            RequireApprovalForWrites = false,
            MultithreadedFileSearches = false,
            IsDarkMode = true,
        };

        if (overrides is null)
        {
            return settings;
        }

        settings.TunnelMode = string.IsNullOrWhiteSpace(overrides.TunnelMode) ? settings.TunnelMode : overrides.TunnelMode;
        settings.NamedTunnelName = overrides.NamedTunnelName ?? settings.NamedTunnelName;
        settings.NamedTunnelHostname = overrides.NamedTunnelHostname ?? settings.NamedTunnelHostname;
        settings.RelayUrl = overrides.RelayUrl ?? settings.RelayUrl;
        settings.DeviceId = string.IsNullOrWhiteSpace(overrides.DeviceId) ? settings.DeviceId : overrides.DeviceId;
        settings.DeviceToken = string.IsNullOrWhiteSpace(overrides.DeviceToken) ? settings.DeviceToken : overrides.DeviceToken;
        settings.DeviceName = string.IsNullOrWhiteSpace(overrides.DeviceName) ? settings.DeviceName : overrides.DeviceName;
        settings.GptApiToken = string.IsNullOrWhiteSpace(overrides.GptApiToken) ? settings.GptApiToken : overrides.GptApiToken;
        settings.TrustedWorkspaces = overrides.TrustedWorkspaces ?? [];
        settings.CurrentWorkspace = overrides.CurrentWorkspace ?? string.Empty;
        settings.ImportCodexCliWorkspaces = overrides.ImportCodexCliWorkspaces;
        settings.RequireApprovalForWrites = overrides.RequireApprovalForWrites;
        settings.MultithreadedFileSearches = overrides.MultithreadedFileSearches;
        settings.IsDarkMode = overrides.IsDarkMode;
        return settings;
    }

    public PersistedState CreateDefaultState()
    {
        return new PersistedState
        {
            Settings = CreateSuggestedSettings(),
            Logs = [],
        };
    }

    private static string SlugifyDeviceName(string value)
    {
        var lowered = value.ToLowerInvariant();
        var normalized = NonSlugCharsRegex().Replace(lowered, "-");
        normalized = normalized.Trim('-');
        return normalized.Length <= 24 ? normalized : normalized[..24];
    }

    private static string CreateToken(string prefix, int bytes = 18)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
        return $"{prefix}{token}";
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex NonSlugCharsRegex();
}
