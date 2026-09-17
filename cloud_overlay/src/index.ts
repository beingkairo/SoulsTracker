import { OverlayState } from "./overlay-state";
import { authorize, failure, reject, response, route } from "./protocol";
export { OverlayState };

export interface Env {
  OVERLAYS: DurableObjectNamespace<OverlayState>;
  PROVISIONED_IDS: string[];
  BROWSER_ORIGIN?: string;
  BOOTSTRAP?: unknown;
  PUBLISHER_RATE_LIMITER: RateLimit;
  LIVE_RATE_LIMITER: RateLimit;
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      const { id, action } = route(request);
      const ids: unknown = env.PROVISIONED_IDS;
      if (!Array.isArray(ids) || ids.length > 16 ||
        !ids.every(value => typeof value === "string" && /^[0-9a-f]{32}$/.test(value)) ||
        !ids.includes(id)) return reject(404, "not_found");
      if (action === "live") {
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
      const limiter = action === "live" ? env.LIVE_RATE_LIMITER : env.PUBLISHER_RATE_LIMITER;
      let admitted: boolean;
      try {
        admitted = (await limiter.limit({ key: `overlay-v1:${id}` })).success;
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
      return await env.OVERLAYS.getByName(id).fetch(request);
    } catch (error) { return failure(error); }
  }
} satisfies ExportedHandler<Env>;
