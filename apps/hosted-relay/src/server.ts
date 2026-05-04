import { createServer, type Server } from "node:http";
import express, { type NextFunction, type Request, type Response } from "express";
import { createRequestId, type ToolName, type ToolRequest, TOOL_ROUTE_MAP, type ToolResponse } from "@portable-codex/shared";
import type { ApiPrincipal, RelayConfig } from "./config.js";
import { DeviceBroker } from "./deviceBroker.js";
import { renderOpenApiJson, renderOpenApiYaml } from "./openapi.js";

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
  app.use(authenticate(config.apiPrincipals));

  for (const [tool, route] of Object.entries(TOOL_ROUTE_MAP) as Array<[ToolName, string]>) {
    app.post(route, async (req: AuthedRequest, res: Response) => {
      const body = (req.body ?? {}) as Partial<ToolRequest>;
      const requestId = body.requestId ?? createRequestId();
      const deviceId = body.deviceId ?? req.principal?.defaultDeviceId;
      const request =
        !toolRequiresWorkspaceRoot(tool)
          ? ({
              ...body,
              requestId,
              deviceId,
              tool,
            } as ToolRequest)
          : (() => {
              const workspaceRoot = "workspaceRoot" in body ? body.workspaceRoot : undefined;
              if (!workspaceRoot || typeof workspaceRoot !== "string") {
                relayLog("request", "validation failed", {
                  requestId,
                  tool,
                  route,
                  code: "WORKSPACE_ROOT_REQUIRED",
                  ip: req.socket.remoteAddress ?? "unknown",
                });
                res.status(400).json({
                  requestId,
                  status: "error",
                  error: {
                    code: "WORKSPACE_ROOT_REQUIRED",
                    message: "workspaceRoot is required",
                  },
                });
                return null;
              }

              return {
                ...body,
                requestId,
                deviceId,
                workspaceRoot,
                tool,
              } as ToolRequest;
            })();

      if (!request) {
        return;
      }

      const response = await broker.dispatch(request);
      const httpStatus = response.status === "error" ? 400 : 200;
      if (response.status === "error") {
        logToolHttpError(tool, request, response, route, req);
      }
      res.status(httpStatus).json(response);
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
  return tool !== "list_trusted_workspaces" && tool !== "list_skills" && tool !== "get_skill" && tool !== "request_user_input";
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
