import { createHash, timingSafeEqual } from "node:crypto";
import { parseHostedOverlay, maximumHostedBytes, validateHostedDecimal, validateHostedJsonTokens,
  normalizeHostedAppearance, normalizeHostedDeath, type HostedAppearance, type HostedDeath } from "../../web_overlay/src/hosted-contracts";

export class Rejection extends Error {
  constructor(readonly status: number, readonly code: string) { super(code); }
}
export function reject(status: number, code: string): never { throw new Rejection(status, code); }
export const digest = (value: unknown): string => createHash("sha256").update(JSON.stringify(value)).digest("hex");
export const verifier = (id: string, role: "read" | "write", capability: string): string =>
  createHash("sha256").update(`overlay-v1:${id}:${role}:${capability}`).digest("hex");
export const verifierV2 = (requestId: string, role: "read" | "write", capability: string): string =>
  createHash("sha256").update(`overlay-v2:${requestId}:${role}:${capability}`).digest("hex");
export function equalVerifier(a: string, b: string): boolean {
  return /^[0-9a-f]{64}$/.test(a) && /^[0-9a-f]{64}$/.test(b) &&
    timingSafeEqual(Buffer.from(a, "hex"), Buffer.from(b, "hex"));
}
export function capability(value: unknown): string {
  if (typeof value !== "string" || !/^[0-9a-f]{64}$/.test(value)) return reject(403, "forbidden");
  return value;
}
export function authorize(request: Request): string {
  const match = /^Bearer ([0-9a-f]{64})$/.exec(request.headers.get("Authorization") ?? "");
  if (!match) return reject(403, "forbidden");
  return match[1];
}

export function response(status: number, body: unknown): Response {
  return Response.json(body, { status, headers: {
    "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer"
  } });
}
export function failure(error: unknown): Response {
  return error instanceof Rejection ? response(error.status, { error: error.code }) : response(500, { error: "storage_failure" });
}
export type Route = "publisher" | "session" | "state" | "credentials" | "live";
export function route(request: Request): { id: string; action: Route } {
  const url = new URL(request.url);
  if (url.protocol !== "https:") return reject(400, "https_required");
  const match = /^\/api\/v1\/overlays\/([0-9a-f]{32}|[0-9a-f]{64})\/(publisher|session|state|credentials|live)$/.exec(url.pathname);
  if (!match || url.search || url.hash) return reject(404, "not_found");
  const action = match[2] as Route;
  const methods = { publisher: "GET", session: "POST", state: "PUT", credentials: "POST", live: "GET" };
  if (request.method !== methods[action]) return reject(405, "method_not_allowed");
  return { id: match[1], action };
}
export const defaultAppearance = parseHostedOverlay(JSON.stringify({
  v: 1, type: "snapshot", death: null, appearance: {
    revision: "0", enabled: true, title: "Total Deaths", fontFamily: "Arial", fontSize: 24,
    textColor: "#F7F6FF", textOpacity: 100, backgroundColor: "#15171B", backgroundOpacity: 0,
    padding: 0, cornerRadius: 0, outlineEnabled: true, outlineColor: "#000000", outlineWidth: 0,
    shadowEnabled: false, shadowColor: "#000000", shadowOffsetX: 2, shadowOffsetY: 2,
    shadowBlur: 4, titleIconMode: "off", iconColor: "#FFFFFF"
  }
})).appearance!;
export const channelStatus = (value: HostedDeath | HostedAppearance | null) => ({
  revision: value?.revision ?? "0", digest: digest(value === null ? null : { ...value, revision: "0" })
});

export function shape(value: unknown, allowed: string[], required = allowed): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value) ||
    Object.keys(value).some(key => !allowed.includes(key)) || required.some(key => !Object.hasOwn(value, key)))
    return reject(400, "invalid_body");
  return value as Record<string, unknown>;
}
export function identity(value: unknown): string {
  if (typeof value !== "string" || !/^[0-9a-f]{32}$/.test(value)) return reject(400, "invalid_body");
  return value;
}
export function increment(value: string): string {
  if (value === "9223372036854775807") return reject(409, "counter_exhausted");
  return (BigInt(value) + 1n).toString();
}
export async function readBody(request: Request): Promise<unknown> {
  if (request.headers.get("Content-Type")?.toLowerCase() !== "application/json") return reject(415, "json_required");
  if (request.headers.has("Content-Encoding")) return reject(415, "encoding_not_supported");
  const reader = request.body?.getReader();
  if (!reader) return reject(400, "invalid_body");
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    for (;;) {
      const next = await reader.read();
      if (next.done) break;
      size += next.value.byteLength;
      if (size > maximumHostedBytes) { await reader.cancel(); return reject(413, "body_too_large"); }
      chunks.push(next.value);
    }
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    const json = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
    const value: unknown = JSON.parse(json);
    validateHostedJsonTokens(json);
    return value;
  } catch (error) {
    if (error instanceof Rejection) throw error;
    return reject(400, "invalid_body");
  } finally { reader.releaseLock(); }
}
export interface Acquisition { v: 1; expectedEpoch: string; sessionRequestId: string }
export function acquisition(value: unknown): Acquisition {
  try {
    const body = shape(value, ["v", "expectedEpoch", "sessionRequestId"]);
    if (body.v !== 1) return reject(400, "invalid_body");
    return { v: 1, expectedEpoch: validateHostedDecimal(body.expectedEpoch), sessionRequestId: identity(body.sessionRequestId) };
  } catch { return reject(400, "invalid_body"); }
}

export interface Provisioning { v: 1; requestId: string; readVerifier: string; writeVerifier: string }
export function provisioning(value: unknown): Provisioning {
  try {
    const body = shape(value, ["v", "requestId", "readVerifier", "writeVerifier"]);
    if (body.v !== 1 || typeof body.readVerifier !== "string" || typeof body.writeVerifier !== "string" ||
      !/^[0-9a-f]{64}$/.test(body.readVerifier) || !/^[0-9a-f]{64}$/.test(body.writeVerifier) ||
      body.readVerifier === body.writeVerifier) return reject(400, "invalid_body");
    if (typeof body.requestId !== "string" || !/^[0-9a-f]{64}$/.test(body.requestId)) return reject(400, "invalid_body");
    return { v: 1, requestId: body.requestId, readVerifier: body.readVerifier, writeVerifier: body.writeVerifier };
  } catch { return reject(400, "invalid_body"); }
}

export interface StateWrite {
  v: 1; epoch: string; sessionRequestId: string; sequence: string;
  death?: HostedDeath; appearance?: HostedAppearance;
}
export interface Rotation { v: 1; rotationId: string; expectedGeneration: string; readCapability?: string; writeCapability?: string }
export function rotation(value: unknown): Rotation {
  try {
    const body = shape(value, ["v", "rotationId", "expectedGeneration", "readCapability", "writeCapability"], ["v", "rotationId", "expectedGeneration"]);
    const read = Object.hasOwn(body, "readCapability"), write = Object.hasOwn(body, "writeCapability");
    if (body.v !== 1 || (!read && !write)) return reject(400, "invalid_body");
    return { v: 1, rotationId: identity(body.rotationId), expectedGeneration: validateHostedDecimal(body.expectedGeneration),
      ...(read ? { readCapability: capability(body.readCapability) } : {}),
      ...(write ? { writeCapability: capability(body.writeCapability) } : {}) };
  } catch { return reject(400, "invalid_body"); }
}
export function stateWrite(value: unknown): StateWrite {
  try {
    const body = shape(value, ["v", "epoch", "sessionRequestId", "sequence", "death", "appearance"],
      ["v", "epoch", "sessionRequestId", "sequence"]);
    const hasDeath = Object.hasOwn(body, "death"), hasAppearance = Object.hasOwn(body, "appearance");
    if (body.v !== 1 || (!hasDeath && !hasAppearance)) return reject(400, "invalid_body");
    const channel = (value: unknown) => {
      if (value === null || typeof value !== "object" || Array.isArray(value) || Object.hasOwn(value, "revision"))
        return reject(400, "invalid_body");
      return { ...value, revision: "0" };
    };
    return { v: 1, epoch: validateHostedDecimal(body.epoch), sessionRequestId: identity(body.sessionRequestId),
      sequence: validateHostedDecimal(body.sequence),
      ...(hasDeath ? { death: normalizeHostedDeath(channel(body.death)) } : {}),
      ...(hasAppearance ? { appearance: normalizeHostedAppearance(channel(body.appearance)) } : {}) };
  } catch { return reject(400, "invalid_body"); }
}
