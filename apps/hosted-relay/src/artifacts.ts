import { randomUUID } from "node:crypto";

export interface ImageArtifactRecord {
  id: string;
  deviceId?: string;
  workspaceRoot?: string;
  path: string;
  mimeType: string;
  bytes?: number;
  expiresAt: string;
}

export interface ImageArtifactStoreOptions {
  ttlMs?: number;
}

export type ImageArtifactLookup =
  | { status: "found"; record: ImageArtifactRecord }
  | { status: "expired" }
  | { status: "missing" };

const DEFAULT_ARTIFACT_TTL_MS = 10 * 60 * 1000;
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export class ImageArtifactStore {
  private readonly records = new Map<string, ImageArtifactRecord>();
  private readonly ttlMs: number;

  public constructor(options: ImageArtifactStoreOptions = {}) {
    this.ttlMs = Math.max(1, options.ttlMs ?? DEFAULT_ARTIFACT_TTL_MS);
  }

  public register(input: {
    deviceId?: string;
    workspaceRoot?: string;
    path: string;
    mimeType: string;
    bytes?: number;
  }): ImageArtifactRecord {
    this.deleteExpired();

    const id = randomUUID();
    const expiresAt = new Date(Date.now() + this.ttlMs).toISOString();
    const record: ImageArtifactRecord = {
      id,
      path: input.path,
      mimeType: input.mimeType,
      expiresAt,
      ...(input.deviceId ? { deviceId: input.deviceId } : {}),
      ...(input.workspaceRoot ? { workspaceRoot: input.workspaceRoot } : {}),
      ...(typeof input.bytes === "number" ? { bytes: input.bytes } : {}),
    };
    this.records.set(id, record);
    return record;
  }

  public lookup(id: string): ImageArtifactLookup {
    if (!isArtifactUuid(id)) {
      return { status: "missing" };
    }

    const record = this.records.get(id);
    if (record === undefined) {
      return { status: "missing" };
    }

    if (Date.parse(record.expiresAt) <= Date.now()) {
      this.records.delete(id);
      return { status: "expired" };
    }

    return { status: "found", record };
  }

  public deleteExpired(now = Date.now()): void {
    for (const [id, record] of this.records) {
      if (Date.parse(record.expiresAt) <= now) {
        this.records.delete(id);
      }
    }
  }
}

export function isArtifactUuid(value: string): boolean {
  return UUID_PATTERN.test(value);
}
