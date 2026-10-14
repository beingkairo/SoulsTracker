import { env } from "cloudflare:workers";
import { runInDurableObject, reset } from "cloudflare:test";
import { afterEach, expect, it, vi } from "vitest";
import { ProvisioningAuthority } from "../src/provisioning-authority";
import { digest } from "../src/protocol";
import { reconcileAuthority } from "../src/reconciliation";
import worker, { type Env } from "../src/index";
import type {} from "./authority.test";

const legacy = "1".repeat(32);
const stub = () => env.PROVISIONING_AUTHORITY!.getByName("reconciliation");
const configured = { ...env, PROVISIONED_IDS: [legacy], PROVISIONING_CONFIGURATION_VERSION: "3",
  PROVISIONING_CEILING: "100", PROVISIONING_HARD_MAXIMUM: "100" } as Env;
const snapshot = (state: DurableObjectState) => state.storage.sql.exec<{ name: string }>(
  "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name").toArray();
afterEach(() => { vi.restoreAllMocks(); return reset(); });

it("fails closed without writes or disclosure when persisted seeds differ from Worker seeds", async () => {
  await runInDurableObject(stub(), (_, state) => {
    state.storage.sql.exec("CREATE TABLE allocations (request_id TEXT, overlay_id TEXT, phase TEXT, schema_version INTEGER)");
    state.storage.sql.exec("CREATE TABLE authority_configuration (singleton INTEGER, configuration_version INTEGER, ceiling INTEGER, hard_maximum INTEGER, seeds_digest TEXT)");
    const stale = digest(["2".repeat(32)]);
    state.storage.sql.exec("INSERT INTO authority_configuration VALUES(1,3,100,100,?)", stale);
    state.storage.sql.exec("INSERT INTO allocations VALUES('legacy:' || ?, ?, 'active', 1)", legacy, legacy);
    const before = state.storage.sql.exec("SELECT * FROM authority_configuration").toArray();
    const exec = state.storage.sql.exec.bind(state.storage.sql);
    const calls: string[] = [];
    vi.spyOn(state.storage.sql, "exec").mockImplementation(((sql: string, ...args: unknown[]) => {
      calls.push(sql); return exec(sql, ...args);
    }) as typeof state.storage.sql.exec);
    const report = reconcileAuthority(state, configured);
    expect(report.unavailable_reason).toBe("seed_configuration_mismatch");
    expect(report.persisted_configuration_state).toBe("unavailable");
    for (const field of ["persisted_configuration_version", "persisted_ceiling", "persisted_hard_maximum",
      "ledger_active", "ledger_reserved", "ledger_total", "seed_configured_count", "seed_ledger_count",
      "identity_overlap_count", "identity_union_count", "legacy_identity_count"])
      expect(report[field as keyof typeof report]).toBeNull();
    expect(report.consistency_scope).toBe("unavailable");
    expect(calls.every(sql => /^SELECT\s/i.test(sql))).toBe(true);
    expect(state.storage.sql.exec("SELECT * FROM authority_configuration").toArray()).toEqual(before);
    for (const sensitive of [legacy, stale, digest(configured.PROVISIONED_IDS), "seeds_digest"])
      expect(JSON.stringify(report)).not.toContain(sensitive);
  });
});

it.each([null, "not-a-digest", "A".repeat(64), undefined])(
  "rejects malformed or absent persisted seed digest %s with null aggregates", async (persisted) => {
    await runInDurableObject(stub(), (_, state) => {
      state.storage.sql.exec("CREATE TABLE allocations (request_id TEXT, overlay_id TEXT, phase TEXT, schema_version INTEGER)");
      state.storage.sql.exec(`CREATE TABLE authority_configuration (singleton INTEGER, configuration_version INTEGER,
        ceiling INTEGER, hard_maximum INTEGER${persisted === undefined ? "" : ", seeds_digest TEXT"})`);
      if (persisted === undefined) state.storage.sql.exec("INSERT INTO authority_configuration VALUES(1,3,100,100)");
      else state.storage.sql.exec("INSERT INTO authority_configuration VALUES(1,3,100,100,?)", persisted);
      const before = state.storage.sql.exec("SELECT * FROM authority_configuration").toArray();
      const exec = state.storage.sql.exec.bind(state.storage.sql);
      const calls: string[] = [];
      vi.spyOn(state.storage.sql, "exec").mockImplementation(((sql: string, ...args: unknown[]) => {
        calls.push(sql); return exec(sql, ...args);
      }) as typeof state.storage.sql.exec);
      const report = reconcileAuthority(state, configured);
      expect(report.unavailable_reason).toBe("seed_configuration_mismatch");
      expect(report.persisted_configuration_state).toBe("unavailable");
      expect(report.consistency_scope).toBe("unavailable");
      for (const field of ["persisted_configuration_version", "persisted_ceiling", "persisted_hard_maximum",
        "ledger_active", "ledger_reserved", "ledger_total", "seed_configured_count", "seed_ledger_count",
        "identity_overlap_count", "identity_union_count", "legacy_identity_count"])
        expect(report[field as keyof typeof report]).toBeNull();
      expect(calls.every(sql => /^SELECT\s/i.test(sql))).toBe(true);
      expect(state.storage.sql.exec("SELECT * FROM authority_configuration").toArray()).toEqual(before);
      expect(JSON.stringify(report)).not.toContain(legacy);
      if (persisted) expect(JSON.stringify(report)).not.toContain(persisted);
    });
  });

it("denies public and unauthorized reconciliation attempts without touching either namespace", async () => {
  const authority = { getByName: vi.fn() } as unknown as Env["PROVISIONING_AUTHORITY"];
  const overlays = { get: vi.fn(), getByName: vi.fn() } as unknown as Env["OVERLAYS"];
  for (const path of ["/api/v1/reconciliation", "/api/v1/overlays/reconciliation",
    `/api/v1/overlays/${legacy}/reconciliation`]) {
    for (const headers of [new Headers(), new Headers({ Authorization: "Bearer synthetic" })]) {
      const result = await worker.fetch(new Request(`https://overlay.test${path}`, { headers }),
        { ...configured, PROVISIONING_AUTHORITY: authority, OVERLAYS: overlays });
      expect(result.status).toBe(404);
      expect(await result.text()).toBe('{"error":"not_found"}');
    }
  }
  expect(authority!.getByName).not.toHaveBeenCalled();
  expect(overlays.get).not.toHaveBeenCalled();
  expect(overlays.getByName).not.toHaveBeenCalled();
});

it("reports missing authority schema without constructing tables or calling targets", async () => {
  await runInDurableObject(stub(), (_, state) => {
    const exec = state.storage.sql.exec.bind(state.storage.sql);
    const calls: string[] = [];
    vi.spyOn(state.storage.sql, "exec").mockImplementation(((sql: string, ...args: unknown[]) => {
      calls.push(sql);
      return exec(sql, ...args);
    }) as typeof state.storage.sql.exec);
    const before = snapshot(state);
    new ProvisioningAuthority(state, configured);
    const report = reconcileAuthority(state, configured);
    expect(report.persisted_configuration_state).toBe("uninitialized");
    expect(report.ledger_total).toBeNull();
    expect(report.legacy_identity_count).toBeNull();
    expect(report.legacy_identity_unavailable_reason).toBe("not_classifiable");
    expect(snapshot(state)).toEqual(before);
    expect(calls.every(sql => /^SELECT\s/i.test(sql))).toBe(true);
    expect(configured.OVERLAYS).toBeDefined();
  });
});

it("reports only allowlisted aggregates from one authority transaction and never modifies storage", async () => {
  const overlays = { get: vi.fn(), getByName: vi.fn(), newUniqueId: vi.fn(), idFromString: vi.fn() } as unknown as Env["OVERLAYS"];
  await runInDurableObject(stub(), (_, state) => {
    // Synthetic fixture only; the production constructor must remain inert.
    state.storage.sql.exec(`CREATE TABLE allocations (request_id TEXT PRIMARY KEY,
      request_digest TEXT NOT NULL, overlay_id TEXT NOT NULL UNIQUE,
      phase TEXT NOT NULL CHECK (phase IN ('reserved','active')),
      schema_version INTEGER NOT NULL CHECK (schema_version = 1))`);
    state.storage.sql.exec(`CREATE TABLE authority_configuration (singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
      configuration_version INTEGER NOT NULL, ceiling INTEGER NOT NULL,
      hard_maximum INTEGER NOT NULL, seeds_digest TEXT NOT NULL)`);
  });
  await runInDurableObject(stub(), (_, state) => {
    state.storage.sql.exec("INSERT INTO authority_configuration VALUES(1,3,100,100,?)", digest(configured.PROVISIONED_IDS));
    for (const [request, id, phase] of [
      [`legacy:${legacy}`, legacy, "active"], ["a".repeat(64), "b".repeat(64), "active"],
      ["c".repeat(64), "d".repeat(64), "reserved"]])
      state.storage.sql.exec("INSERT INTO allocations VALUES(?,?,? ,?,1)", request, "synthetic", id, phase);
    const before = {
      tables: snapshot(state),
      allocations: state.storage.sql.exec("SELECT * FROM allocations").toArray(),
      configuration: state.storage.sql.exec("SELECT * FROM authority_configuration").toArray()
    };
    const exec = state.storage.sql.exec.bind(state.storage.sql);
    const calls: string[] = [];
    vi.spyOn(state.storage.sql, "exec").mockImplementation(((sql: string, ...args: unknown[]) => {
      calls.push(sql); return exec(sql, ...args);
    }) as typeof state.storage.sql.exec);
    const report = reconcileAuthority(state, { ...configured, OVERLAYS: overlays });
    expect(report).toMatchObject({ persisted_configuration_state: "present", persisted_configuration_version: 3,
      persisted_ceiling: 100, persisted_hard_maximum: 100, ledger_active: 2, ledger_reserved: 1,
      ledger_total: 3, seed_configured_count: 1, seed_ledger_count: 1,
      identity_overlap_count: 1, identity_union_count: 3,
      legacy_identity_count: null, legacy_identity_unavailable_reason: "not_classifiable",
      consistency_scope: "single_authority_transaction;worker_seeds_non_atomic;legacy_objects_unavailable" });
    expect(Object.keys(report).sort()).toEqual([
      "report_schema_version", "observed_start_utc", "observed_end_utc", "consistency_scope",
      "persisted_configuration_state", "persisted_configuration_version", "persisted_ceiling",
      "persisted_hard_maximum", "ledger_active", "ledger_reserved", "ledger_total",
      "seed_configured_count", "seed_ledger_count", "identity_overlap_count", "identity_union_count",
      "legacy_identity_count", "legacy_identity_unavailable_reason", "unavailable_reason"
    ].sort());
    expect(calls.every(sql => /^SELECT\s/i.test(sql))).toBe(true);
    expect(snapshot(state)).toEqual(before.tables);
    expect(state.storage.sql.exec("SELECT * FROM allocations").toArray()).toEqual(before.allocations);
    expect(state.storage.sql.exec("SELECT * FROM authority_configuration").toArray()).toEqual(before.configuration);
    for (const secret of [legacy, "a".repeat(64), "synthetic", "http", "verifier", "digest",
      digest(configured.PROVISIONED_IDS)])
      expect(JSON.stringify(report)).not.toContain(secret);
    expect(Date.parse(report.observed_start_utc)).toBeLessThanOrEqual(Date.parse(report.observed_end_utc));
    expect(overlays.get).not.toHaveBeenCalled();
    expect(overlays.getByName).not.toHaveBeenCalled();
    expect(overlays.newUniqueId).not.toHaveBeenCalled();
    expect(overlays.idFromString).not.toHaveBeenCalled();
  });
});

it("keeps missing singleton unavailable without repair", async () => {
  await runInDurableObject(stub(), (_, state) => {
    state.storage.sql.exec("CREATE TABLE allocations (request_id TEXT, overlay_id TEXT, phase TEXT, schema_version INTEGER)");
    state.storage.sql.exec("CREATE TABLE authority_configuration (singleton INTEGER, configuration_version INTEGER, ceiling INTEGER, hard_maximum INTEGER)");
    const before = snapshot(state);
    const exec = state.storage.sql.exec.bind(state.storage.sql);
    const calls: string[] = [];
    vi.spyOn(state.storage.sql, "exec").mockImplementation(((sql: string, ...args: unknown[]) => {
      calls.push(sql); return exec(sql, ...args);
    }) as typeof state.storage.sql.exec);
    const report = reconcileAuthority(state, configured);
    expect(report.persisted_configuration_state).toBe("uninitialized");
    expect(report.unavailable_reason).toBe("uninitialized");
    expect(report.ledger_total).toBeNull();
    expect(calls.every(sql => /^SELECT\s/i.test(sql))).toBe(true);
    expect(snapshot(state)).toEqual(before);
    expect(state.storage.sql.exec("SELECT * FROM authority_configuration").toArray()).toEqual([]);
  });
});

it("serializes authority reads against intervening mutations while declaring seeds non-atomic", async () => {
  await runInDurableObject(stub(), (_, state) => {
    state.storage.sql.exec("CREATE TABLE allocations (request_id TEXT, overlay_id TEXT, phase TEXT, schema_version INTEGER)");
    state.storage.sql.exec("CREATE TABLE authority_configuration (singleton INTEGER, configuration_version INTEGER, ceiling INTEGER, hard_maximum INTEGER, seeds_digest TEXT)");
    state.storage.sql.exec("INSERT INTO authority_configuration VALUES(1,3,100,100,?)", digest(configured.PROVISIONED_IDS));
    const earlier = reconcileAuthority(state, configured);
    state.storage.sql.exec("INSERT INTO allocations VALUES('pending', ?, 'reserved', 1)", "a".repeat(64));
    const later = reconcileAuthority(state, configured);
    expect(earlier.ledger_total).toBe(0);
    expect(later.ledger_total).toBe(1);
    expect(later.ledger_reserved).toBe(1);
    expect(earlier.identity_union_count).toBe(1);
    expect(later.identity_union_count).toBe(2);
    expect(later.consistency_scope).toContain("worker_seeds_non_atomic");
    expect(Date.parse(earlier.observed_end_utc)).toBeLessThanOrEqual(Date.parse(later.observed_end_utc));
  });
});

it("rejects a malformed seed-origin ledger row instead of reporting partial counts", async () => {
  await runInDurableObject(stub(), (_, state) => {
    state.storage.sql.exec("CREATE TABLE allocations (request_id TEXT, overlay_id TEXT, phase TEXT, schema_version INTEGER)");
    state.storage.sql.exec("CREATE TABLE authority_configuration (singleton INTEGER, configuration_version INTEGER, ceiling INTEGER, hard_maximum INTEGER, seeds_digest TEXT)");
    state.storage.sql.exec("INSERT INTO authority_configuration VALUES(1,3,100,100,?)", digest(configured.PROVISIONED_IDS));
    state.storage.sql.exec("INSERT INTO allocations VALUES('legacy:incorrect', ?, 'active', 1)", legacy);
    const report = reconcileAuthority(state, configured);
    expect(report.unavailable_reason).toBe("invalid_ledger");
    expect(report.ledger_total).toBeNull();
    expect(report.identity_union_count).toBeNull();
  });
});
