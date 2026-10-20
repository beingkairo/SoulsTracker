import type { Report } from "../../src/reconciliation";

// Closed aggregate schema adapted from diagnostic candidate 342b784. Preserve
// the accepted authority's explicit refusal to interpret activated policy state.
export function checkedAggregate(value: unknown): Report {
  const fields = ["report_schema_version", "observed_start_utc", "observed_end_utc", "consistency_scope",
    "persisted_configuration_state", "persisted_configuration_version", "persisted_ceiling", "persisted_hard_maximum",
    "ledger_active", "ledger_reserved", "ledger_total", "seed_configured_count", "seed_ledger_count",
    "identity_overlap_count", "identity_union_count", "legacy_identity_count", "legacy_identity_unavailable_reason", "unavailable_reason"];
  const numbers = ["persisted_configuration_version", "persisted_ceiling", "persisted_hard_maximum", "ledger_active",
    "ledger_reserved", "ledger_total", "seed_configured_count", "seed_ledger_count", "identity_overlap_count", "identity_union_count"];
  const reasons = ["none", "uninitialized", "invalid_configuration", "seed_configuration_mismatch", "unsupported_policy", "invalid_ledger", "read_failure"];
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("invalid");
  const r = value as Record<string, unknown>;
  const timestamp = (v: unknown): v is string => typeof v === "string" && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(v) &&
    !Number.isNaN(Date.parse(v)) && new Date(v).toISOString() === v;
  const scope = "single_authority_transaction;worker_seeds_non_atomic;legacy_objects_unavailable";
  if (Object.keys(r).length !== fields.length || fields.some(k => !Object.hasOwn(r, k)) || r.report_schema_version !== 1 ||
      !timestamp(r.observed_start_utc) || !timestamp(r.observed_end_utc) || r.observed_start_utc > r.observed_end_utc ||
      ![scope, "unavailable"].includes(r.consistency_scope as string) ||
      !["present", "uninitialized", "unavailable"].includes(r.persisted_configuration_state as string) ||
      !reasons.includes(r.unavailable_reason as string) || r.legacy_identity_count !== null ||
      r.legacy_identity_unavailable_reason !== "not_classifiable" ||
      numbers.some(k => r[k] !== null && (!Number.isSafeInteger(r[k]) || (r[k] as number) < 0))) throw new Error("invalid");
  if (r.unavailable_reason === "none" ? r.consistency_scope !== scope || r.persisted_configuration_state !== "present" ||
      numbers.some(k => r[k] === null) : r.consistency_scope !== "unavailable" ||
      r.persisted_configuration_state === "present" || numbers.some(k => r[k] !== null)) throw new Error("invalid");
  return Object.fromEntries(fields.map(k => [k, r[k]])) as Report;
}
