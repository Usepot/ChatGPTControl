using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;

namespace PortableCodex.Native.Services;

public sealed class LocalRelayServerService
{
    private const int ArtifactMaxBytes = 10_000_000;
    private static readonly TimeSpan ArtifactTtl = TimeSpan.FromMinutes(10);

    private readonly Action<LocalRelayStatus> _onStatusChanged;
    private readonly Action<ToolRequest, ToolResponse>? _onRelayTerminalResult;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RelayContentService _relayContentService = new();
    private readonly ConcurrentDictionary<string, ImageArtifactRecord> _artifacts = new(StringComparer.OrdinalIgnoreCase);

    private WebApplication? _app;
    private DeviceBroker? _broker;
    private LocalRelayStatus _status = new()
    {
        State = "stopped",
        Message = "Local relay is stopped",
    };

    public LocalRelayServerService(
        Action<LocalRelayStatus> onStatusChanged,
        Action<ToolRequest, ToolResponse>? onRelayTerminalResult = null)
    {
        _onStatusChanged = onStatusChanged;
        _onRelayTerminalResult = onRelayTerminalResult;
    }

    public bool IsRunning => _app is not null && string.Equals(_status.State, "running", StringComparison.Ordinal);

    public LocalRelayStatus GetStatus()
    {
        return _status;
    }

    public async Task<LocalRelayStatus> StartAsync(CompanionSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            if (_app is not null)
            {
                return _status;
            }

            SetStatus(
                new LocalRelayStatus
                {
                    State = "starting",
                    Message = "Starting local relay...",
                });

            var config = CreateConfig(settings);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ApplicationName = typeof(LocalRelayServerService).Assembly.FullName,
                EnvironmentName = Environments.Production,
            });
            builder.WebHost.UseKestrel(options => options.ListenLocalhost(config.Port));

            var app = builder.Build();
            app.UseWebSockets();
            var broker = new DeviceBroker(config.RequestTimeoutMs, config.DeviceTokens, _onRelayTerminalResult);
            Func<ToolRequest, CancellationToken, Task<ToolResponse>> dispatchToolAsync = broker.DispatchAsync;

            app.Map(
                "/ws/device",
                context => broker.HandleWebSocketAsync(context));

            foreach (var path in new[] { "/openapi.actions.json", "/docs/openapi.actions.json" })
            {
                app.MapGet(
                    path,
                    async context =>
                    {
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await context.Response.WriteAsync(_relayContentService.GetOpenApiJson(GetPublicBaseUrl(context.Request)));
                    });
            }

            foreach (var path in new[] { "/openapi.actions.yaml", "/openapi.yaml", "/docs/openapi.actions.yaml", "/docs/openapi.yaml" })
            {
                app.MapGet(
                    path,
                    async context =>
                    {
                        context.Response.ContentType = "application/yaml; charset=utf-8";
                        await context.Response.WriteAsync(_relayContentService.GetOpenApiYaml(GetPublicBaseUrl(context.Request)));
                    });
            }

            foreach (var path in new[] { "/custom-gpt-instructions.md", "/docs/custom-gpt-instructions.md" })
            {
                app.MapGet(
                    path,
                    async context =>
                    {
                        context.Response.ContentType = "text/markdown; charset=utf-8";
                        await context.Response.WriteAsync(RelayContentService.GptInstructions);
                    });
            }

            app.MapGet(
                "/artifacts/{artifactId}",
                async context =>
                {
                    var artifactId = context.Request.RouteValues["artifactId"]?.ToString() ?? string.Empty;
                    PruneExpiredArtifacts();
                    if (!_artifacts.TryGetValue(artifactId, out var artifact))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        await context.Response.WriteAsJsonAsync(
                            new
                            {
                                status = "error",
                                error = new
                                {
                                    code = "ARTIFACT_NOT_FOUND",
                                    message = "Artifact not found",
                                },
                            },
                            JsonDefaults.Transport);
                        return;
                    }

                    if (artifact.ExpiresAt <= DateTimeOffset.UtcNow)
                    {
                        _artifacts.TryRemove(artifactId, out _);
                        context.Response.StatusCode = StatusCodes.Status410Gone;
                        await context.Response.WriteAsJsonAsync(
                            new
                            {
                                status = "error",
                                error = new
                                {
                                    code = "ARTIFACT_EXPIRED",
                                    message = "Artifact expired",
                                },
                            },
                            JsonDefaults.Transport);
                        return;
                    }

                    var response = await broker.DispatchAsync(
                        new ToolRequest
                        {
                            RequestId = Guid.NewGuid().ToString(),
                            DeviceId = artifact.DeviceId,
                            WorkspaceRoot = artifact.WorkspaceRoot,
                            Tool = "view_image",
                            Path = artifact.Path,
                            MaxBytes = ArtifactMaxBytes,
                        },
                        context.RequestAborted);

                    if (!string.Equals(response.Status, "ok", StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = StatusCodes.Status502BadGateway;
                        await context.Response.WriteAsJsonAsync(response, JsonDefaults.Transport);
                        return;
                    }

                    if (!TryExtractImageBuffer(response.Result, out var mimeType, out var bytes))
                    {
                        context.Response.StatusCode = StatusCodes.Status502BadGateway;
                        await context.Response.WriteAsJsonAsync(
                            new
                            {
                                status = "error",
                                error = new
                                {
                                    code = "ARTIFACT_IMAGE_UNAVAILABLE",
                                    message = "Companion did not return image bytes for artifact",
                                },
                            },
                            JsonDefaults.Transport);
                        return;
                    }

                    if (bytes.Length > ArtifactMaxBytes)
                    {
                        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                        await context.Response.WriteAsJsonAsync(
                            new
                            {
                                status = "error",
                                error = new
                                {
                                    code = "ARTIFACT_TOO_LARGE",
                                    message = $"Artifact is {bytes.Length} bytes, which exceeds maxBytes={ArtifactMaxBytes}",
                                },
                            },
                            JsonDefaults.Transport);
                        return;
                    }

                    context.Response.Headers.CacheControl = "private, max-age=600";
                    context.Response.Headers.XContentTypeOptions = "nosniff";
                    context.Response.ContentType = mimeType;
                    await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
                });

            app.MapPost(
                "/mcp",
                async context =>
                {
                    if (!TryAuthorize(context, config.ApiPrincipals, out var principal, out var authResult))
                    {
                        await authResult.ExecuteAsync(context);
                        return;
                    }

                    var bodyResult = await ReadJsonNodeAsync(context.Request);
                    if (!bodyResult.Ok)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await context.Response.WriteAsJsonAsync(
                            new JsonObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = null,
                                ["error"] = new JsonObject
                                {
                                    ["code"] = -32700,
                                    ["message"] = "Parse error",
                                },
                            },
                            JsonDefaults.Transport);
                        return;
                    }

                    var result = await McpProtocolService.HandleHttpBodyAsync(
                        bodyResult.Body,
                        principal,
                        _relayContentService.GetOpenApiJson(GetPublicBaseUrl(context.Request)),
                        dispatchToolAsync,
                        context.RequestAborted);

                    context.Response.StatusCode = result.StatusCode;
                    if (result.Body is not null)
                    {
                        await context.Response.WriteAsJsonAsync(result.Body, JsonDefaults.Transport);
                    }
                });

            app.MapGet(
                "/mcp",
                context =>
                {
                    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    context.Response.Headers.Allow = "POST";
                    return Task.CompletedTask;
                });

            foreach (var (tool, route) in ProtocolConstants.ToolRouteMap)
            {
                app.MapPost(
                    route,
                    async context =>
                    {
                        if (!TryAuthorize(context, config.ApiPrincipals, out var principal, out var authResult))
                        {
                            await authResult.ExecuteAsync(context);
                            return;
                        }

                        var bodyResult = await ReadBodyAsync(context.Request);
                        if (!bodyResult.Ok)
                        {
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsJsonAsync(
                                new ToolResponse
                                {
                                    RequestId = Guid.NewGuid().ToString(),
                                    Status = "error",
                                    Error = new ToolError
                                    {
                                        Code = "INVALID_JSON",
                                        Message = "Request body must be valid JSON",
                                    },
                                },
                                JsonDefaults.Transport);
                            return;
                        }

                        var body = bodyResult.Body;
                        var requestId = body["requestId"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
                        var deviceId = body["deviceId"]?.GetValue<string>() ?? principal.DefaultDeviceId;

                        var request = body.Deserialize<ToolRequest>(JsonDefaults.Transport) ?? new ToolRequest();
                        request.Tool = tool;
                        request.RequestId = requestId;
                        request.DeviceId = deviceId;
                        if (!ToolRequiresWorkspaceRoot(tool))
                        {
                            request.WorkspaceRoot = null;
                        }

                        var response = await broker.DispatchAsync(request, context.RequestAborted);
                        context.Response.StatusCode = string.Equals(response.Status, "error", StringComparison.Ordinal)
                            ? StatusCodes.Status400BadRequest
                            : StatusCodes.Status200OK;
                        await context.Response.WriteAsJsonAsync(
                            CreateActionToolResponse(response, request, GetPublicBaseUrl(context.Request)),
                            JsonDefaults.Transport);
                    });
            }

            app.MapGet(
                "/health",
                async context =>
                {
                    if (!TryAuthorize(context, config.ApiPrincipals, out _, out var authResult))
                    {
                        await authResult.ExecuteAsync(context);
                        return;
                    }

                    await context.Response.WriteAsJsonAsync(
                        new
                        {
                            ok = true,
                            connectedDevices = broker.ListConnectedDevices(),
                        },
                        JsonDefaults.Transport);
                });

            app.MapGet(
                "/audit",
                async context =>
                {
                    if (!TryAuthorize(context, config.ApiPrincipals, out _, out var authResult))
                    {
                        await authResult.ExecuteAsync(context);
                        return;
                    }

                    await context.Response.WriteAsJsonAsync(
                        new
                        {
                            entries = broker.GetAuditLog(),
                        },
                        JsonDefaults.Transport);
                });

            await app.StartAsync();
            _app = app;
            _broker = broker;

            SetStatus(
                new LocalRelayStatus
                {
                    State = "running",
                    Message = $"Local relay running on http://localhost:{config.Port}",
                });
            return _status;
        }
        catch (Exception ex)
        {
            _app = null;
            _broker = null;
            SetStatus(
                new LocalRelayStatus
                {
                    State = "error",
                    Message = ex.Message,
                });
            return _status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalRelayStatus> StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_app is null)
            {
                SetStatus(
                    new LocalRelayStatus
                    {
                        State = "stopped",
                        Message = "Local relay is stopped",
                    });
                return _status;
            }

            var app = _app;
            _app = null;
            _broker = null;
            await app.StopAsync();
            await app.DisposeAsync();

            SetStatus(
                new LocalRelayStatus
                {
                    State = "stopped",
                    Message = "Local relay is stopped",
                });
            return _status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalRelayStatus> RestartAsync(CompanionSettings settings)
    {
        await StopAsync();
        return await StartAsync(settings);
    }

    private static bool TryAuthorize(HttpContext context, IEnumerable<ApiPrincipal> principals, out ApiPrincipal principal, out IResult result)
    {
        var byToken = principals.ToDictionary(item => item.Token, item => item, StringComparer.Ordinal);
        var header = context.Request.Headers.Authorization.FirstOrDefault();
        var token = header?.StartsWith("Bearer ", StringComparison.Ordinal) == true
            ? header["Bearer ".Length..]
            : null;

        if (string.IsNullOrWhiteSpace(token))
        {
            principal = new ApiPrincipal();
            result = Results.Json(
                new
                {
                    status = "error",
                    error = new
                    {
                        code = "MISSING_BEARER_TOKEN",
                        message = "Expected Authorization: Bearer <token>",
                    },
                },
                statusCode: StatusCodes.Status401Unauthorized,
                options: JsonDefaults.Transport);
            return false;
        }

        if (!byToken.TryGetValue(token, out principal!))
        {
            result = Results.Json(
                new
                {
                    status = "error",
                    error = new
                    {
                        code = "INVALID_BEARER_TOKEN",
                        message = "Bearer token is not recognized",
                    },
                },
                statusCode: StatusCodes.Status403Forbidden,
                options: JsonDefaults.Transport);
            return false;
        }

        result = Results.Empty;
        return true;
    }

    private static async Task<(bool Ok, JsonObject Body)> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return (true, new JsonObject());
        }

        try
        {
            var node = JsonNode.Parse(text);
            return node is JsonObject body ? (true, body) : (false, new JsonObject());
        }
        catch
        {
            return (false, new JsonObject());
        }
    }

    private static async Task<(bool Ok, JsonNode? Body)> ReadJsonNodeAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return (false, null);
        }

        try
        {
            return (true, JsonNode.Parse(text));
        }
        catch
        {
            return (false, null);
        }
    }

    private static RelayConfig CreateConfig(CompanionSettings settings)
    {
        var port = GetLocalRelayPort(settings.RelayUrl);
        return new RelayConfig
        {
            Port = port,
            RequestTimeoutMs = 30_000,
            ApiPrincipals =
            [
                new ApiPrincipal
                {
                    UserId = "portable",
                    Token = settings.GptApiToken,
                    DefaultDeviceId = settings.DeviceId,
                },
            ],
            DeviceTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [settings.DeviceId] = settings.DeviceToken,
            },
        };
    }

    private static int GetLocalRelayPort(string relayUrl)
    {
        if (Uri.TryCreate(relayUrl, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase)) &&
            uri.Port > 0)
        {
            return uri.Port;
        }

        return 8787;
    }

    private static bool ToolRequiresWorkspaceRoot(string tool)
    {
        return !string.Equals(tool, "list_trusted_workspaces", StringComparison.Ordinal) &&
               !string.Equals(tool, "get_gpt_instructions", StringComparison.Ordinal) &&
               !string.Equals(tool, "list_skills", StringComparison.Ordinal) &&
               !string.Equals(tool, "get_skill", StringComparison.Ordinal) &&
               !string.Equals(tool, "write_stdin", StringComparison.Ordinal) &&
               !string.Equals(tool, "request_permissions", StringComparison.Ordinal) &&
               !string.Equals(tool, "click_desktop", StringComparison.Ordinal);
    }

    private JsonObject CreateActionToolResponse(ToolResponse response, ToolRequest request, string publicBaseUrl)
    {
        var payload = JsonSerializer.SerializeToNode(response, JsonDefaults.Transport) as JsonObject ?? new JsonObject();
        if (string.Equals(response.Status, "ok", StringComparison.Ordinal))
        {
            payload["result"] = AttachImageArtifactUrl(payload["result"], request, publicBaseUrl);
            payload["result"] = SanitizeInlineImageData(payload["result"]);
        }

        return payload;
    }

    private JsonNode? AttachImageArtifactUrl(JsonNode? node, ToolRequest request, string publicBaseUrl)
    {
        if (node is not JsonObject obj || !IsArtifactBackedImageTool(request.Tool))
        {
            return node;
        }

        var path = GetString(obj, "path") ?? request.Path;
        var mimeType = GetString(obj, "mimeType");
        if (string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(mimeType) ||
            !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        PruneExpiredArtifacts();
        var id = Guid.NewGuid().ToString();
        var expiresAt = DateTimeOffset.UtcNow.Add(ArtifactTtl);
        _artifacts[id] = new ImageArtifactRecord(
            id,
            request.DeviceId,
            request.WorkspaceRoot,
            path,
            mimeType,
            TryGetLong(obj, "bytes"),
            expiresAt);

        obj["path"] = path;
        obj["artifactId"] = id;
        obj["imageUrl"] = $"{publicBaseUrl}/artifacts/{id}";
        obj["expiresAt"] = expiresAt.ToString("O");
        return obj;
    }

    private void PruneExpiredArtifacts()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, artifact) in _artifacts)
        {
            if (artifact.ExpiresAt <= now)
            {
                _artifacts.TryRemove(id, out _);
            }
        }
    }

    private static bool IsArtifactBackedImageTool(string tool)
    {
        return string.Equals(tool, "view_image", StringComparison.Ordinal) ||
               string.Equals(tool, "view_desktop", StringComparison.Ordinal) ||
               string.Equals(tool, "screenshot_desktop", StringComparison.Ordinal);
    }

    private static bool TryExtractImageBuffer(JsonNode? result, out string mimeType, out byte[] bytes)
    {
        mimeType = string.Empty;
        bytes = [];
        if (result is not JsonObject obj)
        {
            return false;
        }

        mimeType = GetString(obj, "mimeType") ?? string.Empty;
        var content = GetString(obj, "data") ?? GetString(obj, "base64");
        var dataUrl = GetString(obj, "dataUrl");
        if (!string.IsNullOrWhiteSpace(dataUrl))
        {
            var parsed = TryParseDataUrl(dataUrl);
            if (parsed is not null)
            {
                mimeType = parsed.Value.MimeType;
                content = parsed.Value.Content;
            }
        }

        if (string.IsNullOrWhiteSpace(mimeType) ||
            string.IsNullOrWhiteSpace(content) ||
            !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (string MimeType, string Content)? TryParseDataUrl(string value)
    {
        const string marker = ";base64,";
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var mimeType = value["data:".Length..markerIndex];
        var content = value[(markerIndex + marker.Length)..];
        return string.IsNullOrWhiteSpace(mimeType) || string.IsNullOrWhiteSpace(content)
            ? null
            : (mimeType, content);
    }

    private static JsonNode? SanitizeInlineImageData(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonArray array:
            {
                var clone = new JsonArray();
                foreach (var item in array)
                {
                    clone.Add(SanitizeInlineImageData(item));
                }

                return clone;
            }
            case JsonObject obj:
            {
                var clone = new JsonObject();
                var removeInlineImageFields = IsInlineImageObject(obj);
                foreach (var (key, value) in obj)
                {
                    if (removeInlineImageFields &&
                        (string.Equals(key, "dataUrl", StringComparison.Ordinal) ||
                         string.Equals(key, "data", StringComparison.Ordinal) ||
                         string.Equals(key, "base64", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    clone[key] = SanitizeInlineImageData(value);
                }

                return clone;
            }
            default:
                return node.DeepClone();
        }
    }

    private static bool IsInlineImageObject(JsonObject obj)
    {
        var mimeType = GetString(obj, "mimeType");
        var hasImageMimeType = mimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
        return !string.IsNullOrWhiteSpace(GetString(obj, "dataUrl")) ||
               (hasImageMimeType &&
                (!string.IsNullOrWhiteSpace(GetString(obj, "data")) ||
                 !string.IsNullOrWhiteSpace(GetString(obj, "base64"))));
    }

    private static string? GetString(JsonObject obj, string key)
    {
        try
        {
            return obj[key]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static long? TryGetLong(JsonObject obj, string key)
    {
        try
        {
            return obj[key]?.GetValue<long>();
        }
        catch
        {
            return null;
        }
    }

    private static string GetPublicBaseUrl(HttpRequest request)
    {
        var forwardedProto = request.Headers["x-forwarded-proto"].FirstOrDefault()?.Split(',')[0].Trim();
        var forwardedHost = request.Headers["x-forwarded-host"].FirstOrDefault()?.Split(',')[0].Trim();
        var host = !string.IsNullOrWhiteSpace(forwardedHost)
            ? forwardedHost
            : request.Host.HasValue
                ? request.Host.Value
                : "localhost:8787";
        var proto = !string.IsNullOrWhiteSpace(forwardedProto) ? forwardedProto : request.Scheme;
        return $"{proto}://{host}".TrimEnd('/');
    }

    private void SetStatus(LocalRelayStatus status)
    {
        _status = status;
        _onStatusChanged(status);
    }

    private sealed record ImageArtifactRecord(
        string Id,
        string? DeviceId,
        string? WorkspaceRoot,
        string Path,
        string MimeType,
        long? Bytes,
        DateTimeOffset ExpiresAt);
}
