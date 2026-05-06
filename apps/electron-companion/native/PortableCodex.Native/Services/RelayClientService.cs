using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;

namespace PortableCodex.Native.Services;

public sealed class RelayClientService
{
    private readonly Func<CompanionSettings> _getSettings;
    private readonly Func<ToolRequest, Task<ToolResponse>> _onToolRequest;
    private readonly Action<RelayConnectionStatus> _onStatusChange;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifecycleCts;
    private Task? _loopTask;
    private bool _manuallyStopped;

    public RelayClientService(
        Func<CompanionSettings> getSettings,
        Func<ToolRequest, Task<ToolResponse>> onToolRequest,
        Action<RelayConnectionStatus> onStatusChange)
    {
        _getSettings = getSettings;
        _onToolRequest = onToolRequest;
        _onStatusChange = onStatusChange;
    }

    public void Connect()
    {
        lock (_sync)
        {
            _manuallyStopped = false;
            if (_loopTask is { IsCompleted: false })
            {
                return;
            }

            _lifecycleCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => ConnectionLoopAsync(_lifecycleCts.Token));
        }
    }

    public async Task DisconnectAsync()
    {
        Task? loopTask;
        ClientWebSocket? socket;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            _manuallyStopped = true;
            loopTask = _loopTask;
            socket = _socket;
            cts = _lifecycleCts;
            _loopTask = null;
            _lifecycleCts = null;
        }

        cts?.Cancel();
        if (socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None);
            }
            catch
            {
                // Ignore socket close errors during shutdown.
            }
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask;
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation.
            }
        }

        UpdateStatus(
            new RelayConnectionStatus
            {
                State = "disconnected",
                Message = "Disconnected",
            });
    }

    public async Task ReconnectAsync()
    {
        await DisconnectAsync();
        Connect();
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_manuallyStopped)
        {
            var settings = _getSettings();
            var relayUrl = settings.RelayUrl?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(relayUrl))
            {
                UpdateStatus(new RelayConnectionStatus
                {
                    State = "disconnected",
                    Message = "Add a relay URL in Settings to connect",
                });
                return;
            }

            if (!relayUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !relayUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                UpdateStatus(new RelayConnectionStatus
                {
                    State = "disconnected",
                    Message = "Relay URL must start with http:// or https://",
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.DeviceId) || string.IsNullOrWhiteSpace(settings.DeviceToken))
            {
                UpdateStatus(new RelayConnectionStatus
                {
                    State = "disconnected",
                    Message = "Generate or enter a device ID and device token",
                });
                return;
            }

            var wsUrl = relayUrl.Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
                .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
                .TrimEnd('/') + "/ws/device";

            UpdateStatus(new RelayConnectionStatus
            {
                State = "connecting",
                Message = $"Connecting to {relayUrl}",
            });

            using var socket = new ClientWebSocket();
            lock (_sync)
            {
                _socket = socket;
            }

            try
            {
                await socket.ConnectAsync(new Uri(wsUrl), cancellationToken);
                await SendHelloAsync(socket, settings, cancellationToken);

                UpdateStatus(new RelayConnectionStatus
                {
                    State = "connected",
                    Message = $"Connected as {settings.DeviceId}",
                });

                await ReceiveLoopAsync(socket, cancellationToken);
                UpdateStatus(new RelayConnectionStatus
                {
                    State = "disconnected",
                    Message = "Relay connection closed",
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                UpdateStatus(new RelayConnectionStatus
                {
                    State = "disconnected",
                    Message = ex.Message,
                });
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_socket, socket))
                    {
                        _socket = null;
                    }
                }
            }

            if (_manuallyStopped || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private async Task SendHelloAsync(ClientWebSocket socket, CompanionSettings settings, CancellationToken cancellationToken)
    {
        var hello = new JsonObject
        {
            ["type"] = "device:hello",
            ["deviceId"] = settings.DeviceId,
            ["deviceName"] = settings.DeviceName,
            ["token"] = settings.DeviceToken,
        };
        await SendJsonAsync(socket, hello, cancellationToken);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var payload = await ReceiveTextAsync(socket, cancellationToken);
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
                continue;
            }

            if (message is null)
            {
                continue;
            }

            var type = message["type"]?.GetValue<string>();
            if (string.Equals(type, "relay:ping", StringComparison.Ordinal))
            {
                var pong = new JsonObject
                {
                    ["type"] = "device:pong",
                    ["sentAt"] = message["sentAt"]?.GetValue<string>() ?? DateTime.UtcNow.ToString("O"),
                };
                await SendJsonAsync(socket, pong, cancellationToken);
                continue;
            }

            if (!string.Equals(type, "tool:request", StringComparison.Ordinal))
            {
                continue;
            }

            var requestNode = message["request"];
            if (requestNode is null)
            {
                continue;
            }

            ToolRequest request;
            try
            {
                request = requestNode.Deserialize<ToolRequest>(JsonDefaults.Transport)
                    ?? throw new InvalidOperationException("Invalid tool request payload");

                if (string.IsNullOrWhiteSpace(request.RequestId))
                {
                    request.RequestId = Guid.NewGuid().ToString();
                }
            }
            catch (Exception ex)
            {
                await SendToolResponseAsync(
                    socket,
                    new ToolResponse
                    {
                        RequestId = message["request"]?["requestId"]?.GetValue<string>() ?? Guid.NewGuid().ToString(),
                        Status = "error",
                        Error = new ToolError
                        {
                            Code = "REQUEST_HANDLER_ERROR",
                            Message = ex.Message,
                        },
                    },
                    cancellationToken);
                continue;
            }

            _ = Task.Run(() => HandleToolRequestAndSendResponseAsync(socket, request, cancellationToken));
        }
    }

    private async Task HandleToolRequestAndSendResponseAsync(
        ClientWebSocket socket,
        ToolRequest request,
        CancellationToken cancellationToken)
    {
        ToolResponse response;
        try
        {
            response = await _onToolRequest(request);
        }
        catch (Exception ex)
        {
            response = new ToolResponse
            {
                RequestId = request.RequestId,
                Status = "error",
                Error = new ToolError
                {
                    Code = "REQUEST_HANDLER_ERROR",
                    Message = ex.Message,
                },
            };
        }

        try
        {
            await SendToolResponseAsync(socket, response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async Task SendToolResponseAsync(ClientWebSocket socket, ToolResponse response, CancellationToken cancellationToken)
    {
        var outgoing = new JsonObject
        {
            ["type"] = "tool:response",
            ["response"] = JsonSerializer.SerializeToNode(response, JsonDefaults.Transport),
        };
        await SendJsonAsync(socket, outgoing, cancellationToken);
    }

    private async Task SendJsonAsync(ClientWebSocket socket, JsonObject payload, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not open");
        }

        var json = payload.ToJsonString(JsonDefaults.Transport);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WebSocket is not open");
            }

            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
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

    private void UpdateStatus(RelayConnectionStatus status)
    {
        _onStatusChange(status);
    }
}
