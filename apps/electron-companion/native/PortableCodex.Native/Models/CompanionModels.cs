using System.Globalization;

namespace PortableCodex.Native.Models;

public sealed class CompanionSettings
{
    public string TunnelMode { get; set; } = "tailscale_funnel";

    public string NamedTunnelName { get; set; } = string.Empty;

    public string NamedTunnelHostname { get; set; } = string.Empty;

    public string RelayUrl { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public string DeviceToken { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string GptApiToken { get; set; } = string.Empty;

    public string IntegrationMode { get; set; } = "legacy_action";

    public List<string> TrustedWorkspaces { get; set; } = [];

    public string CurrentWorkspace { get; set; } = string.Empty;

    public bool ImportCodexCliWorkspaces { get; set; }

    public List<string> SkillRoots { get; set; } = [];

    public bool ImportCodexCliSkills { get; set; }

    public bool RequireApprovalForWrites { get; set; } = true;

    public bool MultithreadedFileSearches { get; set; }

    public bool IsDarkMode { get; set; } = true;
}

public sealed class PersistedState
{
    public CompanionSettings Settings { get; set; } = new();

    public List<ToolLogEntry> Logs { get; set; } = [];
}

public sealed class ToolLogEntry
{
    public string RequestId { get; set; } = string.Empty;

    public string Tool { get; set; } = string.Empty;

    public string ToolDisplay => FormatLabel(Tool);

    public string CreatedAt { get; set; } = string.Empty;

    public string CreatedAtDisplay => FormatTimestamp(CreatedAt);

    public string? CompletedAt { get; set; }

    public string CompletedAtDisplay => FormatTimestamp(CompletedAt);

    public string? WorkspaceRoot { get; set; }

    public List<string> AffectedPaths { get; set; } = [];

    public string Summary { get; set; } = string.Empty;

    public string Status { get; set; } = "pending";

    public string StatusDisplay => FormatLabel(Status);

    public string Approval { get; set; } = "not_required";

    public string? Detail { get; set; }

    private static string FormatTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var timestamp)
            ? timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : value;
    }

    private static string FormatLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.Replace('_', ' '));
    }
}

public sealed class SkillListEntry
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Activation => string.IsNullOrWhiteSpace(Name) ? string.Empty : $"/{Name}";
}

public sealed class ToolProgress
{
    public string RequestId { get; set; } = string.Empty;

    public string Tool { get; set; } = string.Empty;

    public int Processed { get; set; }

    public int Total { get; set; }

    public double Percent => Total <= 0 ? 0 : Math.Clamp(Processed * 100.0 / Total, 0, 100);

    public string Message { get; set; } = string.Empty;
}

public sealed class RelayConnectionStatus
{
    public string State { get; set; } = "disconnected";

    public string Message { get; set; } = "Not connected";
}

public sealed class LocalRelayStatus
{
    public string State { get; set; } = "stopped";

    public string Message { get; set; } = "Local relay is stopped";
}

public sealed class CredentialSet
{
    public string DeviceId { get; set; } = string.Empty;

    public string DeviceToken { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string GptApiToken { get; set; } = string.Empty;
}

public sealed class TunnelStatus
{
    public string State { get; set; } = "stopped";

    public string Message { get; set; } = string.Empty;
}

public sealed class TunnelLaunchOptions
{
    public string Mode { get; set; } = "tailscale_funnel";

    public int LocalPort { get; set; } = 8787;

    public string NamedTunnelName { get; set; } = string.Empty;

    public string NamedTunnelHostname { get; set; } = string.Empty;
}
