using System.Text.Json;
using System.Text.RegularExpressions;

namespace PortableCodex.Native.Services;

/// <summary>
/// Tolerant parsing for the JSON and human-readable shapes emitted by different Tailscale CLI releases.
/// Kept independent from process launching so it can be covered with fixtures on every OS.
/// </summary>
public static partial class TailscaleStatusParser
{
    public static TailscaleStatusSnapshot ParseStatus(string? output)
    {
        var text = output?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TailscaleStatusSnapshot(false, "error", "Tailscale returned no status.");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var backendState = GetString(root, "BackendState", "backendState", "State", "state");
            if (!string.Equals(backendState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                var message = string.Equals(backendState, "NeedsMachineAuth", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(backendState, "NeedsLogin", StringComparison.OrdinalIgnoreCase)
                    ? "Approve this device in your Tailscale admin console, then click Connect to Tailscale."
                    : "Sign in to Tailscale and make sure this device joins your tailnet.";
                return new TailscaleStatusSnapshot(false, "login_required", message);
            }

            var self = GetProperty(root, "Self", "self");
            var dnsName = self is { ValueKind: JsonValueKind.Object }
                ? NormalizeHost(GetString(self.Value, "DNSName", "dnsName", "HostName", "hostname"))
                : string.Empty;
            if (string.IsNullOrWhiteSpace(dnsName))
            {
                return new TailscaleStatusSnapshot(false, "login_required", "Tailscale is running but this device is not fully joined to a tailnet yet.");
            }

            return new TailscaleStatusSnapshot(true, "starting", "Tailscale ready", dnsName);
        }
        catch (JsonException)
        {
            if (ApprovalPendingRegex().IsMatch(text))
            {
                return new TailscaleStatusSnapshot(false, "approval_required", "Approve Funnel in Tailscale, then click Connect to Tailscale again.");
            }

            if (LoginRequiredRegex().IsMatch(text))
            {
                return new TailscaleStatusSnapshot(false, "login_required", "Sign in to Tailscale and make sure this device joins your tailnet.");
            }

            return new TailscaleStatusSnapshot(false, "error", "Tailscale returned an unexpected status format.");
        }
    }

    public static string? FindFunnelUrl(string? output, string? fallbackHost = null)
    {
        var text = output ?? string.Empty;
        var urlMatch = TsNetUrlRegex().Match(text);
        if (urlMatch.Success && Uri.TryCreate(urlMatch.Value.TrimEnd('.', ',', ';'), UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Host}".TrimEnd('/');
        }

        var hostMatch = TsNetHostRegex().Match(text);
        if (hostMatch.Success)
        {
            var host = NormalizeHost(hostMatch.Value);
            if (!string.IsNullOrWhiteSpace(host))
            {
                return $"https://{host}";
            }
        }

        var normalizedFallback = NormalizeHost(fallbackHost);
        return !string.IsNullOrWhiteSpace(normalizedFallback) &&
               text.Contains(normalizedFallback, StringComparison.OrdinalIgnoreCase)
            ? $"https://{normalizedFallback}"
            : null;
    }

    private static JsonElement? GetProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                return value;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        var property = GetProperty(element, names);
        return property is { ValueKind: JsonValueKind.String } ? property.Value.GetString() : null;
    }

    private static string NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().TrimEnd('.');
        if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["https://".Length..];
        }
        else if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["http://".Length..];
        }

        var colon = normalized.IndexOf(':');
        if (colon >= 0)
        {
            normalized = normalized[..colon];
        }

        return normalized.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
            ? normalized.ToLowerInvariant()
            : string.Empty;
    }

    public sealed record TailscaleStatusSnapshot(
        bool Ready,
        string State,
        string Message,
        string DeviceDnsName = "");

    [GeneratedRegex(@"https://[a-z0-9][a-z0-9\-\.]*\.ts\.net(?:[^\s""'<>]*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TsNetUrlRegex();

    [GeneratedRegex(@"[a-z0-9][a-z0-9\-]*(?:\.[a-z0-9\-]+)*\.ts\.net\.?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TsNetHostRegex();

    [GeneratedRegex(@"not logged in|needslogin|needs machine auth|tailscale up|login", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LoginRequiredRegex();

    [GeneratedRegex(@"funnel is not enabled|funnel approval|enable funnel|tailnet admin", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ApprovalPendingRegex();
}
