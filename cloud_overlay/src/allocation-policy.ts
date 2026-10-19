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

// Must run in the SAME synchronous transaction as the allocation INSERT. A slot
// pays for a durable reservation, not successful initialization or live presence.
export function reserveNewAllocation(sql: SqlStorage, value: unknown, now: number): "legacy" | "admit" | "deny" {
  const exists = sql.exec("SELECT name FROM sqlite_master WHERE type='table' AND name='allocation_policy'").toArray().length !== 0;
  if (!exists && value === undefined) return "legacy";
  const policy = policyValue(value);
  if (!policy || !nonnegative(now) || !Number.isSafeInteger(now + policy.windowSeconds * 1000)) return "deny";
  const policyDigest = digest(policy);
  let count = 0;
  let ends = now + policy.windowSeconds * 1000;
  if (exists) {
    const rows = sql.exec<PolicyState>("SELECT * FROM allocation_policy").toArray();
    // Never repair a missing/corrupt singleton or fall back to the lifetime gate.
    if (rows.length !== 1) return "deny";
    const stored = rows[0];
    if (stored.singleton !== 1 || ![stored.revision, stored.window_ends_ms, stored.window_count,
      stored.last_observed_ms].every(nonnegative) || stored.revision < 1 ||
      typeof stored.policy_digest !== "string" || !/^[0-9a-f]{64}$/.test(stored.policy_digest) ||
      stored.window_ends_ms <= stored.last_observed_ms || now < stored.last_observed_ms) return "deny";
    const exact = stored.revision === policy.revision && stored.policy_digest === policyDigest;
    const next = policy.revision === stored.revision + 1;
    if (!exact && !next) return "deny";
    if (now < stored.window_ends_ms) {
      count = stored.window_count;
      // Policy edits/toggling the stop cannot reset a still-consumed budget.
      ends = exact ? stored.window_ends_ms : Math.max(stored.window_ends_ms, ends);
    }
  } else {
    if (policy.revision !== 1) return "deny";
    sql.exec(`CREATE TABLE allocation_policy (
      singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
      revision INTEGER NOT NULL,
      policy_digest TEXT NOT NULL,
      window_ends_ms INTEGER NOT NULL,
      window_count INTEGER NOT NULL,
      last_observed_ms INTEGER NOT NULL
    )`);
  }
  const admitted = policy.enabled && count < policy.maxNewAllocations;
  sql.exec(`INSERT INTO allocation_policy VALUES(1,?,?,?,?,?) ON CONFLICT(singleton) DO UPDATE SET
    revision=excluded.revision, policy_digest=excluded.policy_digest, window_ends_ms=excluded.window_ends_ms,
    window_count=excluded.window_count, last_observed_ms=excluded.last_observed_ms`,
    policy.revision, policyDigest, ends, count + (admitted ? 1 : 0), now);
  return admitted ? "admit" : "deny";
}
