import { digest } from "./protocol";

// Explicit opt-in only. No account budget or Production default is inferred here.
export interface AllocationPolicy {
  v: 1;
  revision: number;
  enabled: boolean;
  windowSeconds: number;
  maxNewAllocations: number;
}

interface PolicyState extends Record<string, number | string> {
  singleton: number;
  revision: number;
  policy_digest: string;
  window_ends_ms: number;
  window_count: number;
  last_observed_ms: number;
}

const nonnegative = (value: unknown): value is number => Number.isSafeInteger(value) && (value as number) >= 0;

function policyValue(value: unknown): AllocationPolicy | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const policy = value as Record<string, unknown>;
  const fields = ["v", "revision", "enabled", "windowSeconds", "maxNewAllocations"];
  if (Object.keys(policy).length !== fields.length || fields.some(key => !Object.hasOwn(policy, key)) ||
      policy.v !== 1 || typeof policy.enabled !== "boolean" ||
      ![policy.revision, policy.windowSeconds, policy.maxNewAllocations].every(v => nonnegative(v) && v > 0) ||
      !Number.isSafeInteger((policy.windowSeconds as number) * 1000)) return null;
  return { v: 1, revision: policy.revision as number, enabled: policy.enabled,
    windowSeconds: policy.windowSeconds as number, maxNewAllocations: policy.maxNewAllocations as number };
}

function storedPolicy(sql: SqlStorage): PolicyState | "absent" | "invalid" {
  const exists = sql.exec("SELECT name FROM sqlite_master WHERE type='table' AND name='allocation_policy'").toArray().length !== 0;
  if (!exists) return "absent";
  const rows = sql.exec<PolicyState>("SELECT * FROM allocation_policy").toArray();
  // Never repair a missing/corrupt singleton or fall back to the lifetime gate.
  if (rows.length !== 1) return "invalid";
  const stored = rows[0];
  if (stored.singleton !== 1 || ![stored.revision, stored.window_ends_ms, stored.window_count,
      stored.last_observed_ms].every(nonnegative) || stored.revision < 1 ||
      typeof stored.policy_digest !== "string" || !/^[0-9a-f]{64}$/.test(stored.policy_digest) ||
      stored.window_ends_ms <= stored.last_observed_ms) return "invalid";
  return stored;
}

const exactPolicy = (stored: PolicyState, policy: AllocationPolicy) =>
  stored.revision === policy.revision && stored.policy_digest === digest(policy);
const validClock = (now: number, stored?: PolicyState) => nonnegative(now) && (!stored || now >= stored.last_observed_ms);

export type PolicyApplication = { ok: true; status: "applied" | "already_applied"; revision: number } |
  { ok: false; error: "invalid_configuration" | "invalid_state" | "revision_conflict" | "invalid_clock" };

// Caller authenticates and wraps this in a synchronous authority transaction.
// No allocations, seeds, namespace calls or target initialization occur here.
export function applyConfiguredPolicy(sql: SqlStorage, value: unknown, expectedRevision: unknown, now: number): PolicyApplication {
  const policy = policyValue(value);
  if (!policy) return { ok: false, error: "invalid_configuration" };
  const stored = storedPolicy(sql);
  if (stored === "invalid") return { ok: false, error: "invalid_state" };
  if (!nonnegative(expectedRevision) || policy.revision !== expectedRevision + 1)
    return { ok: false, error: "revision_conflict" };
  if (!validClock(now, stored === "absent" ? undefined : stored) || !Number.isSafeInteger(now + policy.windowSeconds * 1000))
    return { ok: false, error: "invalid_clock" };
  if (stored !== "absent" && exactPolicy(stored, policy))
    return { ok: true, status: "already_applied", revision: policy.revision };
  if ((stored === "absent" ? 0 : stored.revision) !== expectedRevision)
    return { ok: false, error: "revision_conflict" };
  const unexpired = stored !== "absent" && now < stored.window_ends_ms;
  const count = unexpired ? stored.window_count : 0;
  // Changes cannot refund a live budget or shorten its deadline.
  const ends = unexpired ? Math.max(stored.window_ends_ms, now + policy.windowSeconds * 1000) : now + policy.windowSeconds * 1000;
  if (stored === "absent") sql.exec(`CREATE TABLE allocation_policy (
      singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
      revision INTEGER NOT NULL,
      policy_digest TEXT NOT NULL,
      window_ends_ms INTEGER NOT NULL,
      window_count INTEGER NOT NULL,
      last_observed_ms INTEGER NOT NULL
    )`);
  sql.exec(`INSERT INTO allocation_policy VALUES(1,?,?,?,?,?) ON CONFLICT(singleton) DO UPDATE SET
    revision=excluded.revision, policy_digest=excluded.policy_digest, window_ends_ms=excluded.window_ends_ms,
    window_count=excluded.window_count, last_observed_ms=excluded.last_observed_ms`,
    policy.revision, digest(policy), ends, count, now);
  return { ok: true, status: "applied", revision: policy.revision };
}

// Must run in the SAME synchronous transaction as the allocation INSERT. Public
// claims only consume an exactly applied policy; they never apply configuration.
export function reserveNewAllocation(sql: SqlStorage, value: unknown, now: number): "legacy" | "admit" | "deny" {
  const stored = storedPolicy(sql);
  if (stored === "absent" && value === undefined) return "legacy";
  const policy = policyValue(value);
  if (!policy || typeof stored === "string" || !exactPolicy(stored, policy) || !validClock(now, stored) ||
      !Number.isSafeInteger(now + policy.windowSeconds * 1000)) return "deny";
  const count = now < stored.window_ends_ms ? stored.window_count : 0;
  const ends = now < stored.window_ends_ms ? stored.window_ends_ms : now + policy.windowSeconds * 1000;
  const admitted = policy.enabled && count < policy.maxNewAllocations;
  sql.exec("UPDATE allocation_policy SET window_ends_ms=?,window_count=?,last_observed_ms=? WHERE singleton=1",
    ends, count + (admitted ? 1 : 0), now);
  return admitted ? "admit" : "deny";
}

export interface PolicyReport {
  report_schema_version: 1;
  consistency_scope: "single_authority_transaction;worker_configuration_non_atomic";
  activation_state: "never_activated" | "active" | "unavailable";
  persisted_revision: number | null;
  configured_policy_state: "absent" | "valid" | "invalid";
  configured_policy_equal: boolean | null;
  configured_enabled: boolean | null;
  window_count: number | null;
  window_ends_ms: number | null;
  last_observed_ms: number | null;
  observed_ms: number | null;
  clock_valid: boolean | null;
  window_expired: boolean | null;
  new_allocation_state: "legacy" | "enabled" | "stopped" | "blocked" | "unavailable";
  unavailable_reason: "none" | "invalid_state" | "read_failure";
  persisted_enabled: null;
  persisted_limits: null;
  persisted_fields_unavailable_reason: "not_stored;only_equality_is_available";
}

// SELECT only, including on never-used storage. No clock/window refresh or repair.
export function reportConfiguredPolicy(sql: SqlStorage, value: unknown, now: number): PolicyReport {
  const policy = policyValue(value);
  const report: PolicyReport = {
    report_schema_version: 1, consistency_scope: "single_authority_transaction;worker_configuration_non_atomic",
    activation_state: "unavailable", persisted_revision: null,
    configured_policy_state: value === undefined ? "absent" : policy ? "valid" : "invalid",
    configured_policy_equal: null, configured_enabled: policy?.enabled ?? null,
    window_count: null, window_ends_ms: null, last_observed_ms: null,
    observed_ms: nonnegative(now) ? now : null, clock_valid: null, window_expired: null,
    new_allocation_state: "unavailable", unavailable_reason: "none",
    persisted_enabled: null, persisted_limits: null,
    persisted_fields_unavailable_reason: "not_stored;only_equality_is_available"
  };
  try {
    const stored = storedPolicy(sql);
    if (stored === "invalid") return { ...report, unavailable_reason: "invalid_state" };
    if (stored === "absent") return { ...report, activation_state: "never_activated",
      clock_valid: validClock(now) && (!policy || Number.isSafeInteger(now + policy.windowSeconds * 1000)),
      new_allocation_state: value === undefined ? "legacy" : "blocked" };
    const equal = policy ? exactPolicy(stored, policy) : false;
    const clock = validClock(now, stored) && (!policy || Number.isSafeInteger(now + policy.windowSeconds * 1000));
    return { ...report, activation_state: "active", persisted_revision: stored.revision,
      configured_policy_equal: equal, window_count: stored.window_count, window_ends_ms: stored.window_ends_ms,
      last_observed_ms: stored.last_observed_ms, clock_valid: clock,
      window_expired: clock ? now >= stored.window_ends_ms : null,
      new_allocation_state: !equal || !clock ? "blocked" : !policy!.enabled ? "stopped" :
        now < stored.window_ends_ms && stored.window_count >= policy!.maxNewAllocations ? "blocked" : "enabled" };
  } catch { return { ...report, unavailable_reason: "read_failure" }; }
}
