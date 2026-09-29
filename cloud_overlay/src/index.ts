import { isIP } from "node:net";
import { OverlayState } from "./overlay-state";
import { ProvisioningAuthority } from "./provisioning-authority";
import { authorize, failure, reject, response, route } from "./protocol";
export { OverlayState, ProvisioningAuthority };

export interface Env {
  OVERLAYS: DurableObjectNamespace<OverlayState>;
  PROVISIONING_AUTHORITY?: DurableObjectNamespace<ProvisioningAuthority>;
  PROVISIONED_IDS: string[];
  BROWSER_ORIGIN?: string;
  BOOTSTRAP?: unknown;
  PROVISIONING_CEILING?: unknown;
  PUBLISHER_RATE_LIMITER: RateLimit;
  LIVE_RATE_LIMITER: RateLimit;
  CREATE_CLIENT_RATE_LIMITER?: RateLimit;
  CREATE_SERVICE_RATE_LIMITER?: RateLimit;
}

function retry(status: 429 | 503, error: "rate_limited" | "admission_unavailable"): Response {
  const result = response(status, { error });
  result.headers.set("Retry-After", "60");
  return result;
}

async function admission(limiter: RateLimit | undefined, key: string): Promise<"admit" | "deny" | "unavailable"> {
  try {
    const admitted = (await limiter!.limit({ key })).success;
    if (typeof admitted !== "boolean") return "unavailable";
    return admitted ? "admit" : "deny";
  } catch { return "unavailable"; }
}

async function create(request: Request, env: Env, url: URL): Promise<Response> {
  const clientValue = request.headers.get("CF-Connecting-IP");
  const clientKey = clientValue !== null && isIP(clientValue) !== 0 ? clientValue : "invalid";
  const client = await admission(env.CREATE_CLIENT_RATE_LIMITER, `overlay-v2:create:client:${clientKey}`);
  const service = await admission(env.CREATE_SERVICE_RATE_LIMITER, "overlay-v2:create:service");
  if (clientValue === null || isIP(clientValue) === 0 || client === "unavailable" || service === "unavailable")
    return retry(503, "admission_unavailable");
  if (client === "deny" || service === "deny") return retry(429, "rate_limited");

  if (url.protocol !== "https:" || url.origin !== env.BROWSER_ORIGIN) return reject(403, "forbidden");
  if (url.search || url.hash) return reject(404, "not_found");
  if (request.method !== "POST") return reject(405, "method_not_allowed");
  if (request.headers.has("Cookie") || request.headers.has("Authorization")) return reject(400, "invalid_protocol");
  const { provisioning, readBody } = await import("./protocol");
  const claim = provisioning(await readBody(request));
  try {
    const authority = env.PROVISIONING_AUTHORITY;
    if (!authority) return retry(503, "admission_unavailable");
    const result = await authority.getByName("global").provision(claim);
    if (!result.ok) return response(409, { error: "creation_unavailable" });
    return response(200, { v: 1, status: "provisioned", overlayId: result.overlayId });
  } catch { return response(503, { error: "storage_failure" }); }
}

function validateOverlayId(env: Env, id: string): DurableObjectId | null {
  if (id.length === 32) {
    const ids: unknown = env.PROVISIONED_IDS;
    if (!Array.isArray(ids) || ids.length > 16 ||
      !ids.every(value => typeof value === "string" && /^[0-9a-f]{32}$/.test(value)) || !ids.includes(id))
      return reject(404, "not_found");
    return null;
  }
  try { return env.OVERLAYS.idFromString(id); }
  catch { return reject(404, "not_found"); }
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      const url = new URL(request.url);
      if (url.pathname === "/api/v1/overlays") return await create(request, env, url);
      const { id, action } = route(request);
      const targetId = validateOverlayId(env, id);
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
      if (!env.BROWSER_ORIGIN || url.origin !== env.BROWSER_ORIGIN) return reject(403, "forbidden");
      const limiter = action === "live" ? env.LIVE_RATE_LIMITER : env.PUBLISHER_RATE_LIMITER;
      const admitted = await admission(limiter, `overlay-v1:${id}`);
      if (admitted === "unavailable") return retry(503, "admission_unavailable");
      if (admitted === "deny") return retry(429, "rate_limited");
      const stub = targetId === null ? env.OVERLAYS.getByName(id) : env.OVERLAYS.get(targetId);
      return await stub.fetch(request);
    } catch (error) { return failure(error); }
  }
} satisfies ExportedHandler<Env>;