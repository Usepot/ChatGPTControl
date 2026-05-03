using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;

namespace PortableCodex.Native.Services;

/// <summary>
/// Bundled GPT setup content: OpenAPI comes from <c>docs/openapi.actions.min.json</c> (kept in sync via
/// <c>npm run docs:openapi</c>); instructions from <c>docs/custom-gpt-instructions.md</c>. Both are embedded at build time.
/// </summary>
public sealed class RelayContentService
{
    private const string OpenApiBundledResource = "PortableCodex_BundledOpenApi.json";
    private const string GptInstructionsBundledResource = "PortableCodex_GptInstructions.md";

    private static readonly Lazy<string> OpenApiTemplateJson = new(ReadBundledOpenApiTemplate);
    private static readonly Lazy<string> GptInstructionsText = new(ReadBundledGptInstructions);

    private static readonly JsonSerializerOptions OpenApiMinify = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string GptInstructions => GptInstructionsText.Value;

    public string GetRelayEnvBlock(CompanionSettings settings)
    {
        return string.Join(
            Environment.NewLine,
            $"$env:RELAY_API_KEYS=\"portable:{settings.GptApiToken}:{settings.DeviceId}\"",
            $"$env:RELAY_DEVICE_TOKENS=\"{settings.DeviceId}:{settings.DeviceToken}\"",
            "npm run dev:relay");
    }

    /// <summary>
    /// Full OpenAPI JSON for the Custom GPT Action schema field. Injects the current relay URL into <c>servers[0].url</c>.
    /// </summary>
    public string GetOpenApiJson(string? relayUrl)
    {
        var root = JsonNode.Parse(OpenApiTemplateJson.Value)!.AsObject();
        if (root["servers"] is JsonArray servers && servers.Count > 0 && servers[0] is JsonObject firstServer)
        {
            firstServer["url"] = NormalizeRelayUrl(relayUrl);
        }

        return root.ToJsonString(OpenApiMinify);
    }

    public string GetOpenApiPreview(string? relayUrl)
    {
        var full = GetOpenApiJson(relayUrl);
        const int maxChars = 1600;
        return full.Length <= maxChars
            ? full
            : full[..maxChars] + "…";
    }

    private static string NormalizeRelayUrl(string? relayUrl)
    {
        var value = string.IsNullOrWhiteSpace(relayUrl) ? "https://YOUR-RELAY-URL.example.com" : relayUrl.Trim();
        return value.TrimEnd('/');
    }

    private static string ReadBundledOpenApiTemplate()
    {
        using var stream = OpenEmbedded(OpenApiBundledResource);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string ReadBundledGptInstructions()
    {
        using var stream = OpenEmbedded(GptInstructionsBundledResource);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Stream OpenEmbedded(string logicalName)
    {
        var asm = typeof(RelayContentService).Assembly;
        var stream = asm.GetManifestResourceStream(logicalName);
        if (stream is not null)
        {
            return stream;
        }

        foreach (var name in asm.GetManifestResourceNames())
        {
            if (string.Equals(name, logicalName, StringComparison.Ordinal)
                || name.EndsWith("." + logicalName, StringComparison.Ordinal))
            {
                return asm.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"Embedded resource stream missing: {name}");
            }
        }

        throw new InvalidOperationException(
            $"Embedded resource '{logicalName}' not found. Rebuild the app after running 'npm run docs:openapi' so docs are bundled.");
    }
}
