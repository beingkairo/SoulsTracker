import { OverlayState } from "./overlay-state";
import { authorize, failure, reject, route } from "./protocol";
export { OverlayState };

export interface Env {
  OVERLAYS: DurableObjectNamespace<OverlayState>;
  PROVISIONED_IDS: string[];
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      const { id } = route(request);
      const ids: unknown = env.PROVISIONED_IDS;
      if (!Array.isArray(ids) || ids.length > 16 ||
        !ids.every(value => typeof value === "string" && /^[0-9a-f]{32}$/.test(value)) ||
        !ids.includes(id)) return reject(404, "not_found");
      authorize(request);
      return await env.OVERLAYS.getByName(id).fetch(request);
    } catch (error) { return failure(error); }
  }
} satisfies ExportedHandler<Env>;
