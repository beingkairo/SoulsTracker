import { DurableObject } from "cloudflare:workers";
import type { Env } from "./index";
import { digest, provisioning, type Provisioning } from "./protocol";

interface Allocation extends Record<string, string | number> {
  request_id: string;
  request_digest: string;
  overlay_id: string;
  phase: "reserved" | "active";
  schema_version: number;
}

export type ProvisioningResult = { ok: true; overlayId: string } | { ok: false };

function ceiling(value: unknown): number | null {
  if (typeof value !== "string" || !/^[1-9][0-9]{0,9}$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed <= 2_147_483_647 ? parsed : null;
}

function legacySeeds(value: unknown): string[] | null {
  if (!Array.isArray(value) || value.length > 16 ||
    !value.every(id => typeof id === "string" && /^[0-9a-f]{32}$/.test(id)) ||
    new Set(value).size !== value.length) return null;
  return value;
}

export class ProvisioningAuthority extends DurableObject<Env> {
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.storage.sql.exec(`CREATE TABLE IF NOT EXISTS allocations (
      request_id TEXT PRIMARY KEY,
      request_digest TEXT NOT NULL,
      overlay_id TEXT NOT NULL UNIQUE,
      phase TEXT NOT NULL CHECK (phase IN ('reserved','active')),
      schema_version INTEGER NOT NULL CHECK (schema_version = 1)
    )`);
  }

  async provision(value: unknown): Promise<ProvisioningResult> {
    let claim: Provisioning;
    try { claim = provisioning(value); } catch { return { ok: false }; }
    const configuredCeiling = ceiling(this.env.PROVISIONING_CEILING);
    const seeds = legacySeeds(this.env.PROVISIONED_IDS);
    if (configuredCeiling === null || seeds === null) return { ok: false };
    const requestDigest = digest(claim);

    const allocation = this.ctx.storage.transactionSync((): Allocation | null => {
      for (const id of seeds) this.ctx.storage.sql.exec(
        "INSERT OR IGNORE INTO allocations(request_id,request_digest,overlay_id,phase,schema_version) VALUES(?,?,?,'active',1)",
        `legacy:${id}`, `legacy-v1:${id}`, id);
      const existing = this.ctx.storage.sql.exec<Allocation>(
        "SELECT request_id,request_digest,overlay_id,phase,schema_version FROM allocations WHERE request_id=?",
        claim.requestId).toArray()[0];
      if (existing) return existing.request_digest === requestDigest ? existing : null;
      const count = this.ctx.storage.sql.exec<{ count: number }>("SELECT COUNT(*) AS count FROM allocations").toArray()[0]?.count;
      if (!Number.isSafeInteger(count) || count >= configuredCeiling) return null;
      const overlayId = this.env.OVERLAYS.newUniqueId().toString();
      this.ctx.storage.sql.exec(
        "INSERT INTO allocations(request_id,request_digest,overlay_id,phase,schema_version) VALUES(?,?,?,'reserved',1)",
        claim.requestId, requestDigest, overlayId);
      return { request_id: claim.requestId, request_digest: requestDigest, overlay_id: overlayId,
        phase: "reserved", schema_version: 1 };
    });
    if (!allocation) return { ok: false };

    const targetId = this.env.OVERLAYS.idFromString(allocation.overlay_id);
    const initialized = await this.env.OVERLAYS.get(targetId).provisionV2(allocation.overlay_id, claim);
    if (!initialized) return { ok: false };
    this.ctx.storage.transactionSync(() => {
      this.ctx.storage.sql.exec(
        "UPDATE allocations SET phase='active' WHERE request_id=? AND request_digest=? AND overlay_id=?",
        allocation.request_id, allocation.request_digest, allocation.overlay_id);
    });
    return { ok: true, overlayId: allocation.overlay_id };
  }
}