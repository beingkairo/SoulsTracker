import type { PolicyReadEntrypoint, PolicyApplyEntrypoint } from "../../src/operator-entrypoints";
import { validateHostedJsonTokens } from "../../../web_overlay/src/hosted-contracts";
import { authenticate, once, reply, type GatewaySettings } from "./common";
import { checkedApply, checkedStatus } from "./contract";

export interface GatewayEnv extends GatewaySettings {
  ACCESS_READ_AUDIENCE?: string; ACCESS_READ_OPERATOR?: string; ACCESS_READ_POLICY?: string;
  ACCESS_APPLY_AUDIENCE?: string; ACCESS_APPLY_OPERATOR?: string; ACCESS_APPLY_POLICY?: string;
  POLICY_READ?: Service<PolicyReadEntrypoint>;
  POLICY_APPLY?: Service<PolicyApplyEntrypoint>;
}

async function revision(request: Request): Promise<number> {
  if (request.headers.get("Content-Type") !== "application/json" || !request.body) throw new Error("invalid");
  const reader = request.body.getReader(); const decoder = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });
  let text = "", size = 0;
  try {
    for (;;) {
      const next = await reader.read(); if (next.done) break;
      size += next.value.length;
      if (size > 256) { await reader.cancel(); throw new Error("invalid"); }
      text += decoder.decode(next.value, { stream: true });
    }
    text += decoder.decode();
  } finally { reader.releaseLock(); }
  validateHostedJsonTokens(text);
  const body: unknown = JSON.parse(text);
  if (!body || typeof body !== "object" || Array.isArray(body) || Object.keys(body).length !== 1 ||
      !Object.hasOwn(body, "expectedRevision")) throw new Error("invalid");
  const value = (body as { expectedRevision: unknown }).expectedRevision;
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) throw new Error("invalid");
  return value;
}

export default {
  async fetch(request: Request, env: GatewayEnv): Promise<Response> {
    const path = new URL(request.url).pathname;
    const read = path === "/operator/policy/status" && request.method === "GET";
    const apply = path === "/operator/policy/apply" && request.method === "POST";
    let credential: string; let expected: number | undefined;
    try {
      // Distinct audiences and policies prevent a read JWT being used for apply.
      if ((!read && !apply) || !env.ACCESS_READ_AUDIENCE || !env.ACCESS_APPLY_AUDIENCE ||
          !env.ACCESS_READ_POLICY || !env.ACCESS_APPLY_POLICY || env.ACCESS_READ_AUDIENCE === env.ACCESS_APPLY_AUDIENCE ||
          env.ACCESS_READ_POLICY === env.ACCESS_APPLY_POLICY || (read && (request.body !== null || request.headers.has("Content-Length"))) ||
          (read ? !env.POLICY_READ : !env.POLICY_APPLY)) throw new Error("denied");
      credential = await authenticate(request, env, { audience: (read ? env.ACCESS_READ_AUDIENCE : env.ACCESS_APPLY_AUDIENCE)!,
        operator: (read ? env.ACCESS_READ_OPERATOR : env.ACCESS_APPLY_OPERATOR) ?? "",
        policy: (read ? env.ACCESS_READ_POLICY : env.ACCESS_APPLY_POLICY)! });
      if (apply) expected = await once(revision(request), 3000);
    } catch { return reply(403, { error: "unavailable" }); }
    try {
      // Exactly one RPC per explicit request. Timeout does not cancel that RPC,
      // prove non-commit, retry it, or advance the expected revision.
      const result = read ? checkedStatus(await once(env.POLICY_READ!.status(credential))) :
        checkedApply(await once(env.POLICY_APPLY!.apply(credential, expected)));
      const status = result.ok ? 200 : result.error === "forbidden" ? 403 :
        result.error === "outcome_unknown" || result.error === "read_unavailable" ? 503 : 409;
      return reply(status, result);
    } catch { return reply(503, { ok: false, error: read ? "read_unavailable" : "outcome_unknown" }); }
  }
};
