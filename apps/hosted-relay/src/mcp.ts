import {
  createRequestId,
  isToolName,
  TOOL_ROUTE_MAP,
  type ToolName,
  type ToolRequest,
  type ToolResponse,
} from "@portable-codex/shared";
import type { ApiPrincipal } from "./config.js";
import { renderOpenApiJson } from "./openapi.js";

type JsonPrimitive = string | number | boolean | null;
type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue };
type JsonObject = { [key: string]: JsonValue };

interface JsonRpcMessage {
  jsonrpc?: unknown;
  id?: unknown;
  method?: unknown;
  params?: unknown;
}

interface JsonRpcResponse {
  jsonrpc: "2.0";
  id: string | number | null;
  result?: JsonValue;
  error?: {
    code: number;
    message: string;
    data?: JsonValue;
  };
}

type McpContentItem = JsonObject;

export interface McpHttpResult {
  httpStatus: number;
  body?: unknown;
}

export interface McpRequestContext {
  body: unknown;
  principal?: ApiPrincipal;
  publicBaseUrl?: string;
  dispatch(request: ToolRequest): Promise<ToolResponse>;
}

const MCP_PROTOCOL_VERSION = "2025-03-26";
const MCP_APP_RESOURCE_URI = "ui://portable-codex/workspaces-v1.html";
const MCP_APP_MIME_TYPE = "text/html;profile=mcp-app";
const MCP_APP_SECURITY_SCHEMES: JsonObject[] = [
  {
    type: "http",
    scheme: "bearer",
  },
];
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
const DESTRUCTIVE_TOOLS = new Set<ToolName>([
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

export async function handleMcpHttpBody(context: McpRequestContext): Promise<McpHttpResult> {
  const body = context.body;
  if (Array.isArray(body)) {
    if (body.length === 0) {
      return { httpStatus: 400, body: createErrorResponse(null, -32600, "Invalid JSON-RPC batch") };
    }

    const responses = (
      await Promise.all(body.map((message) => handleJsonRpcMessage(message, context)))
    ).filter((message): message is JsonRpcResponse => message !== undefined);

    return responses.length === 0
      ? { httpStatus: 202 }
      : { httpStatus: 200, body: responses as unknown as JsonValue };
  }

  const response = await handleJsonRpcMessage(body, context);
  return response === undefined
    ? { httpStatus: 202 }
    : { httpStatus: response.error ? 400 : 200, body: response as unknown as JsonValue };
}

async function handleJsonRpcMessage(
  rawMessage: unknown,
  context: McpRequestContext,
): Promise<JsonRpcResponse | undefined> {
  if (!isRecord(rawMessage)) {
    return createErrorResponse(null, -32600, "Invalid JSON-RPC request");
  }

  const message = rawMessage as JsonRpcMessage;
  const id = normalizeJsonRpcId(message.id);
  const expectsResponse = Object.prototype.hasOwnProperty.call(rawMessage, "id");

  if (message.jsonrpc !== "2.0" || typeof message.method !== "string") {
    return expectsResponse
      ? createErrorResponse(id, -32600, "Invalid JSON-RPC request")
      : undefined;
  }

  try {
    const result = await handleJsonRpcMethod(message.method, message.params, context);
    return expectsResponse
      ? {
          jsonrpc: "2.0",
          id,
          result,
        }
      : undefined;
  } catch (error) {
    const messageText = error instanceof McpMethodError ? error.message : "Internal MCP server error";
    const code = error instanceof McpMethodError ? error.code : -32603;
    const data = error instanceof McpMethodError ? error.data : undefined;
    return expectsResponse ? createErrorResponse(id, code, messageText, data) : undefined;
  }
}

async function handleJsonRpcMethod(
  method: string,
  params: unknown,
  context: McpRequestContext,
): Promise<JsonValue> {
  switch (method) {
    case "initialize":
      return createInitializeResult(params);
    case "ping":
      return {};
    case "notifications/initialized":
      return {};
    case "tools/list":
      return {
        tools: buildMcpToolDescriptors(context.publicBaseUrl) as unknown as JsonValue,
      };
    case "tools/call":
      return await callTool(params, context);
    case "resources/list":
      return { resources: [buildMcpAppResourceDescriptor()] };
    case "resources/read":
      return readMcpResource(params, context.publicBaseUrl);
    case "prompts/list":
      return { prompts: [] };
    default:
      throw new McpMethodError(-32601, `Method not found: ${method}`);
  }
}

function createInitializeResult(params: unknown): JsonObject {
  const requestedVersion = isRecord(params) && typeof params.protocolVersion === "string"
    ? params.protocolVersion
    : MCP_PROTOCOL_VERSION;

  return {
    protocolVersion: requestedVersion,
    capabilities: {
      tools: {},
    },
    serverInfo: {
      name: "portable-codex",
      version: "0.1.0",
    },
    instructions:
      "Portable Codex exposes trusted local workspaces through MCP tools. Call list_trusted_workspaces before filesystem tools and use only returned workspaceRoot values.",
  };
}

async function callTool(params: unknown, context: McpRequestContext): Promise<JsonObject> {
  if (!isRecord(params) || typeof params.name !== "string") {
    throw new McpMethodError(-32602, "tools/call requires params.name");
  }

  const tool = params.name;
  if (!isToolName(tool)) {
    throw new McpMethodError(-32602, `Unknown tool: ${tool}`);
  }

  const args = isRecord(params.arguments) ? params.arguments : {};
  const requestId = typeof args.requestId === "string" ? args.requestId : createRequestId();
  const deviceId = typeof args.deviceId === "string" ? args.deviceId : context.principal?.defaultDeviceId;

  if (toolRequiresWorkspaceRoot(tool)) {
    const workspaceRoot = args.workspaceRoot;
    if (typeof workspaceRoot !== "string" || workspaceRoot.trim().length === 0) {
      throw new McpMethodError(-32602, "workspaceRoot is required");
    }
  }

  const request = {
    ...args,
    requestId,
    deviceId,
    tool,
  } as ToolRequest;

  const response = await context.dispatch(request);
  const structuredContent = toJsonObject({
    requestId: response.requestId,
    status: response.status,
    result: sanitizeInlineImageData(response.result) ?? null,
    error: response.error ?? null,
    approvalRequired: response.approvalRequired ?? null,
  });

  return {
    content: buildToolContent(response),
    structuredContent,
    isError: response.status !== "ok",
    _meta: {
      requestId: response.requestId,
      status: response.status,
    },
  };
}

function buildMcpToolDescriptors(publicBaseUrl?: string): JsonObject[] {
  const openApi = JSON.parse(renderOpenApiJson(publicBaseUrl ?? "https://YOUR-RELAY-URL.example.com")) as {
    paths?: Record<string, { post?: Record<string, unknown> }>;
    components?: { schemas?: Record<string, JsonValue> };
  };
  const schemas = openApi.components?.schemas ?? {};

  return (Object.entries(TOOL_ROUTE_MAP) as Array<[ToolName, string]>).map(([tool, route]) => {
    const operation = openApi.paths?.[`/${route}`]?.post ?? {};
    const inputSchema = getOperationInputSchema(operation, schemas);
    const title = toTitleCase(tool);

    return {
      name: tool,
      title,
      description: typeof operation.summary === "string" ? operation.summary : title,
      inputSchema,
      securitySchemes: MCP_APP_SECURITY_SCHEMES,
      annotations: {
        readOnlyHint: !WRITE_TOOLS.has(tool),
        destructiveHint: DESTRUCTIVE_TOOLS.has(tool),
        openWorldHint: OPEN_WORLD_TOOLS.has(tool),
        idempotentHint: !WRITE_TOOLS.has(tool),
      },
      _meta: {
        securitySchemes: MCP_APP_SECURITY_SCHEMES,
        ui: {
          resourceUri: MCP_APP_RESOURCE_URI,
          visibility: ["model", "app"],
        },
        "openai/outputTemplate": MCP_APP_RESOURCE_URI,
        "openai/widgetAccessible": true,
        "openai/toolInvocation/invoking": `Running ${title}...`,
        "openai/toolInvocation/invoked": `${title} complete`,
      },
    };
  });
}

function buildMcpAppResourceDescriptor(): JsonObject {
  return {
    uri: MCP_APP_RESOURCE_URI,
    name: "Portable Codex workspace panel",
    title: "Portable Codex",
    description: "Interactive panel for Portable Codex local workspace tools.",
    mimeType: MCP_APP_MIME_TYPE,
  };
}

function readMcpResource(params: unknown, publicBaseUrl?: string): JsonObject {
  const uri = isRecord(params) && typeof params.uri === "string" ? params.uri : MCP_APP_RESOURCE_URI;
  if (uri !== MCP_APP_RESOURCE_URI) {
    throw new McpMethodError(-32602, `Unknown resource: ${uri}`);
  }

  return {
    contents: [
      {
        uri: MCP_APP_RESOURCE_URI,
        mimeType: MCP_APP_MIME_TYPE,
        text: renderPortableCodexWidgetHtml(publicBaseUrl),
        _meta: {
          ui: {
            prefersBorder: true,
            ...(publicBaseUrl ? { domain: publicBaseUrl } : {}),
            csp: {
              connectDomains: publicBaseUrl ? [publicBaseUrl] : [],
              resourceDomains: [],
              frameDomains: [],
            },
          },
        },
      },
    ],
  };
}

function renderPortableCodexWidgetHtml(publicBaseUrl?: string): string {
  const baseUrl = JSON.stringify(publicBaseUrl ?? "");

  return `
<div id="root" class="shell">
  <section class="hero">
    <p class="eyebrow">Portable Codex</p>
    <h1>Local workspace tools are connected.</h1>
    <p class="muted">Ask ChatGPT to list trusted workspaces, inspect files, patch code, or run focused commands. Results from each tool call appear here.</p>
  </section>
  <section class="card">
    <div class="row">
      <div>
        <h2>Latest tool result</h2>
        <p id="status" class="muted">No tool has run in this app panel yet.</p>
      </div>
      <button id="refresh" type="button">List workspaces</button>
    </div>
    <pre id="output">Prompt ChatGPT with: “List my trusted workspaces.”</pre>
  </section>
</div>
<style>
  :root { color-scheme: light dark; font-family: ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
  body { margin: 0; background: transparent; color: CanvasText; }
  .shell { display: grid; gap: 14px; padding: 16px; }
  .hero, .card { border: 1px solid color-mix(in srgb, CanvasText 12%, transparent); border-radius: 18px; padding: 16px; background: color-mix(in srgb, Canvas 92%, CanvasText 8%); box-shadow: 0 12px 34px color-mix(in srgb, CanvasText 8%, transparent); }
  .eyebrow { margin: 0 0 6px; color: #0f8f6f; font-size: 12px; font-weight: 750; letter-spacing: .08em; text-transform: uppercase; }
  h1, h2 { margin: 0; line-height: 1.1; }
  h1 { font-size: 22px; }
  h2 { font-size: 15px; }
  .muted { color: color-mix(in srgb, CanvasText 65%, transparent); font-size: 13px; line-height: 1.45; }
  .row { align-items: start; display: flex; gap: 12px; justify-content: space-between; }
  button { border: 0; border-radius: 999px; cursor: pointer; font: inherit; font-size: 13px; font-weight: 700; padding: 9px 12px; color: white; background: #0f8f6f; }
  button:disabled { cursor: wait; opacity: .65; }
  pre { margin: 12px 0 0; max-height: 340px; overflow: auto; white-space: pre-wrap; word-break: break-word; border-radius: 14px; padding: 12px; background: color-mix(in srgb, CanvasText 7%, transparent); font-size: 12px; line-height: 1.45; }
</style>
<script type="module">
  const baseUrl = ${baseUrl};
  const root = document.getElementById("root");
  const status = document.getElementById("status");
  const output = document.getElementById("output");
  const refresh = document.getElementById("refresh");

  const pretty = (value) => {
    try { return typeof value === "string" ? value : JSON.stringify(value, null, 2); }
    catch { return String(value); }
  };

  const setResult = (label, value) => {
    status.textContent = label;
    output.textContent = pretty(value);
  };

  const callTool = async (name, args = {}) => {
    refresh.disabled = true;
    status.textContent = "Calling " + name + "...";
    try {
      const bridge = window.openai;
      if (bridge && typeof bridge.callTool === "function") {
        const result = await bridge.callTool(name, args);
        setResult(name + " complete", result?.structuredContent ?? result);
        return;
      }
      window.parent.postMessage({ jsonrpc: "2.0", id: "portable-codex-" + Date.now(), method: "tools/call", params: { name, arguments: args } }, "*");
      status.textContent = "Requested " + name + ". Waiting for host result...";
    } catch (error) {
      setResult(name + " failed", error instanceof Error ? error.message : error);
    } finally {
      refresh.disabled = false;
    }
  };

  refresh.addEventListener("click", () => callTool("list_trusted_workspaces"));

  window.addEventListener("message", (event) => {
    if (event.source !== window.parent) return;
    const message = event.data;
    if (!message || message.jsonrpc !== "2.0") return;
    if (message.method === "ui/notifications/tool-input") {
      setResult("Tool input", message.params);
      return;
    }
    if (message.method === "ui/notifications/tool-result") {
      setResult("Tool result", message.params?.structuredContent ?? message.params?.content ?? message.params);
    }
  }, { passive: true });

  if (baseUrl) root.dataset.baseUrl = baseUrl;
</script>
`.trim();
}

function getOperationInputSchema(
  operation: Record<string, unknown>,
  schemas: Record<string, JsonValue>,
): JsonObject {
  const schema = (((operation.requestBody as Record<string, unknown> | undefined)?.content as Record<string, unknown> | undefined)?.[
    "application/json"
  ] as Record<string, unknown> | undefined)?.schema;

  if (!isRecord(schema)) {
    return { type: "object", properties: {}, additionalProperties: true };
  }

  return dereferenceJsonSchema(schema as JsonObject, schemas) as JsonObject;
}

function dereferenceJsonSchema(
  value: JsonValue,
  schemas: Record<string, JsonValue>,
  seenRefs = new Set<string>(),
): JsonValue {
  if (Array.isArray(value)) {
    return value.map((item) => dereferenceJsonSchema(item, schemas, seenRefs));
  }

  if (!isJsonObject(value)) {
    return value;
  }

  const ref = value.$ref;
  if (typeof ref === "string" && ref.startsWith("#/components/schemas/") && !seenRefs.has(ref)) {
    const schemaName = ref.slice("#/components/schemas/".length);
    const resolved = schemas[schemaName];
    if (resolved !== undefined) {
      const nextSeenRefs = new Set(seenRefs).add(ref);
      const dereferenced = dereferenceJsonSchema(resolved, schemas, nextSeenRefs);
      const rest = { ...value };
      delete rest.$ref;
      return Object.keys(rest).length === 0
        ? dereferenced
        : { ...(isJsonObject(dereferenced) ? dereferenced : {}), ...rest };
    }
  }

  return Object.fromEntries(
    Object.entries(value).map(([key, item]) => [key, dereferenceJsonSchema(item, schemas, seenRefs)]),
  );
}

function summarizeToolResponse(response: ToolResponse): string {
  if (response.status === "ok") {
    return stringifyForContent(sanitizeInlineImageData(response.result) ?? { status: "ok" });
  }

  const code = response.error?.code ?? response.status.toUpperCase();
  const message = response.error?.message ?? `Tool returned status ${response.status}`;
  return `${response.status}: ${code}: ${message}`;
}

function buildToolContent(response: ToolResponse): McpContentItem[] {
  const content: McpContentItem[] = [
    {
      type: "text",
      text: summarizeToolResponse(response),
    },
  ];

  const image = response.status === "ok" ? extractImageContent(response.result) : undefined;
  if (image !== undefined) {
    content.push(image);
  }

  return content;
}

function extractImageContent(value: unknown): McpContentItem | undefined {
  if (!isRecord(value)) {
    return undefined;
  }

  let mimeType = typeof value.mimeType === "string" ? value.mimeType : undefined;
  let data = typeof value.data === "string"
    ? value.data
    : typeof value.base64 === "string"
      ? value.base64
      : undefined;

  if (typeof value.dataUrl === "string") {
    const parsed = /^data:([^;]+);base64,(.+)$/s.exec(value.dataUrl);
    if (parsed !== null) {
      mimeType = parsed[1];
      data = parsed[2];
    }
  }

  if (mimeType === undefined || data === undefined || data.length === 0) {
    return undefined;
  }

  return {
    type: "image",
    mimeType,
    data,
  };
}

function sanitizeInlineImageData(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map((item) => sanitizeInlineImageData(item));
  }

  if (!isRecord(value)) {
    return value;
  }

  const clone: Record<string, unknown> = {};
  const removeInlineImageFields = isInlineImageObject(value);
  let removedImageData = false;
  for (const [key, item] of Object.entries(value)) {
    if (removeInlineImageFields && (key === "dataUrl" || key === "data" || key === "base64")) {
      removedImageData = true;
      continue;
    }

    clone[key] = sanitizeInlineImageData(item);
  }

  if (removedImageData) {
    clone.inlineImageReturned = true;
  }

  return clone;
}

function isInlineImageObject(value: Record<string, unknown>): boolean {
  const mimeType = typeof value.mimeType === "string" ? value.mimeType : undefined;
  const hasImageMimeType = mimeType?.toLowerCase().startsWith("image/") === true;
  return typeof value.dataUrl === "string" ||
    (hasImageMimeType && (typeof value.data === "string" || typeof value.base64 === "string"));
}

function stringifyForContent(value: unknown): string {
  if (typeof value === "string") {
    return value;
  }

  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

function createErrorResponse(
  id: string | number | null,
  code: number,
  message: string,
  data?: JsonValue,
): JsonRpcResponse {
  return {
    jsonrpc: "2.0",
    id,
    error: {
      code,
      message,
      ...(data === undefined ? {} : { data }),
    },
  };
}

function normalizeJsonRpcId(id: unknown): string | number | null {
  return typeof id === "string" || typeof id === "number" ? id : null;
}

function toolRequiresWorkspaceRoot(tool: ToolName): boolean {
  return tool !== "list_trusted_workspaces" &&
    tool !== "get_gpt_instructions" &&
    tool !== "list_skills" &&
    tool !== "get_skill" &&
    tool !== "write_stdin" &&
    tool !== "request_permissions";
}

function toTitleCase(value: string): string {
  return value
    .replace(/_/g, " ")
    .replace(/\b\w/g, (char) => char.toUpperCase());
}

function toJsonObject(value: unknown): JsonObject {
  return JSON.parse(JSON.stringify(value)) as JsonObject;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isJsonObject(value: JsonValue): value is JsonObject {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

class McpMethodError extends Error {
  public constructor(
    public readonly code: number,
    message: string,
    public readonly data?: JsonValue,
  ) {
    super(message);
  }
}
