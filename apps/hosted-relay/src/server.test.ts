import test from "node:test";
import assert from "node:assert/strict";
import { once } from "node:events";
import WebSocket from "ws";
import { TOOL_NAMES, TOOL_ROUTE_MAP, type ToolName } from "@portable-codex/shared";
import { renderOpenApiJson } from "./openapi.js";
import { startRelayServer, type RelayServer } from "./server.js";
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

const WORKSPACE_ROOT_OPTIONAL_TOOLS = new Set<ToolName>([
  "list_trusted_workspaces",
  "get_gpt_instructions",
  "list_skills",
  "get_skill",
  "write_stdin",
  "request_permissions",
]);

const WRITE_TOOLS = new Set<ToolName>([
  "write_file",
  "apply_patch",
  "delete_path",
  "run_command",
  "shell",
  "exec_command",
  "shell_command",
  "screenshot_desktop",
]);

const OPEN_WORLD_TOOLS = new Set<ToolName>([
  "run_command",
  "shell",
  "exec_command",
  "shell_command",
]);

function toolRequiresWorkspaceRoot(tool: ToolName): boolean {
  return !WORKSPACE_ROOT_OPTIONAL_TOOLS.has(tool);
}

function getRelayBaseUrl(relay: RelayServer): string {
  const address = relay.httpServer.address();
  assert.ok(address && typeof address === "object");
  return `http://127.0.0.1:${address.port}`;
}

function createToolBody(tool: ToolName): Record<string, unknown> {
  const body: Record<string, unknown> = {
    requestId: `req-${tool}`,
  };

  if (toolRequiresWorkspaceRoot(tool)) {
    body.workspaceRoot = "/tmp/workspace";
  }

  switch (tool) {
    case "list_trusted_workspaces":
    case "get_gpt_instructions":
    case "list_skills":
      return body;
    case "get_skill":
      return { ...body, skillName: "skill-creator", maxBytes: 4096 };
    case "list_dir":
      return { ...body, path: "src" };
    case "read_file":
      return { ...body, path: "README.md", encoding: "utf-8", maxBytes: 2048 };
    case "write_file":
      return { ...body, path: "notes/todo.md", content: "hello", createDirectories: false };
    case "apply_patch":
      return {
        ...body,
        path: "README.md",
        operations: [{ find: "old", replace: "new", occurrence: 1 }],
      };
    case "search_files":
      return {
        ...body,
        path: "src",
        query: "needle",
        isRegex: true,
        caseSensitive: true,
        maxResults: 5,
        fileExtensions: ["ts"],
        multithreaded: true,
        maxSearchThreads: 2,
      };
    case "stat_path":
      return { ...body, path: "src/index.ts" };
    case "make_dir":
      return { ...body, path: "generated/tests" };
    case "delete_path":
      return { ...body, path: "generated/tests", recursive: true };
    case "run_command":
      return {
        ...body,
        command: "npm test",
        workdir: "apps/hosted-relay",
        workingDirectory: "apps/hosted-relay",
        timeoutMs: 1000,
        maxOutputBytes: 1024,
        stdin: "input\n",
      };
    case "shell":
    case "exec_command":
    case "shell_command":
      return {
        ...body,
        command: ["node", "--version"],
        cmd: "node --version",
        commandLine: "node -v",
        workdir: "src",
        workingDirectory: "src",
        working_directory: "src",
        timeoutMs: 1000,
        timeout_ms: 1000,
        maxOutputBytes: 1024,
        max_output_bytes: 1024,
        yield_time_ms: 0,
        max_output_tokens: 100,
        tty: true,
        shell: "pwsh",
        login: false,
        stdin: "stdin",
        input: "input",
      };
    case "write_stdin":
      return {
        ...body,
        processId: "proc-1",
        sessionId: "proc-1",
        session_id: 1,
        chars: "x",
        yield_time_ms: 0,
        max_output_tokens: 100,
        stdin: "stdin",
        input: "input",
      };
    case "request_permissions":
      return { ...body, permissions: ["network"], reason: "test" };
    case "view_image":
      return { ...body, path: "screen.png", maxBytes: 2048 };
    case "screenshot_desktop":
      return { ...body, path: "screen.png", screen: "primary", maxBytes: 2048 };
  }
}

async function connectTestDevice(baseUrl: string): Promise<WebSocket> {
  const socket = new WebSocket(baseUrl.replace("http://", "ws://") + "/ws/device");
  await once(socket, "open");
  socket.send(
    JSON.stringify({
      type: "device:hello",
      deviceId: "test-device",
      deviceName: "Test Device",
      token: "test-device-token",
    }),
  );

  return socket;
}

async function nextSocketMessage(socket: WebSocket): Promise<Record<string, unknown>> {
  const [payload] = await once(socket, "message");
  return JSON.parse(String(payload)) as Record<string, unknown>;
}

async function postAction(baseUrl: string, tool: ToolName, body: Record<string, unknown>): Promise<Response> {
  return fetch(`${baseUrl}${TOOL_ROUTE_MAP[tool]}`, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      authorization: "Bearer test-api-token",
    },
    body: JSON.stringify(body),
  });
}

test("legacy Custom GPT Action schema advertises every shared tool route", () => {
  const openApi = JSON.parse(renderOpenApiJson("https://relay.example.test")) as {
    paths?: Record<string, { post?: Record<string, unknown> }>;
  };
  const paths = openApi.paths ?? {};

  assert.deepEqual(
    Object.keys(TOOL_ROUTE_MAP).sort(),
    [...TOOL_NAMES].sort(),
  );

  for (const tool of TOOL_NAMES) {
    const route = TOOL_ROUTE_MAP[tool];
    const operation = paths[route]?.post;
    assert.ok(operation, `${tool} is missing OpenAPI path ${route}`);
    assert.equal(operation.operationId, tool);
    assert.equal(operation["x-openai-isConsequential"], false);
    assert.ok(operation.summary, `${tool} should have an action summary`);

    const requestBody = operation.requestBody as { content?: Record<string, unknown> } | undefined;
    assert.ok(requestBody?.content?.["application/json"], `${tool} should accept JSON input`);

    const responses = operation.responses as Record<string, { content?: Record<string, unknown> }> | undefined;
    assert.ok(responses?.["200"]?.content?.["application/json"], `${tool} should return JSON`);
  }
});

test("legacy Custom GPT Action routes forward every registered tool with its payload", async () => {
  const relay = await startRelayServer(createConfig(0));
  const baseUrl = getRelayBaseUrl(relay);
  const socket = await connectTestDevice(baseUrl);

  try {
    for (const tool of TOOL_NAMES) {
      const body = createToolBody(tool);
      const messagePromise = nextSocketMessage(socket);
      const httpPromise = postAction(baseUrl, tool, body);

      const message = await messagePromise;
      assert.equal(message.type, "tool:request", `${tool} should be sent as a tool request`);
      const request = message.request as Record<string, unknown>;
      assert.equal(request.tool, tool);
      assert.equal(request.requestId, body.requestId);
      assert.equal(request.deviceId, "test-device");

      for (const [key, value] of Object.entries(body)) {
        assert.deepEqual(request[key], value, `${tool} should forward ${key}`);
      }
      if (!toolRequiresWorkspaceRoot(tool)) {
        assert.equal("workspaceRoot" in request, false, `${tool} should not synthesize workspaceRoot`);
      }

      socket.send(
        JSON.stringify({
          type: "tool:response",
          response: {
            requestId: request.requestId,
            status: "ok",
            result: { echoedTool: tool },
          },
        }),
      );

      const response = await httpPromise;
      assert.equal(response.status, 200, `${tool} should return HTTP 200 for ok device responses`);
      const json = (await response.json()) as { requestId?: string; status?: string; result?: { echoedTool?: string } };
      assert.equal(json.requestId, body.requestId);
      assert.equal(json.status, "ok");
      assert.equal(json.result?.echoedTool, tool);
    }
  } finally {
    socket.close();
    await relay.stop();
  }
});

test("legacy Custom GPT Action routes forward omitted workspaceRoot for companion current-workspace defaulting", async () => {
  const relay = await startRelayServer(createConfig(0));
  const baseUrl = getRelayBaseUrl(relay);
  const socket = await connectTestDevice(baseUrl);

  try {
    for (const tool of TOOL_NAMES.filter(toolRequiresWorkspaceRoot)) {
      const body = createToolBody(tool);
      delete body.workspaceRoot;

      const messagePromise = nextSocketMessage(socket);
      const httpPromise = postAction(baseUrl, tool, body);
      const message = await messagePromise;
      assert.equal(message.type, "tool:request", `${tool} should be sent as a tool request`);
      const request = message.request as Record<string, unknown>;
      assert.equal(request.tool, tool);
      assert.equal("workspaceRoot" in request, false, `${tool} should not require workspaceRoot at the relay`);

      socket.send(
        JSON.stringify({
          type: "tool:response",
          response: {
            requestId: request.requestId,
            status: "ok",
            result: { defaultedByCompanion: true },
          },
        }),
      );

      const response = await httpPromise;
      assert.equal(response.status, 200, `${tool} should let the companion choose the current workspace`);
      const json = (await response.json()) as { status?: string; result?: { defaultedByCompanion?: boolean } };
      assert.equal(json.status, "ok");
      assert.equal(json.result?.defaultedByCompanion, true);
    }
  } finally {
    socket.close();
    await relay.stop();
  }
});

test("mcp tools/list advertises every registered tool with OpenAPI-derived input schemas", async () => {
  const relay = await startRelayServer(createConfig(0));
  const baseUrl = getRelayBaseUrl(relay);

  try {
    const response = await fetch(`${baseUrl}/mcp`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-api-token",
      },
      body: JSON.stringify({
        jsonrpc: "2.0",
        id: 1,
        method: "tools/list",
      }),
    });

    assert.equal(response.status, 200);
    const json = (await response.json()) as {
      result?: {
        tools?: Array<{
          name?: ToolName;
          inputSchema?: { type?: string; required?: string[]; properties?: Record<string, unknown> };
          securitySchemes?: Array<{ type?: string; scheme?: string }>;
          annotations?: {
            readOnlyHint?: boolean;
            destructiveHint?: boolean;
            openWorldHint?: boolean;
            idempotentHint?: boolean;
          };
          _meta?: {
            ui?: { resourceUri?: string; visibility?: string[] };
            securitySchemes?: Array<{ type?: string; scheme?: string }>;
            "openai/outputTemplate"?: string;
            "openai/widgetAccessible"?: boolean;
          };
        }>;
      };
    };
    const tools = json.result?.tools ?? [];
    assert.deepEqual(
      tools.map((tool) => tool.name).sort(),
      [...TOOL_NAMES].sort(),
    );

    for (const toolName of TOOL_NAMES) {
      const descriptor = tools.find((tool) => tool.name === toolName);
      assert.ok(descriptor, `${toolName} should be listed`);
      assert.equal(descriptor.inputSchema?.type, "object", `${toolName} should have an object input schema`);
      assert.ok(descriptor.inputSchema?.properties, `${toolName} should expose input properties`);
      assert.ok("requestId" in descriptor.inputSchema.properties, `${toolName} should include requestId in its schema`);
      assert.deepEqual(descriptor.securitySchemes, [{ type: "http", scheme: "bearer" }]);
      assert.deepEqual(descriptor._meta?.securitySchemes, [{ type: "http", scheme: "bearer" }]);
      assert.equal(descriptor._meta?.ui?.resourceUri, "ui://portable-codex/workspaces-v1.html");
      assert.deepEqual(descriptor._meta?.ui?.visibility, ["model", "app"]);
      assert.equal(descriptor._meta?.["openai/outputTemplate"], "ui://portable-codex/workspaces-v1.html");
      assert.equal(descriptor._meta?.["openai/widgetAccessible"], true);
      assert.equal(descriptor.annotations?.readOnlyHint, !WRITE_TOOLS.has(toolName));
      assert.equal(descriptor.annotations?.destructiveHint, WRITE_TOOLS.has(toolName));
      assert.equal(descriptor.annotations?.openWorldHint, OPEN_WORLD_TOOLS.has(toolName));
      assert.equal(descriptor.annotations?.idempotentHint, !WRITE_TOOLS.has(toolName));
    }

    const readFile = tools.find((tool) => tool.name === "read_file");
    assert.deepEqual(readFile?.inputSchema?.required, ["path"]);
    assert.ok(readFile?.inputSchema?.properties && "workspaceRoot" in readFile.inputSchema.properties);
    assert.ok(readFile?.inputSchema?.properties && "path" in readFile.inputSchema.properties);

    const getSkill = tools.find((tool) => tool.name === "get_skill");
    assert.deepEqual(getSkill?.inputSchema?.required, ["skillName"]);
    assert.ok(getSkill?.inputSchema?.properties && "maxBytes" in getSkill.inputSchema.properties);
  } finally {
    await relay.stop();
  }
});

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
    assert.match(await response.text(), /# WebCodex Custom GPT System Prompt/);
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
