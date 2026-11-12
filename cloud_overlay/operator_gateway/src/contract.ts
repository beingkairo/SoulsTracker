import type { PolicyReport } from "../../src/allocation-policy";

const record = (v: unknown): Record<string, unknown> => {
  if (!v || typeof v !== "object" || Array.isArray(v)) throw new Error("invalid");
  return v as Record<string, unknown>;
};
const exact = (v: Record<string, unknown>, keys: string[]) => {
  if (Object.keys(v).length !== keys.length || keys.some(k => !Object.hasOwn(v, k))) throw new Error("invalid");
};
const safe = (v: unknown) => typeof v === "number" && Number.isSafeInteger(v) && v >= 0;

export function checkedStatus(value: unknown) {
  const result = record(value);
  if (result.ok === false) {
    exact(result, ["ok", "error"]);
    if (!["forbidden", "read_unavailable"].includes(result.error as string)) throw new Error("invalid");
    return { ok: false as const, error: result.error as "forbidden" | "read_unavailable" };
  }
  exact(result, ["ok", "report"]);
  if (result.ok !== true) throw new Error("invalid");
  const report = record(result.report);
  const numeric = ["persisted_revision", "window_count", "window_ends_ms", "last_observed_ms", "observed_ms"];
  const nullableBoolean = ["configured_policy_equal", "configured_enabled", "clock_valid", "window_expired"];
  const enums: Record<string, string[]> = {
    activation_state: ["never_activated", "active", "unavailable"],
    configured_policy_state: ["absent", "valid", "invalid"],
    new_allocation_state: ["legacy", "enabled", "stopped", "blocked", "unavailable"],
    unavailable_reason: ["none", "invalid_state", "read_failure"]
  };
  const fields = ["report_schema_version", "consistency_scope", ...numeric, ...nullableBoolean, ...Object.keys(enums),
    "persisted_enabled", "persisted_limits", "persisted_fields_unavailable_reason"];
  exact(report, fields);
  if (report.report_schema_version !== 1 ||
      report.consistency_scope !== "single_authority_transaction;worker_configuration_non_atomic" ||
      numeric.some(k => report[k] !== null && !safe(report[k])) ||
      nullableBoolean.some(k => report[k] !== null && typeof report[k] !== "boolean") ||
      Object.entries(enums).some(([k, allowed]) => !allowed.includes(report[k] as string)) ||
      report.persisted_enabled !== null || report.persisted_limits !== null ||
      report.persisted_fields_unavailable_reason !== "not_stored;only_equality_is_available") throw new Error("invalid");
  return { ok: true as const, report: Object.fromEntries(fields.map(k => [k, report[k]])) as unknown as PolicyReport };
}

export function checkedApply(value: unknown) {
  const result = record(value);
  if (result.ok === true) {
    exact(result, ["ok", "status", "revision"]);
    if (!["applied", "already_applied"].includes(result.status as string) || !safe(result.revision) || result.revision === 0)
      throw new Error("invalid");
    return { ok: true as const, status: result.status as "applied" | "already_applied", revision: result.revision as number };
  }
  exact(result, ["ok", "error"]);
  const errors = ["forbidden", "authority_configuration_unavailable", "invalid_configuration", "invalid_state",
    "revision_conflict", "invalid_clock", "outcome_unknown"];
  if (result.ok !== false || !errors.includes(result.error as string)) throw new Error("invalid");
  return { ok: false as const, error: result.error as string };
}
