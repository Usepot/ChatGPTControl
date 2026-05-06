using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;

namespace PortableCodex.Native.Services;

public sealed class DeviceBroker
{
    private readonly int _requestTimeoutMs;
    private readonly Dictionary<string, string> _expectedDeviceTokens;
    private readonly Action<ToolRequest, ToolResponse>? _onRelayTerminalResult;
    private readonly ConcurrentDictionary<string, ConnectedDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly List<ToolAuditEntry> _auditLog = [];
    private readonly object _auditLock = new();

    public DeviceBroker(
        int requestTimeoutMs,
        Dictionary<string, string> expectedDeviceTokens,
        Action<ToolRequest, ToolResponse>? onRelayTerminalResult = null)
    {
        _requestTimeoutMs = requestTimeoutMs;
        _expectedDeviceTokens = expectedDeviceTokens;
        _onRelayTerminalResult = onRelayTerminalResult;
    }

    public async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        string? connectedDeviceId = null;

        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                if (connectedDeviceId is null)
                {
                    receiveCts.CancelAfter(TimeSpan.FromSeconds(5));
                }

                string? payload;
                try
                {
                    payload = await ReceiveTextAsync(socket, receiveCts.Token);
                }
                catch (OperationCanceledException) when (connectedDeviceId is null)
                {
                    await CloseAsync(socket, "Expected device hello");
                    return;
                }

                if (payload is null)
                {
                    return;
                }

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(payload)?.AsObject();
                }
                catch
                {
                    await CloseAsync(socket, "Invalid JSON");
                    return;
                }

                if (message is null)
                {
                    await CloseAsync(socket, "Invalid JSON");
                    return;
                }

                var type = message["type"]?.GetValue<string>();
                if (string.Equals(type, "device:hello", StringComparison.Ordinal))
                {
                    var helloDeviceId = message["deviceId"]?.GetValue<string>();
                    var token = message["token"]?.GetValue<string>();
                    var deviceName = message["deviceName"]?.GetValue<string>() ?? helloDeviceId ?? "Unknown device";
                    if (string.IsNullOrWhiteSpace(helloDeviceId) || string.IsNullOrWhiteSpace(token))
                    {
                        await CloseAsync(socket, "Unauthorized device");
                        return;
                    }

                    if (!_expectedDeviceTokens.TryGetValue(helloDeviceId, out var expectedToken) ||
                        !string.Equals(expectedToken, token, StringComparison.Ordinal))
                    {
                        await CloseAsync(socket, "Unauthorized device");
                        return;
                    }

                    if (_devices.TryGetValue(helloDeviceId, out var existing))
                    {
                        await CloseAsync(existing.Socket, "Superseded by a new session");
                    }

                    connectedDeviceId = helloDeviceId;
                    _devices[helloDeviceId] = new ConnectedDevice(helloDeviceId, deviceName, socket, DateTime.UtcNow.ToString("O"));
                    continue;
                }

                if (connectedDeviceId is null)
                {
                    await CloseAsync(socket, "Expected device hello first");
                    return;
                }

                if (!string.Equals(type, "tool:response", StringComparison.Ordinal))
                {
                    continue;
                }

                var response = message["response"]?.Deserialize<ToolResponse>(JsonDefaults.Transport);
                if (response is null || string.IsNullOrWhiteSpace(response.RequestId))
                {
                    continue;
                }

                ResolveRequest(response);
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(connectedDeviceId) &&
                _devices.TryGetValue(connectedDeviceId, out var device) &&
                ReferenceEquals(device.Socket, socket))
            {
                _devices.TryRemove(connectedDeviceId, out _);
            }
        }
    }

    public async Task<ToolResponse> DispatchAsync(ToolRequest toolRequest, CancellationToken cancellationToken)
    {
        var targetDeviceId = toolRequest.DeviceId;
        if (string.IsNullOrWhiteSpace(targetDeviceId))
        {
            var missingDeviceResponse = new ToolResponse
            {
                RequestId = toolRequest.RequestId,
                Status = "error",
                Error = new ToolError
                {
                    Code = "DEVICE_ID_REQUIRED",
                    Message = "No target device ID supplied",
                },
            };
            NotifyRelayTerminal(toolRequest, missingDeviceResponse);
            return missingDeviceResponse;
        }

        _devices.TryGetValue(targetDeviceId, out var device);
        lock (_auditLock)
        {
            _auditLog.Insert(
                0,
                new ToolAuditEntry
                {
                    RequestId = toolRequest.RequestId,
                    Tool = toolRequest.Tool,
                    DeviceId = targetDeviceId,
                    WorkspaceRoot = toolRequest.WorkspaceRoot,
                    CreatedAt = DateTime.UtcNow.ToString("O"),
                    Status = device is null ? "timeout" : "pending",
                    ArgumentsSummary = SummarizeRequest(toolRequest),
                });
        }

        if (device is null)
        {
            UpdateAudit(toolRequest.RequestId, "timeout");
            var offlineResponse = new ToolResponse
            {
                RequestId = toolRequest.RequestId,
                Status = "timeout",
                Error = new ToolError
                {
                    Code = "DEVICE_OFFLINE",
                    Message = $"Device {targetDeviceId} is not connected",
                },
            };
            NotifyRelayTerminal(toolRequest, offlineResponse);
            return offlineResponse;
        }

        var pending = new PendingRequest();
        _pending[toolRequest.RequestId] = pending;
        try
        {
            var payload = new JsonObject
            {
                ["type"] = "tool:request",
                ["request"] = JsonSerializer.SerializeToNode(toolRequest, JsonDefaults.Transport),
            };
            await SendJsonAsync(device, payload, cancellationToken);
        }
        catch
        {
            _pending.TryRemove(toolRequest.RequestId, out _);
            UpdateAudit(toolRequest.RequestId, "timeout");
            var sendFailedResponse = new ToolResponse
            {
                RequestId = toolRequest.RequestId,
                Status = "timeout",
                Error = new ToolError
                {
                    Code = "DEVICE_OFFLINE",
                    Message = $"Device {targetDeviceId} is not connected",
                },
            };
            NotifyRelayTerminal(toolRequest, sendFailedResponse);
            return sendFailedResponse;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(_requestTimeoutMs, CancellationToken.None);
                    if (_pending.TryRemove(toolRequest.RequestId, out var timeoutPending))
                    {
                        UpdateAudit(toolRequest.RequestId, "timeout");
                        timeoutPending.SetResult(
                            new ToolResponse
                            {
                                RequestId = toolRequest.RequestId,
                                Status = "timeout",
                                Error = new ToolError
                                {
                                    Code = "DEVICE_TIMEOUT",
                                    Message = "Device did not answer before timeout",
                                },
                            });
                    }
                }
                catch
                {
                    // Ignore timeout scheduling errors.
                }
            });

        using var registration = cancellationToken.Register(
            () =>
            {
                if (_pending.TryRemove(toolRequest.RequestId, out var cancelledPending))
                {
                    cancelledPending.SetResult(
                        new ToolResponse
                        {
                            RequestId = toolRequest.RequestId,
                            Status = "timeout",
                            Error = new ToolError
                            {
                                Code = "DEVICE_TIMEOUT",
                                Message = "Request was cancelled",
                            },
                        });
                }
            });

        return await pending.Task;
    }

    public List<ToolAuditEntry> GetAuditLog()
    {
        lock (_auditLock)
        {
            return _auditLog.Select(CloneAuditEntry).ToList();
        }
    }

    public List<object> ListConnectedDevices()
    {
        return _devices.Values
            .Select(device => (object)new
            {
                deviceId = device.DeviceId,
                deviceName = device.DeviceName,
                connectedAt = device.ConnectedAt,
            })
            .ToList();
    }

    private void ResolveRequest(ToolResponse response)
    {
        if (!_pending.TryRemove(response.RequestId, out var pending))
        {
            return;
        }

        UpdateAudit(response.RequestId, response.Status);
        pending.SetResult(response);
    }

    private void UpdateAudit(string requestId, string status)
    {
        lock (_auditLock)
        {
            var entry = _auditLog.FirstOrDefault(item => item.RequestId == requestId);
            if (entry is null)
            {
                return;
            }

            entry.Status = status;
            entry.CompletedAt = DateTime.UtcNow.ToString("O");
        }
    }

    private void NotifyRelayTerminal(ToolRequest request, ToolResponse response)
    {
        try
        {
            _onRelayTerminalResult?.Invoke(request, response);
        }
        catch
        {
            // UI/logging must not break the relay.
        }
    }

    private static string SummarizeRequest(ToolRequest request)
    {
        return request.Tool switch
        {
            "list_trusted_workspaces" => "list trusted workspaces",
            "list_skills" => "list skills",
            "get_skill" => $"skill=/{request.SkillName}",
            "list_dir" => $"path={request.Path ?? "."}",
            "read_file" => $"path={request.Path}",
            "write_file" => $"path={request.Path}, bytes={Encoding.UTF8.GetByteCount(request.Content ?? string.Empty)}",
            "apply_patch" => string.IsNullOrWhiteSpace(request.Patch)
                ? $"path={request.Path}, ops={request.Operations?.Count ?? 0}"
                : "codex patch",
            "search_files" => $"query={request.Query}, path={request.Path ?? "."}",
            "stat_path" => $"path={request.Path ?? "."}",
            "make_dir" => $"path={request.Path}",
            "delete_path" => $"path={request.Path}, recursive={request.Recursive == true}",
            "run_command" or "shell" or "exec_command" or "shell_command" => $"command={SummarizeCommand(request)}, cwd={request.WorkingDirectory ?? request.WorkingDirectorySnake ?? request.Workdir ?? "."}",
            "write_stdin" => $"process={request.ProcessId ?? request.SessionId ?? request.SessionIdSnake?.ToString() ?? "unknown"}",
            "request_permissions" => $"permissions={string.Join(",", request.Permissions ?? [])}",
            "view_image" => $"path={request.Path}",
            "screenshot_desktop" => $"path={request.Path ?? "desktop_screenshot.png"}, screen={request.Screen ?? "primary"}",
            _ => "unknown",
        };
    }

    private static string SummarizeCommand(ToolRequest request)
    {
        if (request.Command is JsonArray commandArray)
        {
            return string.Join(" ", commandArray.Select(item => item?.GetValue<string>() ?? string.Empty));
        }

        if (request.Command is JsonValue commandValue && commandValue.TryGetValue<string>(out var commandString))
        {
            return commandString;
        }

        return request.Cmd ?? request.CommandLine ?? string.Empty;
    }

    private static ToolAuditEntry CloneAuditEntry(ToolAuditEntry source)
    {
        return new ToolAuditEntry
        {
            RequestId = source.RequestId,
            Tool = source.Tool,
            DeviceId = source.DeviceId,
            WorkspaceRoot = source.WorkspaceRoot,
            CreatedAt = source.CreatedAt,
            CompletedAt = source.CompletedAt,
            Status = source.Status,
            ArgumentsSummary = source.ArgumentsSummary,
        };
    }

    private static async Task SendJsonAsync(ConnectedDevice device, JsonObject payload, CancellationToken cancellationToken)
    {
        if (device.Socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not open");
        }

        var json = payload.ToJsonString(JsonDefaults.Transport);
        var bytes = Encoding.UTF8.GetBytes(json);
        await device.SendGate.WaitAsync(cancellationToken);
        try
        {
            if (device.Socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WebSocket is not open");
            }

            await device.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            device.SendGate.Release();
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }

    private static async Task CloseAsync(WebSocket socket, string reason)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None);
            }
            catch
            {
                // Ignore close exceptions.
            }
        }
    }

    private sealed class ConnectedDevice
    {
        public ConnectedDevice(string deviceId, string deviceName, WebSocket socket, string connectedAt)
        {
            DeviceId = deviceId;
            DeviceName = deviceName;
            Socket = socket;
            ConnectedAt = connectedAt;
        }

        public string DeviceId { get; }

        public string DeviceName { get; }

        public WebSocket Socket { get; }

        public SemaphoreSlim SendGate { get; } = new(1, 1);

        public string ConnectedAt { get; }
    }

    private sealed class PendingRequest
    {
        private readonly TaskCompletionSource<ToolResponse> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ToolResponse> Task => _tcs.Task;

        public void SetResult(ToolResponse response)
        {
            _tcs.TrySetResult(response);
        }
    }
}
