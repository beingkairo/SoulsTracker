import { WorkerEntrypoint } from "cloudflare:workers";
import type { Env } from "./index";
import { ProvisioningAuthority } from "./provisioning-authority";
import { equalVerifier } from "./protocol";
import { reconcileAuthority } from "./reconciliation";

// Exported under the EXISTING ProvisioningAuthority class name only by the
// optional diagnostic module. Removing that module removes this RPC entirely.
export class DiagnosticProvisioningAuthority extends ProvisioningAuthority {
  async readAuthorityAggregate(credential: unknown) {
    const key = this.env.PROVISIONING_DIAGNOSTIC_READ_KEY;
    if (this.env.PROVISIONING_DIAGNOSTIC_ENABLED !== "true" || typeof key !== "string" ||
        typeof credential !== "string" || key === this.env.PROVISIONING_POLICY_READ_KEY ||
        key === this.env.PROVISIONING_POLICY_APPLY_KEY || !equalVerifier(key, credential))
      return { ok: false as const, error: "forbidden" as const };
    return { ok: true as const, report: reconcileAuthority(this.ctx, this.env) };
  }
}

export class DiagnosticReadEntrypoint extends WorkerEntrypoint<Env> {
  async aggregate(credential: unknown) {
    if (!this.env.PROVISIONING_AUTHORITY) throw new Error("unavailable");
    const authority = this.env.PROVISIONING_AUTHORITY as unknown as DurableObjectNamespace<DiagnosticProvisioningAuthority>;
    return authority.getByName("global").readAuthorityAggregate(credential);
  }
}
