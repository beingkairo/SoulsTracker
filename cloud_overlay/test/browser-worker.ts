// Local integration harness only. Never referenced by the deployment config.
import worker, { type Env } from "../src/index";
import { OverlayState as ProductionOverlayState } from "../src/overlay-state";
import { verifier } from "../src/protocol";
export class OverlayState extends ProductionOverlayState {
  async fetch(request: Request): Promise<Response> {
    const path = new URL(request.url).pathname;
    if (path === "/__test/provision") {
      for (const socket of this.ctx.getWebSockets()) socket.close(1000);
      const token = () => Array.from(crypto.getRandomValues(new Uint8Array(32)), byte => byte.toString(16).padStart(2, "0")).join("");
      const read = token(), write = token(), id = "1".repeat(32);
      this.ctx.storage.sql.exec("DELETE FROM records");
      this.ctx.storage.sql.exec("INSERT INTO records (key,value) VALUES ('control',?)", JSON.stringify({
        readVerifier: verifier(id, "read", read), writeVerifier: verifier(id, "write", write),
        epoch: "0", generation: "0", readGeneration: "0", session: null, last: null, rotation: null
      }));
      return Response.json({ id, read, write });
    }
    if (path === "/__test/disconnect") {
      for (const socket of this.ctx.getWebSockets()) socket.close(1012, "Local restart simulation");
      return new Response(null, { status: 204 });
    }
    return super.fetch(request);
  }
}
export default {
  fetch(request: Request, env: Env): Promise<Response> {
    if (request.method === "POST" && ["/__test/provision", "/__test/disconnect"].includes(new URL(request.url).pathname))
      return env.OVERLAYS.getByName("1".repeat(32)).fetch(request);
    return worker.fetch(request, env);
  }
};
