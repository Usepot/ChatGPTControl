export interface ApiPrincipal {
  userId: string;
  token: string;
  defaultDeviceId?: string;
}

export interface RelayConfig {
  port: number;
  requestTimeoutMs: number;
  apiPrincipals: ApiPrincipal[];
  deviceTokens: Map<string, string>;
  publicBaseUrl?: string;
  artifactTtlMs?: number;
  artifactMaxBytes?: number;
}

function parseApiPrincipals(raw: string | undefined): ApiPrincipal[] {
  if (!raw) {
    return [
      {
        userId: "demo-user",
        token: "demo-api-key",
        defaultDeviceId: "demo-device",
      },
    ];
  }

  return raw.split(",").filter(Boolean).map((entry) => {
    const [userId, token, defaultDeviceId] = entry.split(":");
    if (!userId || !token) {
      throw new Error(`Invalid RELAY_API_KEYS entry: ${entry}`);
    }

    return { userId, token, defaultDeviceId };
  });
}

function parseDeviceTokens(raw: string | undefined): Map<string, string> {
  if (!raw) {
    return new Map([["demo-device", "demo-device-token"]]);
  }

  const tokens = new Map<string, string>();
  for (const entry of raw.split(",").filter(Boolean)) {
    const [deviceId, token] = entry.split(":");
    if (!deviceId || !token) {
      throw new Error(`Invalid RELAY_DEVICE_TOKENS entry: ${entry}`);
    }
    tokens.set(deviceId, token);
  }
  return tokens;
}

export function loadRelayConfig(env: NodeJS.ProcessEnv = process.env): RelayConfig {
  return {
    port: Number(env.PORT ?? 8787),
    requestTimeoutMs: Number(env.RELAY_REQUEST_TIMEOUT_MS ?? 300000),
    apiPrincipals: parseApiPrincipals(env.RELAY_API_KEYS),
    deviceTokens: parseDeviceTokens(env.RELAY_DEVICE_TOKENS),
    publicBaseUrl: env.RELAY_PUBLIC_BASE_URL?.trim() || undefined,
    artifactTtlMs: Number(env.RELAY_ARTIFACT_TTL_MS ?? 600000),
    artifactMaxBytes: Number(env.RELAY_ARTIFACT_MAX_BYTES ?? 10000000),
  };
}
