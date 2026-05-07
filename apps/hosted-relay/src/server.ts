import { createServer, type Server } from "node:http";
import express, { type NextFunction, type Request, type Response } from "express";
import { createRequestId, type ToolName, type ToolRequest, TOOL_ROUTE_MAP, type ToolResponse } from "@portable-codex/shared";
import type { ApiPrincipal, RelayConfig } from "./config.js";
import { ImageArtifactStore } from "./artifacts.js";
import { DeviceBroker } from "./deviceBroker.js";
import { handleMcpHttpBody } from "./mcp.js";
import { renderGptInstructions, renderOpenApiJson, renderOpenApiYaml } from "./openapi.js";

function relayLog(scope: string, message: string, fields: Record<string, unknown>): void {
  const line = `[relay:${scope}] ${message}`;
  console.warn(line, fields);
}

interface AuthedRequest extends Request {
  principal?: ApiPrincipal;
}

export interface RelayServer {
  httpServer: Server;
  broker: DeviceBroker;
  stop(): Promise<void>;
}

export async function startRelayServer(config: RelayConfig): Promise<RelayServer> {
  const app = express();
  app.use(express.json({ limit: "1mb" }));

  const broker = new DeviceBroker(config.requestTimeoutMs, config.deviceTokens);
  const artifacts = new ImageArtifactStore({ ttlMs: config.artifactTtlMs });
  for (const path of ["/openapi.actions.json", "/docs/openapi.actions.json"]) {
    app.get(path, (req, res) => {
      res.type("application/json").send(renderOpenApiJson(getPublicBaseUrl(req, config)));
    });
  }
  for (const path of ["/openapi.actions.yaml", "/openapi.yaml", "/docs/openapi.actions.yaml", "/docs/openapi.yaml"]) {
    app.get(path, (req, res) => {
      res.type("application/yaml").send(renderOpenApiYaml(getPublicBaseUrl(req, config)));
    });
  }
  for (const path of ["/custom-gpt-instructions.md", "/docs/custom-gpt-instructions.md"]) {
    app.get(path, (_req, res) => {
      res.type("text/markdown").send(renderGptInstructions());
    });
  }
  app.get("/artifacts/:artifactId", async (req, res) => {
    const lookup = artifacts.lookup(req.params.artifactId ?? "");
    if (lookup.status === "missing") {
      res.status(404).json({ status: "error", error: { code: "ARTIFACT_NOT_FOUND", message: "Artifact not found" } });
      return;
    }
    if (lookup.status === "expired") {
      res.status(410).json({ status: "error", error: { code: "ARTIFACT_EXPIRED", message: "Artifact expired" } });
      return;
    }

    const response = await broker.dispatch({
      tool: "view_image",
      requestId: createRequestId(),
      deviceId: lookup.record.deviceId,
      workspaceRoot: lookup.record.workspaceRoot,
      path: lookup.record.path,
      maxBytes: getArtifactMaxBytes(config),
    });

    if (response.status !== "ok") {
      res.status(502).json(response);
      return;
    }

    const image = extractInlineImageBuffer(response.result);
    if (image === undefined) {
      res.status(502).json({
        status: "error",
        error: { code: "ARTIFACT_IMAGE_UNAVAILABLE", message: "Companion did not return image bytes for artifact" },
      });
      return;
    }

    const maxBytes = getArtifactMaxBytes(config);
    if (image.buffer.byteLength > maxBytes) {
      res.status(413).json({
        status: "error",
        error: { code: "ARTIFACT_TOO_LARGE", message: `Artifact is ${image.buffer.byteLength} bytes, which exceeds maxBytes=${maxBytes}` },
      });
      return;
    }

    res
      .status(200)
      .set("cache-control", "private, max-age=600")
      .set("x-content-type-options", "nosniff")
      .type(image.mimeType)
      .send(image.buffer);
  });

  app.use(authenticate(config.apiPrincipals));

  app.post("/mcp", async (req: AuthedRequest, res: Response) => {
    const result = await handleMcpHttpBody({
      body: req.body,
      principal: req.principal,
      publicBaseUrl: getPublicBaseUrl(req, config),
      dispatch: (request) => broker.dispatch(request),
      registerImageArtifact: (input) => artifacts.register(input),
    });

    if (result.body === undefined) {
      res.status(result.httpStatus).end();
      return;
    }

    res.status(result.httpStatus).type("application/json").send(result.body);
  });

  app.get("/mcp", (_req, res) => {
    res.status(405).set("allow", "POST").end();
  });

  for (const [tool, route] of Object.entries(TOOL_ROUTE_MAP) as Array<[ToolName, string]>) {
    app.post(route, async (req: AuthedRequest, res: Response) => {
      const body = (req.body ?? {}) as Partial<ToolRequest>;
      const requestId = body.requestId ?? createRequestId();
      const deviceId = body.deviceId ?? req.principal?.defaultDeviceId;
      const request = {
        ...body,
        requestId,
        deviceId,
        tool,
      } as ToolRequest;
      if (!toolRequiresWorkspaceRoot(tool)) {
        delete (request as { workspaceRoot?: string }).workspaceRoot;
      }

      const response = await broker.dispatch(request);
      const httpStatus = response.status === "error" ? 400 : 200;
      if (response.status === "error") {
        logToolHttpError(tool, request, response, route, req);
      }
      res.status(httpStatus).json(createActionToolResponse(response, request, getPublicBaseUrl(req, config), artifacts));
    });
  }

  app.get("/health", (_req, res) => {
    res.json({
      ok: true,
      connectedDevices: broker.listConnectedDevices(),
    });
  });

  app.get("/audit", (_req, res) => {
    res.json({
      entries: broker.getAuditLog(),
    });
  });

  const httpServer = createServer(app);
  broker.attach(httpServer);

  await new Promise<void>((resolve) => {
    httpServer.listen(config.port, resolve);
  });

  return {
    httpServer,
    broker,
    stop: async () =>
      new Promise<void>((resolve, reject) => {
        httpServer.close((error) => (error ? reject(error) : resolve()));
      }),
  };
}

function createActionToolResponse(
  response: ToolResponse,
  request: ToolRequest,
  publicBaseUrl: string,
  artifacts: ImageArtifactStore,
): Record<string, unknown> {
  const payload = JSON.parse(JSON.stringify(response)) as Record<string, unknown>;
  if (response.status !== "ok") {
    return payload;
  }

  payload.result = attachImageArtifactUrl(payload.result, request, publicBaseUrl, artifacts);
  payload.result = sanitizeInlineImageData(payload.result);
  return payload;
}

function attachImageArtifactUrl(
  value: unknown,
  request: ToolRequest,
  publicBaseUrl: string,
  artifacts: ImageArtifactStore,
): unknown {
  if (!isRecord(value) || !isArtifactBackedImageTool(request.tool)) {
    return value;
  }

  const path = typeof value.path === "string" && value.path.trim().length > 0
    ? value.path
    : "path" in request && typeof request.path === "string" && request.path.trim().length > 0
      ? request.path
      : undefined;
  const mimeType = typeof value.mimeType === "string" ? value.mimeType : undefined;
  if (path === undefined || mimeType === undefined || !mimeType.toLowerCase().startsWith("image/")) {
    return value;
  }

  const record = artifacts.register({
    path,
    mimeType,
    deviceId: "deviceId" in request ? request.deviceId : undefined,
    workspaceRoot: "workspaceRoot" in request ? request.workspaceRoot : undefined,
    bytes: typeof value.bytes === "number" ? value.bytes : undefined,
  });

  return {
    ...value,
    path,
    artifactId: record.id,
    imageUrl: `${publicBaseUrl}/artifacts/${record.id}`,
    expiresAt: record.expiresAt,
  };
}

function isArtifactBackedImageTool(tool: ToolName): boolean {
  return tool === "view_image" || tool === "view_desktop";
}

function extractInlineImageBuffer(value: unknown): { mimeType: string; buffer: Buffer } | undefined {
  if (!isRecord(value)) {
    return undefined;
  }

  let mimeType = typeof value.mimeType === "string" ? value.mimeType : undefined;
  let content = typeof value.data === "string"
    ? value.data
    : typeof value.base64 === "string"
      ? value.base64
      : undefined;

  if (typeof value.dataUrl === "string") {
    const parsed = /^data:([^;]+);base64,(.+)$/s.exec(value.dataUrl);
    if (parsed !== null) {
      mimeType = parsed[1];
      content = parsed[2];
    }
  }

  if (mimeType === undefined || content === undefined || !mimeType.toLowerCase().startsWith("image/")) {
    return undefined;
  }

  return {
    mimeType,
    buffer: Buffer.from(content, "base64"),
  };
}

function getArtifactMaxBytes(config: RelayConfig): number {
  const maxBytes = config.artifactMaxBytes ?? 10_000_000;
  return Number.isFinite(maxBytes) && maxBytes > 0 ? Math.trunc(maxBytes) : 10_000_000;
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
  for (const [key, item] of Object.entries(value)) {
    if (removeInlineImageFields && (key === "dataUrl" || key === "data" || key === "base64")) {
      continue;
    }

    clone[key] = sanitizeInlineImageData(item);
  }

  return clone;
}

function isInlineImageObject(value: Record<string, unknown>): boolean {
  const mimeType = typeof value.mimeType === "string" ? value.mimeType : undefined;
  const hasImageMimeType = mimeType?.toLowerCase().startsWith("image/") === true;
  return typeof value.dataUrl === "string" ||
    (hasImageMimeType && (typeof value.data === "string" || typeof value.base64 === "string"));
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function getPublicBaseUrl(req: Request, config: RelayConfig): string {
  const configured = config.publicBaseUrl?.trim().replace(/\/+$/, "");
  if (configured) {
    return configured;
  }

  const forwardedProto = req.header("x-forwarded-proto")?.split(",")[0]?.trim();
  const forwardedHost = req.header("x-forwarded-host")?.split(",")[0]?.trim();
  const host = forwardedHost || req.header("host") || "localhost";
  const proto = forwardedProto || req.protocol || "http";
  return `${proto}://${host}`.replace(/\/+$/, "");
}

function toolRequiresWorkspaceRoot(tool: ToolName): boolean {
  return tool !== "list_trusted_workspaces" &&
    tool !== "get_gpt_instructions" &&
    tool !== "list_skills" &&
    tool !== "get_skill" &&
    tool !== "write_stdin" &&
    tool !== "request_permissions" &&
    !tool.startsWith("browser_");
}

function authenticate(apiPrincipals: ApiPrincipal[]) {
  const byToken = new Map(apiPrincipals.map((principal) => [principal.token, principal]));

  return (req: AuthedRequest, res: Response, next: NextFunction): void => {
    const header = req.header("authorization");
    const token = header?.startsWith("Bearer ") ? header.slice("Bearer ".length) : undefined;
    const ip = req.socket.remoteAddress ?? "unknown";
    const authMeta = {
      method: req.method,
      path: req.path,
      ip,
      hasAuthorizationHeader: Boolean(header),
      authScheme: header?.split(/\s+/)[0] ?? null,
    };

    if (!token) {
      relayLog("auth", "missing bearer token (401)", authMeta);
      res.status(401).json({
        status: "error",
        error: {
          code: "MISSING_BEARER_TOKEN",
          message: "Expected Authorization: Bearer <token>",
        },
      });
      return;
    }

    const principal = byToken.get(token);
    if (!principal) {
      relayLog("auth", "invalid bearer token (403)", {
        ...authMeta,
        bearerTokenLength: token.length,
        configuredApiKeys: apiPrincipals.length,
      });
      res.status(403).json({
        status: "error",
        error: {
          code: "INVALID_BEARER_TOKEN",
          message: "Bearer token is not recognized",
        },
      });
      return;
    }

    req.principal = principal;
    next();
  };
}

function logToolHttpError(
  tool: ToolName,
  request: ToolRequest,
  response: ToolResponse,
  route: string,
  req: Request,
): void {
  const err = response.error;
  relayLog("tool", "responding with HTTP 400 (tool status error)", {
    tool,
    route,
    requestId: response.requestId,
    errorCode: err?.code,
    errorMessage: err?.message,
    deviceId: "deviceId" in request ? request.deviceId : undefined,
    workspaceRoot: "workspaceRoot" in request ? request.workspaceRoot : undefined,
    ip: req.socket.remoteAddress ?? "unknown",
  });
}
