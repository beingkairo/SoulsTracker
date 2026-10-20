import { DurableObject } from "cloudflare:workers";
import type { Env } from "./index";
import { digest, equalVerifier, provisioning, type Provisioning } from "./protocol";
import { applyConfiguredPolicy, reportConfiguredPolicy, reserveNewAllocation } from "./allocation-policy";

interface Allocation extends Record<string, string | number> {
  request_id: string;
  request_digest: string;
  overlay_id: string;
  phase: "reserved" | "active";
  schema_version: number;
}

export type ProvisioningResult = { ok: true; overlayId: string } | { ok: false };

function positiveInteger(value: unknown): number | null {
  if (typeof value !== "string" || !/^[1-9][0-9]{0,15}$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) ? parsed : null;
}

function legacySeeds(value: unknown): string[] | null {
  if (!Array.isArray(value) || value.length > 16 ||
    !value.every(id => typeof id === "string" && /^[0-9a-f]{32}$/.test(id)) ||
    new Set(value).size !== value.length) return null;
  return value;
}

export class ProvisioningAuthority extends DurableObject<Env> {
  #policyAuthorized(value: unknown, permission: "apply" | "read"): boolean {
    const key = permission === "apply" ? this.env.PROVISIONING_POLICY_APPLY_KEY : this.env.PROVISIONING_POLICY_READ_KEY;
    const other = permission === "apply" ? this.env.PROVISIONING_POLICY_READ_KEY : this.env.PROVISIONING_POLICY_APPLY_KEY;
    // A Service Binding/DO binding is not a method-level permission. Missing,
    // malformed and reused keys fail closed before touching storage.
    return typeof key === "string" && typeof value === "string" && key !== other && equalVerifier(key, value);
  }

  async applyConfiguredPolicy(credential: unknown, expectedRevision: unknown) {
    if (!this.#policyAuthorized(credential, "apply")) return { ok: false as const, error: "forbidden" as const };
    try {
      return this.ctx.storage.transactionSync(() => {
        // Policy application cannot initialize authority configuration or seeds.
        // Require the reviewed legacy configuration to be persisted and equal.
        const seeds = legacySeeds(this.env.PROVISIONED_IDS);
        const version = positiveInteger(this.env.PROVISIONING_CONFIGURATION_VERSION);
        const ceiling = positiveInteger(this.env.PROVISIONING_CEILING);
        const maximum = positiveInteger(this.env.PROVISIONING_HARD_MAXIMUM);
        if (!seeds || version === null || ceiling === null || maximum === null || ceiling > maximum)
          return { ok: false as const, error: "authority_configuration_unavailable" as const };
        if (!this.ctx.storage.sql.exec("SELECT name FROM sqlite_master WHERE type='table' AND name='authority_configuration'").toArray().length)
          return { ok: false as const, error: "authority_configuration_unavailable" as const };
        const rows = this.ctx.storage.sql.exec<{ singleton: number; configuration_version: number; ceiling: number;
          hard_maximum: number; seeds_digest: string }>("SELECT * FROM authority_configuration").toArray();
        if (rows.length !== 1 || rows[0].singleton !== 1 || rows[0].configuration_version !== version ||
            rows[0].ceiling !== ceiling || rows[0].hard_maximum !== maximum || rows[0].seeds_digest !== digest(seeds))
          return { ok: false as const, error: "authority_configuration_unavailable" as const };
        return applyConfiguredPolicy(this.ctx.storage.sql, this.env.PROVISIONING_ALLOCATION_POLICY, expectedRevision, Date.now());
      });
    } catch {
      // A thrown storage/transport operation may have committed. Never retry it
      // here and never expose exception text. Read status before operator action.
      return { ok: false as const, error: "outcome_unknown" as const };
    }
  }

  async policyStatus(credential: unknown) {
    if (!this.#policyAuthorized(credential, "read")) return { ok: false as const, error: "forbidden" as const };
    try {
      return { ok: true as const, report: this.ctx.storage.transactionSync(() =>
        reportConfiguredPolicy(this.ctx.storage.sql, this.env.PROVISIONING_ALLOCATION_POLICY, Date.now())) };
    } catch { return { ok: false as const, error: "read_unavailable" as const }; }
  }

  async provision(value: unknown): Promise<ProvisioningResult> {
    // Legacy authority/allocation schema belongs to the provisioning write path.
    this.ctx.storage.sql.exec(`CREATE TABLE IF NOT EXISTS allocations (
      request_id TEXT PRIMARY KEY,
      request_digest TEXT NOT NULL,
      overlay_id TEXT NOT NULL UNIQUE,
      phase TEXT NOT NULL CHECK (phase IN ('reserved','active')),
      schema_version INTEGER NOT NULL CHECK (schema_version = 1)
    )`);
    this.ctx.storage.sql.exec(`CREATE TABLE IF NOT EXISTS authority_configuration (
      singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
      configuration_version INTEGER NOT NULL,
      ceiling INTEGER NOT NULL,
      hard_maximum INTEGER NOT NULL,
      seeds_digest TEXT NOT NULL
    )`);
    let claim: Provisioning;
    try { claim = provisioning(value); } catch { return { ok: false }; }
    const configurationVersion = positiveInteger(this.env.PROVISIONING_CONFIGURATION_VERSION);
    const configuredCeiling = positiveInteger(this.env.PROVISIONING_CEILING);
    const hardMaximum = positiveInteger(this.env.PROVISIONING_HARD_MAXIMUM);
    const seeds = legacySeeds(this.env.PROVISIONED_IDS);
    if (configurationVersion === null || configuredCeiling === null || hardMaximum === null ||
      configuredCeiling > hardMaximum || seeds === null) return { ok: false };
    const requestDigest = digest(claim);
    const seedsDigest = digest(seeds);

    const allocation = this.ctx.storage.transactionSync((): Allocation | null => {
      const configured = this.ctx.storage.sql.exec<{ configuration_version: number; ceiling: number;
        hard_maximum: number; seeds_digest: string }>(
        "SELECT configuration_version,ceiling,hard_maximum,seeds_digest FROM authority_configuration WHERE singleton=1"
      ).toArray()[0];
      if (!configured) {
        this.ctx.storage.sql.exec(
          "INSERT INTO authority_configuration(singleton,configuration_version,ceiling,hard_maximum,seeds_digest) VALUES(1,?,?,?,?)",
          configurationVersion, configuredCeiling, hardMaximum, seedsDigest);
      } else {
        const exact = configured.configuration_version === configurationVersion &&
          configured.ceiling === configuredCeiling && configured.hard_maximum === hardMaximum &&
          configured.seeds_digest === seedsDigest;
        const nextApproved = configurationVersion === configured.configuration_version + 1;
        if (!exact && !nextApproved) return null;
        if (nextApproved) this.ctx.storage.sql.exec(
          "UPDATE authority_configuration SET configuration_version=?,ceiling=?,hard_maximum=?,seeds_digest=? WHERE singleton=1",
          configurationVersion, configuredCeiling, hardMaximum, seedsDigest);
      }
      for (const id of seeds) this.ctx.storage.sql.exec(
        "INSERT OR IGNORE INTO allocations(request_id,request_digest,overlay_id,phase,schema_version) VALUES(?,?,?,'active',1)",
        `legacy:${id}`, `legacy-v1:${id}`, id);
      const existing = this.ctx.storage.sql.exec<Allocation>(
        "SELECT request_id,request_digest,overlay_id,phase,schema_version FROM allocations WHERE request_id=?",
        claim.requestId).toArray()[0];
      if (existing) return existing.request_digest === requestDigest ? existing : null;
      // Exact replay/recovery above does not spend budget and remains available
      // while new allocations are stopped. Existing publisher/read paths never
      // consult this policy. Missing opt-in retains the historical lifetime gate.
      const admission = reserveNewAllocation(this.ctx.storage.sql, this.env.PROVISIONING_ALLOCATION_POLICY, Date.now());
      if (admission === "deny") return null;
      if (admission === "legacy") {
        const count = this.ctx.storage.sql.exec<{ count: number }>("SELECT COUNT(*) AS count FROM allocations").toArray()[0]?.count;
        if (!Number.isSafeInteger(count) || count >= configuredCeiling) return null;
      }
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
