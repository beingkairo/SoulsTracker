// Synthetic adversarial binding holder; never packaged or deployed.
import { WorkerEntrypoint } from "cloudflare:workers";
import type { PolicyReadEntrypoint, PolicyApplyEntrypoint } from "../../src/operator-entrypoints";
type ProbeEnv = { READ: Service<PolicyReadEntrypoint>; APPLY: Service<PolicyApplyEntrypoint> };
export class LostApplyEntrypoint extends WorkerEntrypoint<ProbeEnv> {
  async apply(credential: unknown, expected: unknown) {
    await this.env.APPLY.apply(credential, expected);
    throw new Error("synthetic response lost after target commit");
  }
}
export default { async fetch(request: Request, env: ProbeEnv) {
  const body = await request.json() as { apply: boolean; credential?: unknown };
  return Response.json(body.apply ? await env.APPLY.apply(body.credential, 0) : await env.READ.status(body.credential));
} };
