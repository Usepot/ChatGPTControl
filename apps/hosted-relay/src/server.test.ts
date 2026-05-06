import test from "node:test";
import assert from "node:assert/strict";
import { once } from "node:events";
import WebSocket from "ws";
import { startRelayServer } from "./server.js";
import type { RelayConfig } from "./config.js";

function createConfig(port: number): RelayConfig {
  return {
    port,
    requestTimeoutMs: 250,
    apiPrincipals: [
      {
        userId: "test-user",
        token: "test-api-token",
        defaultDeviceId: "test-device",
      },
    ],
    deviceTokens: new Map([["test-device", "test-device-token"]]),
  };
}

test("relay returns timeout when the device is offline", async () => {
  const relay = await startRelayServer(createConfig(8891));

  try {
    const response = await fetch("http://127.0.0.1:8891/tools/list-dir", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-api-token",
      },
      body: JSON.stringify({
        workspaceRoot: "/tmp/workspace",
      }),
    });

    assert.equal(response.status, 200);
    const json = (await response.json()) as { status: string; error?: { code: string } };
    assert.equal(json.status, "timeout");
    assert.equal(json.error?.code, "DEVICE_OFFLINE");
  } finally {
    await relay.stop();
  }
});

test("relay brokers a request to the connected device and returns the response", async () => {
  const relay = await startRelayServer(createConfig(8892));
  const socket = new WebSocket("ws://127.0.0.1:8892/ws/device");

  try {
    await once(socket, "open");
    socket.send(
      JSON.stringify({
        type: "device:hello",
        deviceId: "test-device",
        deviceName: "Test Device",
        token: "test-device-token",
      }),
    );

    const messagePromise = once(socket, "message").then(([payload]) => JSON.parse(String(payload)));
    const httpPromise = fetch("http://127.0.0.1:8892/tools/read-file", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-api-token",
      },
      body: JSON.stringify({
        workspaceRoot: "/tmp/workspace",
        path: "README.md",
      }),
    });

    const message = await messagePromise;
    assert.equal(message.type, "tool:request");
    assert.equal(message.request.tool, "read_file");

    socket.send(
      JSON.stringify({
        type: "tool:response",
        response: {
          requestId: message.request.requestId,
          status: "ok",
          result: {
            path: "README.md",
            content: "# hello",
          },
        },
      }),
    );

    const response = await httpPromise;
    const json = (await response.json()) as { status: string; result?: { content: string } };
    assert.equal(json.status, "ok");
    assert.equal(json.result?.content, "# hello");
  } finally {
    socket.close();
    await relay.stop();
  }
});

test("relay accepts list_trusted_workspaces without a workspaceRoot", async () => {
  const relay = await startRelayServer(createConfig(8893));
  const socket = new WebSocket("ws://127.0.0.1:8893/ws/device");

  try {
    await once(socket, "open");
    socket.send(
      JSON.stringify({
        type: "device:hello",
        deviceId: "test-device",
        deviceName: "Test Device",
        token: "test-device-token",
      }),
    );

    const messagePromise = once(socket, "message").then(([payload]) => JSON.parse(String(payload)));
    const httpPromise = fetch("http://127.0.0.1:8893/tools/list-trusted-workspaces", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-api-token",
      },
      body: JSON.stringify({}),
    });

    const message = await messagePromise;
    assert.equal(message.type, "tool:request");
    assert.equal(message.request.tool, "list_trusted_workspaces");
    assert.equal("workspaceRoot" in message.request, false);

    socket.send(
      JSON.stringify({
        type: "tool:response",
        response: {
          requestId: message.request.requestId,
          status: "ok",
          result: {
            workspaces: ["C:/workspace"],
          },
        },
      }),
    );

    const response = await httpPromise;
    const json = (await response.json()) as { status: string; result?: { workspaces: string[] } };
    assert.equal(json.status, "ok");
    assert.deepEqual(json.result?.workspaces, ["C:/workspace"]);
  } finally {
    socket.close();
    await relay.stop();
  }
});

test("relay forwards Codex shell alias requests", async () => {
  const relay = await startRelayServer(createConfig(8896));
  const socket = new WebSocket("ws://127.0.0.1:8896/ws/device");

  try {
    await once(socket, "open");
    socket.send(
      JSON.stringify({
        type: "device:hello",
        deviceId: "test-device",
        deviceName: "Test Device",
        token: "test-device-token",
      }),
    );

    const messagePromise = once(socket, "message").then(([payload]) => JSON.parse(String(payload)));
    const httpPromise = fetch("http://127.0.0.1:8896/tools/shell", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-api-token",
      },
      body: JSON.stringify({
        workspaceRoot: "/tmp/workspace",
        cmd: "pwd",
        working_directory: "src",
        stdin: "hello\n",
      }),
    });

    const message = await messagePromise;
    assert.equal(message.type, "tool:request");
    assert.equal(message.request.tool, "shell");
    assert.equal(message.request.workspaceRoot, "/tmp/workspace");
    assert.equal(message.request.cmd, "pwd");
    assert.equal(message.request.working_directory, "src");
    assert.equal(message.request.stdin, "hello\n");

    socket.send(
      JSON.stringify({
        type: "tool:response",
        response: {
          requestId: message.request.requestId,
          status: "ok",
          result: {
            exitCode: 0,
            stdout: "/tmp/workspace/src\n",
            stderr: "",
          },
        },
      }),
    );

    const response = await httpPromise;
    const json = (await response.json()) as { status: string; result?: { stdout: string } };
    assert.equal(json.status, "ok");
    assert.equal(json.result?.stdout, "/tmp/workspace/src\n");
  } finally {
    socket.close();
    await relay.stop();
  }
});

test("relay accepts request_permissions without a workspaceRoot", async () => {
  const relay = await startRelayServer(createConfig(8897));
  const socket = new WebSocket("ws://127.0.0.1:8897/ws/device");

  try {
    await once(socket, "open");
    socket.send(
      JSON.stringify({
        type: "device:hello",
        deviceId: "test-device",
        deviceName: "Test Device",
        token: "test-device-token",
      }),
    );

    const messagePromise = once(socket, "message").then(([payload]) => JSON.parse(String(payload)));
    const httpPromise = fetch("http://127.0.0.1:8897/tools/request-permissions", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-api-token",
      },
      body: JSON.stringify({
        permissions: ["network"],
        reason: "test",
      }),
    });

    const message = await messagePromise;
    assert.equal(message.type, "tool:request");
    assert.equal(message.request.tool, "request_permissions");
    assert.equal("workspaceRoot" in message.request, false);
    assert.deepEqual(message.request.permissions, ["network"]);

    socket.send(
      JSON.stringify({
        type: "tool:response",
        response: {
          requestId: message.request.requestId,
          status: "denied",
          error: {
            code: "REQUEST_PERMISSIONS_UNSUPPORTED",
            message: "Use trusted workspaces instead.",
          },
        },
      }),
    );

    const response = await httpPromise;
    const json = (await response.json()) as { status: string; error?: { code: string } };
    assert.equal(json.status, "denied");
    assert.equal(json.error?.code, "REQUEST_PERMISSIONS_UNSUPPORTED");
  } finally {
    socket.close();
    await relay.stop();
  }
});

test("openapi endpoint rewrites the server URL to the public request origin", async () => {
  const relay = await startRelayServer(createConfig(8894));

  try {
    const response = await fetch("http://127.0.0.1:8894/openapi.actions.json", {
      headers: {
        "x-forwarded-proto": "https",
        "x-forwarded-host": "portable-demo.tailnet.ts.net",
      },
    });

    assert.equal(response.status, 200);
    const json = (await response.json()) as { servers?: Array<{ url?: string }> };
    assert.equal(json.servers?.[0]?.url, "https://portable-demo.tailnet.ts.net");
  } finally {
    await relay.stop();
  }
});

test("instructions endpoint serves the current Custom GPT instructions", async () => {
  const relay = await startRelayServer(createConfig(8895));

  try {
    const response = await fetch("http://127.0.0.1:8895/docs/custom-gpt-instructions.md");

    assert.equal(response.status, 200);
    assert.match(response.headers.get("content-type") ?? "", /text\/markdown/);
    assert.match(await response.text(), /# Default Custom GPT System Prompt/);
  } finally {
    await relay.stop();
  }
});

test("mcp endpoint initializes and lists Portable Codex tools", async () => {
  const relay = await startRelayServer(createConfig(8898));

  try {
    const initializeResponse = await fetch("http://127.0.0.1:8898/mcp", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-api-token",
      },
      body: JSON.stringify({
        jsonrpc: "2.0",
        id: 1,
        method: "initialize",
        params: {
          protocolVersion: "2025-03-26",
          clientInfo: {
            name: "test-client",
            version: "0.0.0",
          },
        },
      }),
    });

    assert.equal(initializeResponse.status, 200);
    const initializeJson = (await initializeResponse.json()) as {
      result?: { capabilities?: { tools?: object }; serverInfo?: { name?: string } };
    };
    assert.equal(initializeJson.result?.serverInfo?.name, "portable-codex");
    assert.deepEqual(initializeJson.result?.capabilities?.tools, {});

    const listResponse = await fetch("http://127.0.0.1:8898/mcp", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-api-token",
      },
      body: JSON.stringify({
        jsonrpc: "2.0",
        id: 2,
        method: "tools/list",
      }),
    });

    assert.equal(listResponse.status, 200);
    const listJson = (await listResponse.json()) as {
      result?: {
        tools?: Array<{
          name?: string;
          inputSchema?: { type?: string };
          securitySchemes?: Array<{ type?: string; scheme?: string }>;
          annotations?: { readOnlyHint?: boolean; openWorldHint?: boolean };
          _meta?: {
            ui?: { resourceUri?: string; visibility?: string[] };
            securitySchemes?: Array<{ type?: string; scheme?: string }>;
            "openai/outputTemplate"?: string;
            "openai/widgetAccessible"?: boolean;
          };
        }>;
      };
    };
    const listTrustedWorkspaces = listJson.result?.tools?.find((tool) => tool.name === "list_trusted_workspaces");
    assert.equal(listTrustedWorkspaces?.inputSchema?.type, "object");
    assert.equal(listTrustedWorkspaces?._meta?.ui?.resourceUri, "ui://portable-codex/workspaces-v1.html");
    assert.equal(listTrustedWorkspaces?._meta?.["openai/outputTemplate"], "ui://portable-codex/workspaces-v1.html");
    assert.equal(listTrustedWorkspaces?._meta?.["openai/widgetAccessible"], true);
    assert.deepEqual(listTrustedWorkspaces?._meta?.ui?.visibility, ["model", "app"]);
    assert.deepEqual(listTrustedWorkspaces?.securitySchemes, [{ type: "http", scheme: "bearer" }]);
    assert.deepEqual(listTrustedWorkspaces?._meta?.securitySchemes, [{ type: "http", scheme: "bearer" }]);
    assert.equal(listTrustedWorkspaces?.annotations?.readOnlyHint, true);
    assert.equal(listTrustedWorkspaces?.annotations?.openWorldHint, false);
    assert.ok(listJson.result?.tools?.some((tool) => tool.name === "read_file"));
  } finally {
    await relay.stop();
  }
});

test("mcp tools/call returns local images as image content", async () => {
  const relay = await startRelayServer(createConfig(8901));
  const socket = new WebSocket("ws://127.0.0.1:8901/ws/device");

  try {
    await once(socket, "open");
    socket.send(
      JSON.stringify({
        type: "device:hello",
        deviceId: "test-device",
        deviceName: "Test Device",
        token: "test-device-token",
      }),
    );

    const messagePromise = once(socket, "message").then(([payload]) => JSON.parse(String(payload)));
    const httpPromise = fetch("http://127.0.0.1:8901/mcp", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-api-token",
      },
      body: JSON.stringify({
        jsonrpc: "2.0",
        id: "image-call-1",
        method: "tools/call",
        params: {
          name: "view_image",
          arguments: {
            workspaceRoot: "/tmp/workspace",
            path: "screen.png",
          },
        },
      }),
    });

    const message = await messagePromise;
    assert.equal(message.type, "tool:request");
    assert.equal(message.request.tool, "view_image");

    socket.send(
      JSON.stringify({
        type: "tool:response",
        response: {
          requestId: message.request.requestId,
          status: "ok",
          result: {
            path: "screen.png",
            mimeType: "image/png",
            bytes: 3,
            dataUrl: "data:image/png;base64,AAEC",
          },
        },
      }),
    );

    const response = await httpPromise;
    assert.equal(response.status, 200);
    const json = (await response.json()) as {
      result?: {
        content?: Array<{ type?: string; mimeType?: string; data?: string; text?: string }>;
        structuredContent?: { result?: { dataUrl?: string; inlineImageReturned?: boolean } };
      };
    };
    const image = json.result?.content?.find((item) => item.type === "image");
    assert.equal(image?.mimeType, "image/png");
    assert.equal(image?.data, "AAEC");
    assert.equal(json.result?.structuredContent?.result?.inlineImageReturned, true);
    assert.equal("dataUrl" in (json.result?.structuredContent?.result ?? {}), false);
  } finally {
    socket.close();
    await relay.stop();
  }
});

test("action endpoint returns local images in actionImage", async () => {
  const relay = await startRelayServer(createConfig(8902));
  const socket = new WebSocket("ws://127.0.0.1:8902/ws/device");

  try {
    await once(socket, "open");
    socket.send(
      JSON.stringify({
        type: "device:hello",
        deviceId: "test-device",
        deviceName: "Test Device",
        token: "test-device-token",
      }),
    );

    const messagePromise = once(socket, "message").then(([payload]) => JSON.parse(String(payload)));
    const httpPromise = fetch("http://127.0.0.1:8902/tools/view-image", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-api-token",
      },
      body: JSON.stringify({
        workspaceRoot: "/tmp/workspace",
        path: "screen.png",
      }),
    });

    const message = await messagePromise;
    assert.equal(message.type, "tool:request");
    assert.equal(message.request.tool, "view_image");

    socket.send(
      JSON.stringify({
        type: "tool:response",
        response: {
          requestId: message.request.requestId,
          status: "ok",
          result: {
            path: "screen.png",
            mimeType: "image/png",
            bytes: 3,
            dataUrl: "data:image/png;base64,AAEC",
          },
        },
      }),
    );

    const response = await httpPromise;
    assert.equal(response.status, 200);
    const json = (await response.json()) as {
      status?: string;
      result?: { dataUrl?: string; actionImageReturned?: boolean };
      actionImage?: { name?: string; mime_type?: string; content?: string };
    };
    assert.equal(json.status, "ok");
    assert.equal(json.actionImage?.name, "screen.png");
    assert.equal(json.actionImage?.mime_type, "image/png");
    assert.equal(json.actionImage?.content, "AAEC");
    assert.equal(json.result?.actionImageReturned, true);
    assert.equal("dataUrl" in (json.result ?? {}), false);
  } finally {
    socket.close();
    await relay.stop();
  }
});

test("mcp endpoint serves the Portable Codex app resource", async () => {
  const relay = await startRelayServer(createConfig(8900));

  try {
    const listResponse = await fetch("http://127.0.0.1:8900/mcp", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-api-token",
      },
      body: JSON.stringify({
        jsonrpc: "2.0",
        id: 1,
        method: "resources/list",
      }),
    });

    assert.equal(listResponse.status, 200);
    const listJson = (await listResponse.json()) as {
      result?: { resources?: Array<{ uri?: string; mimeType?: string; title?: string }> };
    };
    const resource = listJson.result?.resources?.find((item) => item.uri === "ui://portable-codex/workspaces-v1.html");
    assert.equal(resource?.title, "Portable Codex");
    assert.equal(resource?.mimeType, "text/html;profile=mcp-app");

    const readResponse = await fetch("http://127.0.0.1:8900/mcp", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-api-token",
        "x-forwarded-proto": "https",
        "x-forwarded-host": "portable-demo.tailnet.ts.net",
      },
      body: JSON.stringify({
        jsonrpc: "2.0",
        id: 2,
        method: "resources/read",
        params: {
          uri: "ui://portable-codex/workspaces-v1.html",
        },
      }),
    });

    assert.equal(readResponse.status, 200);
    const readJson = (await readResponse.json()) as {
      result?: {
        contents?: Array<{
          uri?: string;
          mimeType?: string;
          text?: string;
          _meta?: { ui?: { domain?: string; csp?: { connectDomains?: string[] } } };
        }>;
      };
    };
    const content = readJson.result?.contents?.[0];
    assert.equal(content?.uri, "ui://portable-codex/workspaces-v1.html");
    assert.equal(content?.mimeType, "text/html;profile=mcp-app");
    assert.match(content?.text ?? "", /Portable Codex/);
    assert.match(content?.text ?? "", /list_trusted_workspaces/);
    assert.equal(content?._meta?.ui?.domain, "https://portable-demo.tailnet.ts.net");
    assert.deepEqual(content?._meta?.ui?.csp?.connectDomains, ["https://portable-demo.tailnet.ts.net"]);
  } finally {
    await relay.stop();
  }
});

test("mcp tools/call forwards requests to the connected device", async () => {
  const relay = await startRelayServer(createConfig(8899));
  const socket = new WebSocket("ws://127.0.0.1:8899/ws/device");

  try {
    await once(socket, "open");
    socket.send(
      JSON.stringify({
        type: "device:hello",
        deviceId: "test-device",
        deviceName: "Test Device",
        token: "test-device-token",
      }),
    );

    const messagePromise = once(socket, "message").then(([payload]) => JSON.parse(String(payload)));
    const httpPromise = fetch("http://127.0.0.1:8899/mcp", {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-api-token",
      },
      body: JSON.stringify({
        jsonrpc: "2.0",
        id: "call-1",
        method: "tools/call",
        params: {
          name: "list_trusted_workspaces",
          arguments: {},
        },
      }),
    });

    const message = await messagePromise;
    assert.equal(message.type, "tool:request");
    assert.equal(message.request.tool, "list_trusted_workspaces");
    assert.equal("workspaceRoot" in message.request, false);

    socket.send(
      JSON.stringify({
        type: "tool:response",
        response: {
          requestId: message.request.requestId,
          status: "ok",
          result: {
            workspaces: ["C:/workspace"],
          },
        },
      }),
    );

    const response = await httpPromise;
    assert.equal(response.status, 200);
    const json = (await response.json()) as {
      result?: {
        isError?: boolean;
        structuredContent?: { status?: string; result?: { workspaces?: string[] } };
      };
    };
    assert.equal(json.result?.isError, false);
    assert.equal(json.result?.structuredContent?.status, "ok");
    assert.deepEqual(json.result?.structuredContent?.result?.workspaces, ["C:/workspace"]);
  } finally {
    socket.close();
    await relay.stop();
  }
});
