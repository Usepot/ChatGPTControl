import type { IncomingMessage } from "node:http";
import { WebSocketServer, type WebSocket } from "ws";
import {
  type DeviceHelloMessage,
  type DeviceToRelayMessage,
  type DeviceToolResponseMessage,
  type RelayToDeviceMessage,
  type ToolAuditEntry,
  type ToolRequest,
  type ToolResponse,
} from "@portable-codex/shared";

interface ConnectedDevice {
  deviceId: string;
  deviceName: string;
  socket: WebSocket;
  connectedAt: string;
}

interface PendingRequest {
  resolve: (response: ToolResponse) => void;
  timer: NodeJS.Timeout;
}

export class DeviceBroker {
  private readonly devices = new Map<string, ConnectedDevice>();
  private readonly pending = new Map<string, PendingRequest>();
  private readonly auditLog: ToolAuditEntry[] = [];

  constructor(
    private readonly requestTimeoutMs: number,
    private readonly expectedDeviceTokens: Map<string, string>,
  ) {}

  attach(server: import("node:http").Server): WebSocketServer {
    const wss = new WebSocketServer({ noServer: true });

    server.on("upgrade", (request, socket, head) => {
      if (request.url !== "/ws/device") {
        socket.destroy();
        return;
      }

      wss.handleUpgrade(request, socket, head, (ws) => {
        this.handleConnection(ws, request);
      });
    });

    return wss;
  }

  private handleConnection(socket: WebSocket, request: IncomingMessage): void {
    let deviceId: string | undefined;
    const helloTimer = setTimeout(() => {
      socket.close(4001, "Expected device hello");
    }, 5000);

    socket.on("message", (data) => {
      let message: DeviceToRelayMessage;
      try {
        message = JSON.parse(String(data)) as DeviceToRelayMessage;
      } catch {
        socket.close(4000, "Invalid JSON");
        return;
      }

      if (message.type === "device:hello") {
        const hello = message as DeviceHelloMessage;
        const expectedToken = this.expectedDeviceTokens.get(hello.deviceId);
        if (!expectedToken || expectedToken !== hello.token) {
          socket.close(4003, "Unauthorized device");
          return;
        }

        clearTimeout(helloTimer);
        deviceId = hello.deviceId;
        const existing = this.devices.get(hello.deviceId);
        if (existing) {
          existing.socket.close(4002, "Superseded by a new session");
        }

        this.devices.set(hello.deviceId, {
          deviceId: hello.deviceId,
          deviceName: hello.deviceName,
          socket,
          connectedAt: new Date().toISOString(),
        });
        return;
      }

      if (!deviceId) {
        socket.close(4001, "Expected device hello first");
        return;
      }

      if (message.type === "tool:response") {
        this.resolveRequest(message as DeviceToolResponseMessage);
      }
    });

    socket.on("close", () => {
      clearTimeout(helloTimer);
      if (deviceId) {
        const active = this.devices.get(deviceId);
        if (active?.socket === socket) {
          this.devices.delete(deviceId);
        }
      }
    });

    socket.on("error", () => {
      socket.close();
    });
  }

  private resolveRequest(message: DeviceToolResponseMessage): void {
    const pending = this.pending.get(message.response.requestId);
    if (!pending) {
      return;
    }

    clearTimeout(pending.timer);
    this.pending.delete(message.response.requestId);
    this.updateAudit(message.response.requestId, message.response.status);
    pending.resolve(message.response);
  }

  async dispatch(toolRequest: ToolRequest): Promise<ToolResponse> {
    const targetDeviceId = toolRequest.deviceId;
    if (!targetDeviceId) {
      return {
        requestId: toolRequest.requestId,
        status: "error",
        error: {
          code: "DEVICE_ID_REQUIRED",
          message: "No target device ID supplied",
        },
      };
    }

    const device = this.devices.get(targetDeviceId);
    this.auditLog.unshift({
      requestId: toolRequest.requestId,
      tool: toolRequest.tool,
      deviceId: targetDeviceId,
      workspaceRoot: "workspaceRoot" in toolRequest ? toolRequest.workspaceRoot : undefined,
      createdAt: new Date().toISOString(),
      status: device ? "pending" : "timeout",
      argumentsSummary: summarizeRequest(toolRequest),
    });

    if (!device) {
      this.updateAudit(toolRequest.requestId, "timeout");
      return {
        requestId: toolRequest.requestId,
        status: "timeout",
        error: {
          code: "DEVICE_OFFLINE",
          message: `Device ${targetDeviceId} is not connected`,
        },
      };
    }

    const payload: RelayToDeviceMessage = {
      type: "tool:request",
      request: toolRequest,
    };

    device.socket.send(JSON.stringify(payload));
    return new Promise<ToolResponse>((resolve) => {
      const timer = setTimeout(() => {
        this.pending.delete(toolRequest.requestId);
        this.updateAudit(toolRequest.requestId, "timeout");
        resolve({
          requestId: toolRequest.requestId,
          status: "timeout",
          error: {
            code: "DEVICE_TIMEOUT",
            message: "Device did not answer before timeout",
          },
        });
      }, this.requestTimeoutMs);

      this.pending.set(toolRequest.requestId, { resolve, timer });
    });
  }

  getAuditLog(): ToolAuditEntry[] {
    return [...this.auditLog];
  }

  listConnectedDevices(): Array<{ deviceId: string; deviceName: string; connectedAt: string }> {
    return Array.from(this.devices.values()).map((device) => ({
      deviceId: device.deviceId,
      deviceName: device.deviceName,
      connectedAt: device.connectedAt,
    }));
  }

  private updateAudit(requestId: string, status: ToolAuditEntry["status"]): void {
    const entry = this.auditLog.find((item) => item.requestId === requestId);
    if (!entry) {
      return;
    }

    entry.status = status;
    entry.completedAt = new Date().toISOString();
  }
}

function summarizeRequest(request: ToolRequest): string {
  switch (request.tool) {
    case "list_trusted_workspaces":
      return "list trusted workspaces";
    case "list_dir":
      return `path=${request.path ?? "."}`;
    case "read_file":
      return `path=${request.path}`;
    case "write_file":
      return `path=${request.path}, bytes=${Buffer.byteLength(request.content)}`;
    case "apply_patch":
      return request.patch ? "codex patch" : `path=${request.path}, ops=${request.operations?.length ?? 0}`;
    case "search_files":
      return `query=${request.query}, path=${request.path ?? "."}`;
    case "stat_path":
      return `path=${request.path ?? "."}`;
    case "make_dir":
      return `path=${request.path}`;
    case "delete_path":
      return `path=${request.path}, recursive=${Boolean(request.recursive)}`;
    case "run_command":
      return `command=${request.command}, cwd=${request.workingDirectory ?? "."}`;
    default:
      return "unknown";
  }
}
