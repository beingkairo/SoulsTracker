import { OverlayState } from "./overlay-state";
import { authorize, authorizeSetup, equalVerifier, failure, reject, response, route, verifier } from "./protocol";
export { OverlayState };

export interface Env {
  OVERLAYS: DurableObjectNamespace<OverlayState>;
  PROVISIONED_IDS: string[];
  BROWSER_ORIGIN?: string;
  BOOTSTRAP?: unknown;
  PROVISIONING_SLOTS?: unknown;
  PUBLISHER_RATE_LIMITER: RateLimit;
  LIVE_RATE_LIMITER: RateLimit;
  PROVISIONING_RATE_LIMITER: RateLimit;
}

export interface ProvisioningSlot { v: 1; overlayId: string; setupVerifier: string }
export function provisioningSlots(env: Env, ids: string[]): ProvisioningSlot[] {
  const value = env.PROVISIONING_SLOTS;
  if (!Array.isArray(value) || value.length > 16) return [];
  const slots: ProvisioningSlot[] = [];
  for (const item of value) {
    if (item === null || typeof item !== "object" || Array.isArray(item)) return [];
    const record = item as Record<string, unknown>;
    if (Object.keys(record).length !== 3 || record.v !== 1 || typeof record.overlayId !== "string" ||
      typeof record.setupVerifier !== "string" || !/^[0-9a-f]{32}$/.test(record.overlayId) ||
      !/^[0-9a-f]{64}$/.test(record.setupVerifier) || !ids.includes(record.overlayId) ||
      slots.some(slot => slot.overlayId === record.overlayId)) return [];
    slots.push(record as unknown as ProvisioningSlot);
  }
  return slots;
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      const { id, action } = route(request);
      const ids: unknown = env.PROVISIONED_IDS;
      if (!Array.isArray(ids) || ids.length > 16 ||
        !ids.every(value => typeof value === "string" && /^[0-9a-f]{32}$/.test(value)) ||
        !ids.includes(id)) return reject(404, "not_found");
      let provisioningSlot: ProvisioningSlot | undefined;
      if (action === "provision") {
        provisioningSlot = provisioningSlots(env, ids).find(value => value.overlayId === id);
        if (!provisioningSlot) return reject(404, "not_found");
      } else if (action === "live") {
        if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") return reject(404, "not_found");
        if (!env.BROWSER_ORIGIN || request.headers.get("Origin") !== env.BROWSER_ORIGIN)
          return reject(403, "forbidden");
        let origin: URL;
        try { origin = new URL(env.BROWSER_ORIGIN); } catch { return reject(403, "forbidden"); }
        if (origin.protocol !== "https:" || origin.origin !== env.BROWSER_ORIGIN) return reject(403, "forbidden");
        if (request.headers.has("Cookie") || request.headers.has("Sec-WebSocket-Protocol") || request.headers.has("Authorization"))
          return reject(400, "invalid_protocol");
      } else authorize(request);
      if (!env.BROWSER_ORIGIN || new URL(request.url).origin !== env.BROWSER_ORIGIN)
        return reject(403, "forbidden");
      const limiter = action === "live" ? env.LIVE_RATE_LIMITER :
        action === "provision" ? env.PROVISIONING_RATE_LIMITER : env.PUBLISHER_RATE_LIMITER;
      let admitted: boolean;
      try {
        admitted = (await limiter.limit({ key: action === "provision" ? `overlay-v1:setup:${id}` : `overlay-v1:${id}` })).success;
        if (typeof admitted !== "boolean") throw new Error();
      } catch {
        const unavailable = response(503, { error: "admission_unavailable" });
        unavailable.headers.set("Retry-After", "60");
        return unavailable;
      }
      if (!admitted) {
        const limited = response(429, { error: "rate_limited" });
        limited.headers.set("Retry-After", "60");
        return limited;
      }
      if (action === "provision") {
        const grant = authorizeSetup(request);
        if (!equalVerifier(provisioningSlot!.setupVerifier, verifier(id, "setup", grant))) return reject(403, "forbidden");
        if (request.headers.has("Cookie")) return reject(400, "invalid_protocol");
      }
      return await env.OVERLAYS.getByName(id).fetch(request);
    } catch (error) { return failure(error); }
  }
} satisfies ExportedHandler<Env>;
