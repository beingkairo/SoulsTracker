import type { Env } from "./index";
import { digest } from "./protocol";

// Local test-worker invocation only. This module is not imported by the Worker
// entry or exposed as a Durable Object RPC. No production operation is authorized.
type Reason = "none" | "uninitialized" | "invalid_configuration" | "seed_configuration_mismatch" | "unsupported_policy" | "invalid_ledger" | "read_failure";
type Report = {
  report_schema_version: 1;
  observed_start_utc: string;
  observed_end_utc: string;
  consistency_scope: "single_authority_transaction;worker_seeds_non_atomic;legacy_objects_unavailable" | "unavailable";
  persisted_configuration_state: "present" | "uninitialized" | "unavailable";
  persisted_configuration_version: number | null;
  persisted_ceiling: number | null;
  persisted_hard_maximum: number | null;
  ledger_active: number | null;
  ledger_reserved: number | null;
  ledger_total: number | null;
  seed_configured_count: number | null;
  seed_ledger_count: number | null;
  identity_overlap_count: number | null;
  identity_union_count: number | null;
  legacy_identity_count: null;
  legacy_identity_unavailable_reason: "not_classifiable";
  unavailable_reason: Reason;
};
const safe = (value: unknown): value is number => Number.isSafeInteger(value) && (value as number) >= 0;

export function reconcileAuthority(state: DurableObjectState, env: Env): Report {
  const start = new Date().toISOString();
  const empty = (reason: Reason, configurationState: Report["persisted_configuration_state"]): Report => ({
    report_schema_version: 1, observed_start_utc: start, observed_end_utc: new Date().toISOString(),
    consistency_scope: "unavailable", persisted_configuration_state: configurationState,
    persisted_configuration_version: null, persisted_ceiling: null, persisted_hard_maximum: null,
    ledger_active: null, ledger_reserved: null, ledger_total: null, seed_configured_count: null,
    seed_ledger_count: null, identity_overlap_count: null, identity_union_count: null,
    legacy_identity_count: null, legacy_identity_unavailable_reason: "not_classifiable", unavailable_reason: reason
  });
  const seeds: unknown = env.PROVISIONED_IDS;
  if (!Array.isArray(seeds) || seeds.length > 16 ||
      !seeds.every(id => typeof id === "string" && /^[0-9a-f]{32}$/.test(id)) ||
      new Set(seeds).size !== seeds.length) return empty("invalid_configuration", "unavailable");
  try {
    // This version-1 local report describes the lifetime policy only. Do not
    // mislabel the retained legacy ceiling as an active windowed-policy cap.
    if (state.storage.sql.exec("SELECT name FROM sqlite_master WHERE type='table' AND name='allocation_policy'").toArray().length)
      return empty("unsupported_policy", "unavailable");
    // Atomic only for this authority object; Worker vars and independent overlay
    // objects do not participate. Synchronous reads cannot interleave DO writes.
    const result = state.storage.transactionSync(() => {
      const tables = state.storage.sql.exec<{ name: string }>(
        "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('allocations','authority_configuration')"
      ).toArray();
      if (tables.length !== 2) return { reason: "uninitialized" as const };
      const configs = state.storage.sql.exec<{ configuration_version: number; ceiling: number; hard_maximum: number; seeds_digest: string | null }>(
        "SELECT * FROM authority_configuration WHERE singleton=1"
      ).toArray();
      if (configs.length !== 1) return { reason: "uninitialized" as const };
      if (typeof configs[0].seeds_digest !== "string" ||
          !/^[0-9a-f]{64}$/.test(configs[0].seeds_digest) ||
          configs[0].seeds_digest !== digest(seeds)) return { reason: "seed_configuration_mismatch" as const };
      const counts = state.storage.sql.exec<{ total: number; active: number; reserved: number; seed_origin: number; invalid: number }>(
        `SELECT COUNT(*) AS total,
          COUNT(*) FILTER (WHERE phase='active') AS active,
          COUNT(*) FILTER (WHERE phase='reserved') AS reserved,
          COUNT(*) FILTER (WHERE request_id = 'legacy:' || overlay_id AND length(overlay_id)=32
            AND overlay_id NOT GLOB '*[^0-9a-f]*') AS seed_origin,
          COUNT(*) FILTER (WHERE schema_version != 1 OR phase NOT IN ('active','reserved')
            OR (length(overlay_id) != 32 AND length(overlay_id) != 64)
            OR overlay_id GLOB '*[^0-9a-f]*'
            OR (substr(request_id,1,7) = 'legacy:' AND
              (request_id != 'legacy:' || overlay_id OR length(overlay_id) != 32))) AS invalid FROM allocations`
      ).one();
      const overlap = seeds.length === 0 ? 0 : state.storage.sql.exec<{ count: number }>(
        `SELECT COUNT(*) AS count FROM allocations WHERE overlay_id IN (${seeds.map(() => "?").join(",")})`,
        ...seeds).one().count;
      return { config: configs[0], counts, overlap };
    });
    if ("reason" in result) return result.reason === "uninitialized"
      ? empty("uninitialized", "uninitialized") : empty("seed_configuration_mismatch", "unavailable");
    const { config, counts, overlap } = result;
    if (![config.configuration_version, config.ceiling, config.hard_maximum, counts.total,
      counts.active, counts.reserved, counts.seed_origin, counts.invalid, overlap].every(safe) ||
      config.configuration_version < 1 || config.ceiling < 1 || config.hard_maximum < config.ceiling ||
      counts.invalid !== 0 || counts.active + counts.reserved !== counts.total ||
      counts.total > config.ceiling || overlap > seeds.length ||
      !safe(counts.total + seeds.length - overlap)) return empty("invalid_ledger", "unavailable");
    return {
      report_schema_version: 1, observed_start_utc: start, observed_end_utc: new Date().toISOString(),
      consistency_scope: "single_authority_transaction;worker_seeds_non_atomic;legacy_objects_unavailable",
      persisted_configuration_state: "present", persisted_configuration_version: config.configuration_version,
      persisted_ceiling: config.ceiling, persisted_hard_maximum: config.hard_maximum,
      ledger_active: counts.active, ledger_reserved: counts.reserved, ledger_total: counts.total,
      seed_configured_count: seeds.length, seed_ledger_count: counts.seed_origin,
      identity_overlap_count: overlap, identity_union_count: counts.total + seeds.length - overlap,
      legacy_identity_count: null, legacy_identity_unavailable_reason: "not_classifiable", unavailable_reason: "none"
    };
  } catch {
    return empty("read_failure", "unavailable");
  }
}
