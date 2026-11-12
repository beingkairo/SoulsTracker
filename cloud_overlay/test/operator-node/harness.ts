// SYNTHETIC ONLY. Not imported by any deploy entry or packaging inventory.
import { ProvisioningAuthority as BaseAuthority } from "../../src/provisioning-authority";
import { DiagnosticProvisioningAuthority } from "../../src/diagnostic-authority";
import type { Env } from "../../src/index";
import { digest, verifierV2 } from "../../src/protocol";
export { OverlayState } from "../../src/overlay-state";
export { PolicyReadEntrypoint, PolicyApplyEntrypoint } from "../../src/operator-entrypoints";
export { DiagnosticReadEntrypoint } from "../../src/diagnostic-authority";

export class ProvisioningAuthority extends DiagnosticProvisioningAuthority {
  async fixtureSettings(value: unknown) { await this.ctx.storage.put("fixture_policy", value); }
  async fixtureSeed() {
    this.ctx.storage.sql.exec("CREATE TABLE authority_configuration (singleton INTEGER,configuration_version INTEGER,ceiling INTEGER,hard_maximum INTEGER,seeds_digest TEXT)");
    this.ctx.storage.sql.exec("INSERT INTO authority_configuration VALUES(1,3,100,100,?)", digest(this.env.PROVISIONED_IDS));
    this.ctx.storage.sql.exec("CREATE TABLE allocations (request_id TEXT PRIMARY KEY,request_digest TEXT,overlay_id TEXT UNIQUE,phase TEXT,schema_version INTEGER)");
    for (const id of this.env.PROVISIONED_IDS) this.ctx.storage.sql.exec("INSERT INTO allocations VALUES(?,?,?,'active',1)", `legacy:${id}`, `legacy-v1:${id}`, id);
  }
  async #configured(): Promise<Env> {
    return { ...this.env, PROVISIONING_ALLOCATION_POLICY: await this.ctx.storage.get("fixture_policy") };
  }
  override async applyConfiguredPolicy(credential: unknown, expected: unknown) {
    return new BaseAuthority(this.ctx, await this.#configured()).applyConfiguredPolicy(credential, expected);
  }
  override async policyStatus(credential: unknown) { return new BaseAuthority(this.ctx, await this.#configured()).policyStatus(credential); }
  override async readAuthorityAggregate(credential: unknown) {
    return new DiagnosticProvisioningAuthority(this.ctx, await this.#configured()).readAuthorityAggregate(credential);
  }
  async fixtureClaim(index: number) {
    const requestId = index.toString(16).padStart(64, "0");
    return new BaseAuthority(this.ctx, await this.#configured()).provision({ v: 1, requestId,
      readVerifier: verifierV2(requestId, "read", "a".repeat(64)), writeVerifier: verifierV2(requestId, "write", "b".repeat(64)) });
  }
  async fixtureSnapshot() {
    const tables = this.ctx.storage.sql.exec<{ name: string }>("SELECT name FROM sqlite_master WHERE type='table' AND name IN ('allocations','authority_configuration','allocation_policy') ORDER BY name").toArray();
    return Object.fromEntries(tables.map(({ name }) => [name, this.ctx.storage.sql.exec(`SELECT * FROM ${name}`).toArray()]));
  }
}

// Host test driver only. All other workers bind exclusively to named surfaces.
export default {
  async fetch(request: Request, env: Env) {
    const stub = env.PROVISIONING_AUTHORITY!.getByName("global") as unknown as ProvisioningAuthority;
    const body = request.method === "POST" ? await request.json() as { action: string; value?: unknown } : { action: "snapshot" };
    switch (body.action) {
      case "seed": return Response.json(await stub.fixtureSeed() ?? null);
      case "settings": return Response.json(await stub.fixtureSettings(body.value) ?? null);
      case "claim": return Response.json(await stub.fixtureClaim(body.value as number));
      case "directDiagnostic": return Response.json(await stub.readAuthorityAggregate(body.value));
      case "directApply": return Response.json(await stub.applyConfiguredPolicy(body.value, 0));
      default: return Response.json(await stub.fixtureSnapshot());
    }
  }
};
