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
