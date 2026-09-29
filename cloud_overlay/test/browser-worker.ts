// Local integration harness only. Never referenced by the deployment config.
import worker, { type Env } from "../src/index";
import { OverlayState as ProductionOverlayState } from "../src/overlay-state";
import { verifierV2 } from "../src/protocol";
export class OverlayState extends ProductionOverlayState {
  async fetch(request: Request): Promise<Response> {
    const path = new URL(request.url).pathname;
    if (path === "/__test/disconnect") {
      for (const socket of this.ctx.getWebSockets()) socket.close(1012, "Local restart simulation");
      return new Response(null, { status: 204 });
    }
    return super.fetch(request);
  }
}
const token = () => Array.from(crypto.getRandomValues(new Uint8Array(32)),
  byte => byte.toString(16).padStart(2, "0")).join("");
export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const path = new URL(request.url).pathname;
    if (request.method === "POST" && path === "/__test/provision") {
      const requestId = token(), read = token(), write = token();
      const targetId = env.OVERLAYS.newUniqueId();
      const id = targetId.toString();
      const initialized = await env.OVERLAYS.get(targetId).provisionV2(id, {
        v: 1, requestId, readVerifier: verifierV2(requestId, "read", read),
        writeVerifier: verifierV2(requestId, "write", write)
      });
      if (!initialized) return new Response(null, { status: 500 });
      return Response.json({ id, read, write });
    }
    if (request.method === "POST" && path === "/__test/disconnect") {
      try {
        const forwarded = request.clone();
        const value: unknown = await request.json();
        if (value === null || typeof value !== "object" || Array.isArray(value) ||
          Object.keys(value).length !== 1 || typeof (value as Record<string, unknown>).id !== "string")
          throw new Error();
        const id = env.OVERLAYS.idFromString((value as { id: string }).id);
        return env.OVERLAYS.get(id).fetch(forwarded);
      } catch { return new Response(null, { status: 400 }); }
    }
    return worker.fetch(request, env);
  }
};
