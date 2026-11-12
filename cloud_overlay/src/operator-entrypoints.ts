import { WorkerEntrypoint } from "cloudflare:workers";
import type { Env } from "./index";

// Bindings select a narrow surface; the authority still authenticates every call.
// Credentials are supplied by the operator, never synthesized from binding trust.
export class PolicyReadEntrypoint extends WorkerEntrypoint<Env> {
  async status(credential: unknown) {
    if (!this.env.PROVISIONING_AUTHORITY) throw new Error("unavailable");
    return this.env.PROVISIONING_AUTHORITY.getByName("global").policyStatus(credential);
  }
}

export class PolicyApplyEntrypoint extends WorkerEntrypoint<Env> {
  async apply(credential: unknown, expectedRevision: unknown) {
    if (!this.env.PROVISIONING_AUTHORITY) throw new Error("unavailable");
    return this.env.PROVISIONING_AUTHORITY.getByName("global").applyConfiguredPolicy(credential, expectedRevision);
  }
}
