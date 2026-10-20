import type { DiagnosticReadEntrypoint } from "../../src/diagnostic-authority";
import { authenticate, once, reply, type GatewaySettings } from "./common";
import { checkedAggregate } from "./diagnostic-contract";

export interface DiagnosticGatewayEnv extends GatewaySettings {
  ACCESS_DIAGNOSTIC_AUDIENCE?: string; ACCESS_DIAGNOSTIC_OPERATOR?: string; ACCESS_DIAGNOSTIC_POLICY?: string;
  DIAGNOSTIC_READ?: Service<DiagnosticReadEntrypoint>;
}
export default {
  async fetch(request: Request, env: DiagnosticGatewayEnv): Promise<Response> {
    let credential: string;
    try {
      if (new URL(request.url).pathname !== "/operator/reconcile" || request.method !== "GET" ||
          request.body !== null || request.headers.has("Content-Length") || !env.DIAGNOSTIC_READ) throw new Error("denied");
      credential = await authenticate(request, env, { audience: env.ACCESS_DIAGNOSTIC_AUDIENCE ?? "",
        operator: env.ACCESS_DIAGNOSTIC_OPERATOR ?? "", policy: env.ACCESS_DIAGNOSTIC_POLICY ?? "" });
    } catch { return reply(403, { error: "unavailable" }); }
    try {
      const result: unknown = await once(env.DIAGNOSTIC_READ!.aggregate(credential));
      if (!result || typeof result !== "object" || !("ok" in result) || result.ok !== true)
        return reply(403, { error: "unavailable" });
      if (Object.keys(result).length !== 2 || !("report" in result)) throw new Error("invalid");
      return reply(200, { ok: true, report: checkedAggregate(result.report) });
    } catch { return reply(503, { error: "unavailable" }); }
  }
};
