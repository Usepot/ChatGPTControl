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
    private readonly Action<LocalRelayStatus> _onStatusChanged;
    private readonly Action<ToolRequest, ToolResponse>? _onRelayTerminalResult;
    private readonly SemaphoreSlim _gate = new(1, 1);

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

            app.Map(
                "/ws/device",
                context => broker.HandleWebSocketAsync(context));

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

                        ToolRequest request;
                        if (string.Equals(tool, "list_trusted_workspaces", StringComparison.Ordinal))
                        {
                            request = body.Deserialize<ToolRequest>(JsonDefaults.Transport) ?? new ToolRequest();
                            request.Tool = tool;
                            request.RequestId = requestId;
                            request.DeviceId = deviceId;
                            request.WorkspaceRoot = null;
                        }
                        else
                        {
                            var workspaceRoot = body["workspaceRoot"]?.GetValue<string>();
                            if (string.IsNullOrWhiteSpace(workspaceRoot))
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                await context.Response.WriteAsJsonAsync(
                                    new ToolResponse
                                    {
                                        RequestId = requestId,
                                        Status = "error",
                                        Error = new ToolError
                                        {
                                            Code = "WORKSPACE_ROOT_REQUIRED",
                                            Message = "workspaceRoot is required",
                                        },
                                    },
                                    JsonDefaults.Transport);
                                return;
                            }

                            request = body.Deserialize<ToolRequest>(JsonDefaults.Transport) ?? new ToolRequest();
                            request.Tool = tool;
                            request.RequestId = requestId;
                            request.DeviceId = deviceId;
                            request.WorkspaceRoot = workspaceRoot;
                        }

                        var response = await broker.DispatchAsync(request, context.RequestAborted);
                        context.Response.StatusCode = string.Equals(response.Status, "error", StringComparison.Ordinal)
                            ? StatusCodes.Status400BadRequest
                            : StatusCodes.Status200OK;
                        await context.Response.WriteAsJsonAsync(response, JsonDefaults.Transport);
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

    private void SetStatus(LocalRelayStatus status)
    {
        _status = status;
        _onStatusChanged(status);
    }
}
