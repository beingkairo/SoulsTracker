import { env } from "cloudflare:workers";
import { abortAllDurableObjects, listDurableObjectIds, reset, runInDurableObject } from "cloudflare:test";
import { afterEach, expect, it, vi } from "vitest";
import worker, { type Env } from "../src/index";
import { ProvisioningAuthority } from "../src/provisioning-authority";
import { verifierV2 } from "../src/protocol";
import { reconcileAuthority } from "../src/reconciliation";
import type { AllocationPolicy } from "../src/allocation-policy";
import type {} from "./authority.test";

const seed = "1".repeat(32), read = "a".repeat(64), write = "b".repeat(64);
const start = 1_000_000;
const applyKey = "d".repeat(64), readKey = "e".repeat(64);
const policy: AllocationPolicy = { v: 1, revision: 1, enabled: true, windowSeconds: 10, maxNewAllocations: 2 };
const allowed = { limit: async () => ({ success: true }) } as RateLimit;
const settings = (value: unknown = policy): Env => ({ ...env, PROVISIONED_IDS: [seed],
  BROWSER_ORIGIN: "https://overlay.test", PROVISIONING_CONFIGURATION_VERSION: "3",
  PROVISIONING_CEILING: "100", PROVISIONING_HARD_MAXIMUM: "100", PROVISIONING_ALLOCATION_POLICY: value,
  PROVISIONING_POLICY_APPLY_KEY: applyKey, PROVISIONING_POLICY_READ_KEY: readKey,
  CREATE_CLIENT_RATE_LIMITER: allowed, CREATE_SERVICE_RATE_LIMITER: allowed,
  PUBLISHER_RATE_LIMITER: allowed, LIVE_RATE_LIMITER: allowed });
const claim = (index: number) => {
  const requestId = index.toString(16).padStart(64, "0");
  return { v: 1 as const, requestId, readVerifier: verifierV2(requestId, "read", read),
    writeVerifier: verifierV2(requestId, "write", write) };
};
const stub = () => env.PROVISIONING_AUTHORITY!.getByName("global");
async function provision(index: number, config = settings(), now = start) {
  return runInDurableObject(stub(), async (_, state) => {
    const clock = vi.spyOn(Date, "now").mockReturnValue(now);
    try { return await new ProvisioningAuthority(state, config).provision(claim(index)); }
    finally { clock.mockRestore(); }
  });
}
async function apply(config = settings(), expected = 0, now = start) {
  // Establish only the retained legacy configuration through a denied public
  // claim. The admin operation itself must not bootstrap it or insert seeds.
  await provision(0, { ...config, PROVISIONING_ALLOCATION_POLICY: null }, now);
  return runInDurableObject(stub(), async (_, state) => {
    const clock = vi.spyOn(Date, "now").mockReturnValue(now);
    try { return await new ProvisioningAuthority(state, config).applyConfiguredPolicy(applyKey, expected); }
    finally { clock.mockRestore(); }
  });
}
const allocations = () => runInDurableObject(stub(), (_, state) =>
  state.storage.sql.exec("SELECT * FROM allocations ORDER BY request_id").toArray());
const budget = () => runInDurableObject(stub(), (_, state) =>
  state.storage.sql.exec("SELECT * FROM allocation_policy").toArray());
const targetRows = (id: string) => runInDurableObject(env.OVERLAYS.get(env.OVERLAYS.idFromString(id)), (_, state) =>
  state.storage.sql.exec("SELECT * FROM records ORDER BY key").toArray());
async function admin(config = settings(), expected: unknown = 0, now = start, credential: unknown = applyKey) {
  return runInDurableObject(stub(), async (_, state) => {
    const clock = vi.spyOn(Date, "now").mockReturnValue(now);
    try { return await new ProvisioningAuthority(state, config).applyConfiguredPolicy(credential, expected); }
    finally { clock.mockRestore(); }
  });
}
async function status(config = settings(), now = start, credential: unknown = readKey) {
  return runInDurableObject(stub(), async (_, state) => {
    const clock = vi.spyOn(Date, "now").mockReturnValue(now);
    try { return await new ProvisioningAuthority(state, config).policyStatus(credential); }
    finally { clock.mockRestore(); }
  });
}
afterEach(() => { vi.restoreAllMocks(); return reset(); });

it("checks separate apply/read credentials before any storage access, including shared or absent configuration", async () => {
  await runInDurableObject(stub(), async (_, state) => {
    const exec = vi.spyOn(state.storage.sql, "exec");
    try {
      for (const credential of [undefined, null, "", read, write, readKey, {}, applyKey.toUpperCase()])
        expect(await new ProvisioningAuthority(state, settings()).applyConfiguredPolicy(credential, 0)).toEqual({ ok: false, error: "forbidden" });
      for (const credential of [undefined, null, "", read, write, applyKey, {}])
        expect(await new ProvisioningAuthority(state, settings()).policyStatus(credential)).toEqual({ ok: false, error: "forbidden" });
      for (const config of [
        { ...settings(), PROVISIONING_POLICY_APPLY_KEY: undefined, PROVISIONING_POLICY_READ_KEY: undefined },
        { ...settings(), PROVISIONING_POLICY_APPLY_KEY: readKey }
      ]) {
        const authority = new ProvisioningAuthority(state, config);
        expect(await authority.applyConfiguredPolicy(readKey, 0)).toEqual({ ok: false, error: "forbidden" });
        expect(await authority.policyStatus(readKey)).toEqual({ ok: false, error: "forbidden" });
      }
      expect(exec).not.toHaveBeenCalled();
    } finally { exec.mockRestore(); }
  });
});

it("reports never-activated and missing authority state without bootstrapping storage", async () => {
  expect(await admin()).toEqual({ ok: false, error: "authority_configuration_unavailable" });
  expect(await status()).toMatchObject({ ok: true, report: { activation_state: "never_activated",
    persisted_revision: null, configured_policy_equal: null, window_count: null, clock_valid: true,
    new_allocation_state: "blocked", persisted_enabled: null, persisted_limits: null } });
  const absent = settings(); delete absent.PROVISIONING_ALLOCATION_POLICY;
  expect(await status(absent)).toMatchObject({ report: { new_allocation_state: "legacy", configured_policy_state: "absent" } });
  await runInDurableObject(stub(), (_, state) => {
    expect(state.storage.sql.exec("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE '_cf_%'").toArray()).toEqual([]);
  });
});

it("never initializes or advances policy through public claims and keeps configured mismatch fail-closed", async () => {
  expect((await provision(1)).ok).toBe(false);
  await runInDurableObject(stub(), (_, state) => {
    expect(state.storage.sql.exec("SELECT name FROM sqlite_master WHERE name='allocation_policy'").toArray()).toEqual([]);
  });
  await apply();
  const before = await budget();
  const next = settings({ ...policy, revision: 2, enabled: false });
  for (let i = 1; i <= 5; i++) expect((await provision(i, next)).ok).toBe(false);
  expect(await budget()).toEqual(before);
  expect(await status(next)).toMatchObject({ report: { persisted_revision: 1, configured_policy_equal: false,
    configured_enabled: false, new_allocation_state: "blocked" } });
  expect(await admin(next, 1)).toEqual({ ok: true, status: "applied", revision: 2 });
  expect(await status(next)).toMatchObject({ report: { persisted_revision: 2, configured_policy_equal: true,
    activation_state: "active", new_allocation_state: "stopped" } });
});

it("enforces consecutive expected revisions and makes repeated application a zero-write no-op", async () => {
  await apply(); await provision(1);
  const before = await budget();
  for (const [config, expected] of [
    [settings(), 1], [settings({ ...policy, revision: 3 }), 1],
    [settings({ ...policy, maxNewAllocations: 3 }), 0], [settings({ ...policy, revision: 2 }), 0],
    [settings(), null], [settings(), "0"], [settings(), -1]
  ] as const) expect(await admin(config, expected)).toEqual({ ok: false, error: "revision_conflict" });
  expect(await runInDurableObject(stub(), (_, state) =>
    new ProvisioningAuthority(state, settings()).applyConfiguredPolicy(applyKey, undefined)))
    .toEqual({ ok: false, error: "revision_conflict" });
  expect(await admin(settings(), 0, start + 500)).toEqual({ ok: true, status: "already_applied", revision: 1 });
  expect(await budget()).toEqual(before);
});

it("serializes conflicting applications and concurrent claims without resetting consumption", async () => {
  await apply(); await provision(1);
  const result = await runInDurableObject(stub(), async (_, state) => {
    const clock = vi.spyOn(Date, "now").mockReturnValue(start + 100);
    try {
      const a = new ProvisioningAuthority(state, settings({ ...policy, revision: 2, maxNewAllocations: 1 }));
      const b = new ProvisioningAuthority(state, settings({ ...policy, revision: 2, maxNewAllocations: 5 }));
      return Promise.all([a.applyConfiguredPolicy(applyKey, 1), b.applyConfiguredPolicy(applyKey, 1), a.provision(claim(2))]);
    } finally { clock.mockRestore(); }
  });
  expect(result).toEqual([{ ok: true, status: "applied", revision: 2 }, { ok: false, error: "revision_conflict" }, { ok: false }]);
  expect(await budget()).toMatchObject([{ revision: 2, window_count: 1, window_ends_ms: start + 10_100 }]);
});

it("applies and reads with zero identity-allocation or target side effects; read uses SELECT only", async () => {
  await provision(0, settings(null));
  const ledger = await allocations();
  const ids = (await listDurableObjectIds(env.OVERLAYS)).map(id => id.toString());
  const newUniqueId = vi.fn(), get = vi.fn(), getByName = vi.fn(), idFromString = vi.fn();
  const config = { ...settings(), OVERLAYS: { newUniqueId, get, getByName, idFromString } as unknown as Env["OVERLAYS"] };
  expect((await admin(config)).ok).toBe(true);
  const before = await budget();
  await runInDurableObject(stub(), async (_, state) => {
    const original = state.storage.sql.exec.bind(state.storage.sql);
    const statements: string[] = [];
    const exec = vi.spyOn(state.storage.sql, "exec").mockImplementation(((sql: string, ...args: unknown[]) => {
      statements.push(sql); return original(sql, ...args);
    }) as typeof state.storage.sql.exec);
    try {
      const result = await new ProvisioningAuthority(state, config).policyStatus(readKey);
      expect(result).toMatchObject({ ok: true, report: { persisted_revision: 1, configured_policy_equal: true } });
      const serialized = JSON.stringify(result);
      for (const secret of [applyKey, readKey, read, write, seed, "policy_digest", "seeds_digest"])
        expect(serialized).not.toContain(secret);
      expect(statements.length).toBeGreaterThan(0);
      expect(statements.every(sql => /^SELECT\b/.test(sql))).toBe(true);
    } finally { exec.mockRestore(); }
  });
  expect(await budget()).toEqual(before);
  expect(await allocations()).toEqual(ledger);
  expect((await listDurableObjectIds(env.OVERLAYS)).map(id => id.toString())).toEqual(ids);
  for (const spy of [newUniqueId, get, getByName, idFromString]) expect(spy).not.toHaveBeenCalled();
});

it.each(["DELETE FROM allocation_policy", "UPDATE allocation_policy SET revision=0",
  "UPDATE allocation_policy SET window_count=-1", "UPDATE allocation_policy SET policy_digest='bad'",
  "UPDATE allocation_policy SET last_observed_ms=window_ends_ms"])(
  "reports and rejects invalid persisted state without repair: %s", async sql => {
    await apply();
    await runInDurableObject(stub(), (_, state) => state.storage.sql.exec(sql));
    const before = await budget();
    expect(await admin(settings({ ...policy, revision: 2 }), 1)).toEqual({ ok: false, error: "invalid_state" });
    expect(await status()).toMatchObject({ report: { activation_state: "unavailable", persisted_revision: null,
      window_count: null, configured_policy_equal: null, unavailable_reason: "invalid_state" } });
    expect(await budget()).toEqual(before);
  });

it("reports invalid configuration and regressed/unsafe clock without writes, retaining exact recovery", async () => {
  await apply(); const existing = await provision(1);
  const before = await budget();
  expect(await status(settings(null))).toMatchObject({ report: { configured_policy_state: "invalid", configured_policy_equal: false } });
  expect(await admin(settings(null))).toEqual({ ok: false, error: "invalid_configuration" });
  for (const now of [start - 1, -1, Number.NaN, Number.MAX_SAFE_INTEGER]) {
    expect(await admin(settings({ ...policy, revision: 2 }), 1, now)).toEqual({ ok: false, error: "invalid_clock" });
    expect(await status(settings(), now)).toMatchObject({ report: { clock_valid: false, new_allocation_state: "blocked", window_expired: null } });
    expect(await provision(1, settings(), now)).toEqual(existing);
  }
  expect(await budget()).toEqual(before);
  expect(await status(settings(), start + 10_000)).toMatchObject({ report: { clock_valid: true, window_expired: true, window_count: 1 } });
  expect(await budget()).toEqual(before); // read does not roll an expired window
});

it("does not retry an ambiguous committed apply; separate readback resolves the lost response", async () => {
  await provision(0, settings(null));
  await runInDurableObject(stub(), async (_, state) => {
    const original = state.storage.transactionSync.bind(state.storage);
    const transaction = vi.spyOn(state.storage, "transactionSync").mockImplementation((callback: () => unknown) => {
      original(callback);
      throw new Error("synthetic response timeout after commit");
    });
    try {
      expect(await new ProvisioningAuthority(state, settings()).applyConfiguredPolicy(applyKey, 0))
        .toEqual({ ok: false, error: "outcome_unknown" });
      expect(transaction).toHaveBeenCalledOnce();
    } finally { transaction.mockRestore(); }
  });
  expect(await status()).toMatchObject({ report: { persisted_revision: 1, configured_policy_equal: true } });
  expect(await allocations()).toHaveLength(1);
  expect(await budget()).toMatchObject([{ revision: 1, window_count: 0 }]);
});

it("reports storage read failures as unavailable without error details or repair", async () => {
  await apply(); const before = await budget();
  await runInDurableObject(stub(), async (_, state) => {
    const original = state.storage.sql.exec.bind(state.storage.sql);
    const exec = vi.spyOn(state.storage.sql, "exec").mockImplementation(((sql: string, ...args: unknown[]) => {
      if (sql === "SELECT * FROM allocation_policy") throw new Error(`synthetic private failure ${readKey}`);
      return original(sql, ...args);
    }) as typeof state.storage.sql.exec);
    try {
      const result = await new ProvisioningAuthority(state, settings()).policyStatus(readKey);
      expect(result).toMatchObject({ ok: true, report: { unavailable_reason: "read_failure", activation_state: "unavailable",
        persisted_revision: null, configured_policy_equal: null, window_count: null, clock_valid: null } });
      expect(JSON.stringify(result)).not.toContain(readKey);
    } finally { exec.mockRestore(); }
  });
  expect(await budget()).toEqual(before);
});

it("requires exact persisted authority configuration for apply without changing its rows", async () => {
  await provision(0, settings(null));
  const before = await allocations();
  for (const config of [
    { ...settings(), PROVISIONED_IDS: [] }, { ...settings(), PROVISIONING_CONFIGURATION_VERSION: "4" },
    { ...settings(), PROVISIONING_CEILING: "99" }, { ...settings(), PROVISIONING_HARD_MAXIMUM: "101" },
    { ...settings(), PROVISIONING_CEILING: undefined }
  ]) expect(await admin(config)).toEqual({ ok: false, error: "authority_configuration_unavailable" });
  expect(await allocations()).toEqual(before);
  expect(await status()).toMatchObject({ report: { activation_state: "never_activated" } });
});

it("stops without any creation request, retains the deadline on repeat, and rolls only expired budgets", async () => {
  await apply(); await provision(1); await provision(2);
  const stopped = settings({ ...policy, revision: 2, enabled: false, windowSeconds: 1 });
  expect((await admin(stopped, 1, start + 100)).ok).toBe(true);
  expect(await status(stopped, start + 100)).toMatchObject({ report: { window_count: 2,
    window_ends_ms: start + 10_000, new_allocation_state: "stopped" } });
  const before = await budget();
  expect(await admin(stopped, 1, start + 20_000)).toMatchObject({ status: "already_applied" });
  expect(await budget()).toEqual(before);
  const resumed = settings({ ...policy, revision: 3, windowSeconds: 1 });
  expect((await admin(resumed, 2, start + 20_000)).ok).toBe(true);
  expect(await status(resumed, start + 20_000)).toMatchObject({ report: { window_count: 0,
    window_ends_ms: start + 21_000, new_allocation_state: "enabled" } });
});

it("retains policy enforcement on compatible rollback, including stop and absent configuration", async () => {
  const legacy = settings(); delete legacy.PROVISIONING_ALLOCATION_POLICY;
  const first = await provision(1, legacy);
  await apply();
  await admin(settings({ ...policy, revision: 2, enabled: false }), 1);
  await abortAllDurableObjects();
  for (const config of [legacy, settings(), settings({ ...policy, revision: 2, enabled: false })]) {
    expect(await provision(1, config)).toEqual(first);
    expect((await provision(2, config)).ok).toBe(false);
  }
  const resumed = settings({ ...policy, revision: 3 });
  expect((await admin(resumed, 2)).ok).toBe(true);
  expect((await provision(2, resumed)).ok).toBe(true);
});

it("exposes no policy operation through creation, publisher or browser routes", async () => {
  const getByName = vi.fn(), get = vi.fn();
  const config = { ...settings(), PROVISIONING_AUTHORITY: { getByName } as unknown as Env["PROVISIONING_AUTHORITY"],
    OVERLAYS: { getByName, get } as unknown as Env["OVERLAYS"] };
  for (const operation of ["apply-configured-policy", "applyConfiguredPolicy", "policyStatus", "policy-status"]) {
    for (const path of [`/api/v1/${operation}`, `/api/v1/overlays/${operation}`, `/api/v1/overlays/${seed}/${operation}`]) {
      const response = await worker.fetch(new Request(`https://overlay.test${path}`, {
        method: "POST", headers: { Authorization: `Bearer ${applyKey}`, "Content-Type": "application/json" },
        body: JSON.stringify({ expectedRevision: 0 }) }), config);
      expect(response.status).toBe(404);
    }
    const response = await worker.fetch(new Request("https://overlay.test/api/v1/overlays", {
      method: "POST", headers: { "CF-Connecting-IP": "192.0.2.1", "Content-Type": "application/json" },
      body: JSON.stringify({ ...claim(1), operation }) }), config);
    expect(response.status).toBe(400);
  }
  expect(getByName).not.toHaveBeenCalled(); expect(get).not.toHaveBeenCalled();
});

it("requires a complete revision-one opt-in and never creates policy state for invalid activation", async () => {
  for (const value of [null, {}, { ...policy, revision: 2 }, { ...policy, enabled: undefined }])
    expect((await provision(1, settings(value))).ok).toBe(false);
  await runInDurableObject(stub(), (_, state) => {
    expect(state.storage.sql.exec("SELECT name FROM sqlite_master WHERE name='allocation_policy'").toArray()).toEqual([]);
  });
  expect(await allocations()).toHaveLength(1); // retained legacy seed only
  expect((await provision(1, settings(), Number.MAX_SAFE_INTEGER)).ok).toBe(false);
  expect((await provision(1)).ok).toBe(false);
  expect(await apply()).toMatchObject({ ok: true, status: "applied" });
  expect((await provision(1)).ok).toBe(true);
});

it("migrates the full synthetic 100-identity lifetime pool and grows without recycling identities", async () => {
  const legacy = settings(); delete legacy.PROVISIONING_ALLOCATION_POLICY;
  const existing: string[] = [];
  for (let i = 1; i <= 99; i++) {
    const result = await provision(i, legacy);
    expect(result.ok).toBe(true);
    if (result.ok) existing.push(result.overlayId);
  }
  expect((await provision(100, legacy)).ok).toBe(false);
  const before = await targetRows(existing[0]);
  const originalLedger = await allocations();
  expect((await apply()).ok).toBe(true);
  for (let i = 100; i <= 105; i++) {
    const result = await provision(i, settings(), start + Math.floor((i - 100) / 2) * 10_000);
    expect(result.ok).toBe(true);
    if (result.ok) existing.push(result.overlayId);
  }
  expect(new Set(existing).size).toBe(105);
  const grown = await allocations();
  expect(grown).toHaveLength(106); // 105 namespace identities plus the unchanged legacy seed.
  for (const row of originalLedger) expect(grown).toContainEqual(row);
  expect(await targetRows(existing[0])).toEqual(before);
  await abortAllDurableObjects();
  const stopped = settings({ ...policy, revision: 2, enabled: false });
  expect(await provision(1, stopped, start + 30_000)).toEqual({ ok: true, overlayId: existing[0] });
  expect(await targetRows(existing[0])).toEqual(before);
  await runInDurableObject(stub(), (_, state) => {
    expect(state.storage.sql.exec("SELECT configuration_version,ceiling,hard_maximum FROM authority_configuration").one())
      .toEqual({ configuration_version: 3, ceiling: 100, hard_maximum: 100 });
    expect(reconcileAuthority(state, stopped).unavailable_reason).toBe("unsupported_policy");
  });
});

it("serializes concurrent new reservations at the window budget without full-ledger counts", async () => {
  await apply(settings({ ...policy, maxNewAllocations: 3 }));
  const results = await runInDurableObject(stub(), async (_, state) => {
    const clock = vi.spyOn(Date, "now").mockReturnValue(start);
    const exec = state.storage.sql.exec.bind(state.storage.sql);
    const statements: string[] = [];
    const spy = vi.spyOn(state.storage.sql, "exec").mockImplementation(((sql: string, ...args: unknown[]) => {
      statements.push(sql); return exec(sql, ...args);
    }) as typeof state.storage.sql.exec);
    try {
      const results = await Promise.all(Array.from({ length: 12 }, (_, i) =>
        new ProvisioningAuthority(state, settings({ ...policy, maxNewAllocations: 3 })).provision(claim(i + 1))));
      expect(statements.some(sql => /COUNT\(\*\).*FROM allocations/i.test(sql))).toBe(false);
      return results;
    } finally { spy.mockRestore(); clock.mockRestore(); }
  });
  expect(results.filter(result => result.ok)).toHaveLength(3);
  expect(await allocations()).toHaveLength(4);
  expect(await budget()).toMatchObject([{ window_count: 3 }]);
  await abortAllDurableObjects();
  expect((await provision(13, settings({ ...policy, maxNewAllocations: 3 }))).ok).toBe(false);
  expect((await provision(13, settings({ ...policy, maxNewAllocations: 3 }), start + 10_000)).ok).toBe(true);
});

it("charges concurrent duplicates once and retains the same identity after restart", async () => {
  await apply();
  const results = await runInDurableObject(stub(), (_, state) => Promise.all(Array.from({ length: 8 }, () =>
    new ProvisioningAuthority(state, settings()).provision(claim(1)))));
  expect(results.every(result => result.ok)).toBe(true);
  expect(results.every(result => JSON.stringify(result) === JSON.stringify(results[0]))).toBe(true);
  expect(await budget()).toMatchObject([{ window_count: 1 }]);
  const before = await allocations();
  await abortAllDurableObjects();
  const missingPolicy = settings(); delete missingPolicy.PROVISIONING_ALLOCATION_POLICY;
  expect(await provision(1, missingPolicy)).toEqual(results[0]);
  expect((await provision(2, missingPolicy)).ok).toBe(false);
  expect(await allocations()).toEqual(before);
});

it("keeps failed target reservations charged and recovers them during an emergency stop", async () => {
  const id = env.OVERLAYS.newUniqueId();
  const target = vi.fn().mockRejectedValueOnce(new Error("synthetic target failure")).mockResolvedValue(true);
  const unique = vi.fn(() => id);
  const overlays = { newUniqueId: unique, idFromString: vi.fn(() => id), get: vi.fn(() => ({ provisionV2: target })) } as unknown as Env["OVERLAYS"];
  const config = { ...settings({ ...policy, maxNewAllocations: 1 }), OVERLAYS: overlays };
  await apply(config);
  await expect(provision(1, config)).rejects.toThrow("synthetic target failure");
  expect(await budget()).toMatchObject([{ window_count: 1 }]);
  expect(await allocations()).toContainEqual(expect.objectContaining({ overlay_id: id.toString(), phase: "reserved" }));
  await abortAllDurableObjects();
  const stopped = { ...config, PROVISIONING_ALLOCATION_POLICY: { ...policy, revision: 2, enabled: false, maxNewAllocations: 1 } };
  await apply(stopped, 1);
  expect((await provision(2, stopped)).ok).toBe(false);
  expect(await provision(1, stopped)).toEqual({ ok: true, overlayId: id.toString() });
  expect(unique).toHaveBeenCalledOnce();
  expect(target).toHaveBeenCalledTimes(2);
  expect(await budget()).toMatchObject([{ revision: 2, window_count: 1 }]);
  expect(await allocations()).toContainEqual(expect.objectContaining({ overlay_id: id.toString(), phase: "active" }));
});

it("denies exhausted budgets before generating an identity or accessing a target", async () => {
  const config = settings({ ...policy, maxNewAllocations: 1 });
  await apply(config);
  await provision(1, config);
  const newUniqueId = vi.fn(), get = vi.fn(), idFromString = vi.fn();
  const denied = { ...config, OVERLAYS: { newUniqueId, get, idFromString } as unknown as Env["OVERLAYS"] };
  expect((await provision(2, denied)).ok).toBe(false);
  expect(newUniqueId).not.toHaveBeenCalled();
  expect(get).not.toHaveBeenCalled();
  expect(idFromString).not.toHaveBeenCalled();
  expect(await budget()).toMatchObject([{ window_count: 1 }]);
});

it("rolls back a budget charge if the allocation INSERT fails, retaining the explicit revision", async () => {
  await apply(settings({ ...policy, enabled: false }));
  expect((await provision(1, settings({ ...policy, enabled: false }))).ok).toBe(false);
  await runInDurableObject(stub(), (_, state) => state.storage.sql.exec(
    "CREATE TRIGGER fail_allocation BEFORE INSERT ON allocations WHEN NEW.request_id NOT LIKE 'legacy:%' BEGIN SELECT RAISE(ABORT, 'synthetic insert failure'); END"));
  const updated = settings({ ...policy, revision: 2 });
  await apply(updated, 1);
  const before = await budget();
  await expect(provision(1, updated)).rejects.toThrow("synthetic insert failure");
  expect(await budget()).toEqual(before);
  expect(await allocations()).toHaveLength(1);
  await runInDurableObject(stub(), (_, state) => state.storage.sql.exec("DROP TRIGGER fail_allocation"));
  expect((await provision(1, updated)).ok).toBe(true);
  expect(await budget()).toMatchObject([{ revision: 2, window_count: 1 }]);
});

it("recovers a lost active-phase commit without changing target credentials or spending another slot", async () => {
  await apply(settings({ ...policy, enabled: false }));
  await provision(99, settings({ ...policy, enabled: false }));
  await runInDurableObject(stub(), (_, state) => state.storage.sql.exec(
    "CREATE TRIGGER fail_active BEFORE UPDATE OF phase ON allocations BEGIN SELECT RAISE(ABORT, 'synthetic phase failure'); END"));
  const config = settings({ ...policy, revision: 2 });
  await apply(config, 1);
  await expect(provision(1, config)).rejects.toThrow("synthetic phase failure");
  const reserved = (await allocations()).find(row => row.request_id === claim(1).requestId)!;
  const before = await targetRows(reserved.overlay_id as string);
  expect(await budget()).toMatchObject([{ window_count: 1 }]);
  await abortAllDurableObjects();
  await runInDurableObject(stub(), (_, state) => state.storage.sql.exec("DROP TRIGGER fail_active"));
  expect(await provision(1, config)).toEqual({ ok: true, overlayId: reserved.overlay_id });
  expect(await targetRows(reserved.overlay_id as string)).toEqual(before);
  expect(await budget()).toMatchObject([{ window_count: 1 }]);
});

it("does not reset consumed budget on stop, resume, or shorter-window configuration changes", async () => {
  await apply();
  expect((await provision(1)).ok).toBe(true);
  expect((await provision(2)).ok).toBe(true);
  await apply(settings({ ...policy, revision: 2, enabled: false }), 1, start + 1_000);
  expect((await provision(3, settings({ ...policy, revision: 2, enabled: false }), start + 1_000)).ok).toBe(false);
  const resumed = { ...policy, revision: 3, windowSeconds: 1 };
  await apply(settings(resumed), 2, start + 2_000);
  expect((await provision(3, settings(resumed), start + 2_000)).ok).toBe(false);
  expect(await budget()).toMatchObject([{ revision: 3, window_count: 2, window_ends_ms: start + 11_000 }]);
  const before = await budget();
  for (const bad of [policy, { ...resumed, maxNewAllocations: 3 }, { ...resumed, revision: 5 }])
    expect((await provision(3, settings(bad), start + 3_000)).ok).toBe(false);
  expect(await budget()).toEqual(before);
  expect((await provision(3, settings(resumed), start + 11_000)).ok).toBe(true);
  expect(await budget()).toMatchObject([{ revision: 3, window_count: 1 }]);
  expect((await provision(4, settings(resumed), start + 10_999)).ok).toBe(false);
  expect((await provision(1, settings(resumed), start - 1)).ok).toBe(true); // exact replay needs no new clock budget
});

it.each([null, "enabled", {}, { ...policy, v: 2 }, { ...policy, revision: 0 }, { ...policy, enabled: "true" },
  { ...policy, windowSeconds: 0 }, { ...policy, windowSeconds: 1.5 }, { ...policy, windowSeconds: Number.MAX_SAFE_INTEGER },
  { ...policy, maxNewAllocations: -1 }, { ...policy, maxNewAllocations: 1.5 }, { ...policy, extra: true }])(
  "fails closed for malformed policy %j without preventing verified replay", async value => {
    await apply();
    const first = await provision(1);
    const before = await budget();
    expect((await provision(2, settings(value))).ok).toBe(false);
    expect(await provision(1, settings(value))).toEqual(first);
    expect(await budget()).toEqual(before);
    const changed = { ...claim(1), writeVerifier: "c".repeat(64) };
    expect((await runInDurableObject(stub(), (_, state) => new ProvisioningAuthority(state, settings(value)).provision(changed))).ok).toBe(false);
  });

it.each(["DELETE FROM allocation_policy", "UPDATE allocation_policy SET window_count=-1",
  "UPDATE allocation_policy SET policy_digest='invalid'", "UPDATE allocation_policy SET window_ends_ms=last_observed_ms"])(
  "does not repair corrupted policy state: %s", async sql => {
    await apply();
    await provision(1);
    await runInDurableObject(stub(), (_, state) => state.storage.sql.exec(sql));
    const before = await budget();
    expect((await provision(2)).ok).toBe(false);
    const absent = settings(); delete absent.PROVISIONING_ALLOCATION_POLICY;
    expect((await provision(2, absent)).ok).toBe(false);
    expect(await budget()).toEqual(before);
  });

it("keeps publisher and reader admission independent of the new-allocation stop", async () => {
  await apply();
  const first = await provision(1);
  if (!first.ok) throw new Error("synthetic setup failed");
  const stopped = settings({ ...policy, revision: 2, enabled: false });
  await apply(stopped, 1);
  expect((await provision(2, stopped)).ok).toBe(false);
  const creation = vi.fn(async () => ({ success: false }));
  const config = { ...stopped, CREATE_CLIENT_RATE_LIMITER: { limit: creation } as RateLimit,
    CREATE_SERVICE_RATE_LIMITER: { limit: creation } as RateLimit };
  const publisher = () => new Request(`https://overlay.test/api/v1/overlays/${first.overlayId}/publisher`, {
    headers: { Authorization: `Bearer ${write}`, "X-SoulsTracker-Protocol": "2" } });
  expect((await worker.fetch(publisher(), config)).status).toBe(200);
  const denied = { limit: async () => ({ success: false }) } as RateLimit;
  expect((await worker.fetch(publisher(), { ...config, PUBLISHER_RATE_LIMITER: denied })).status).toBe(429);
  const live = () => new Request(`https://overlay.test/api/v1/overlays/${first.overlayId}/live`, {
    headers: { Upgrade: "websocket", Origin: "https://overlay.test" } });
  expect((await worker.fetch(live(), { ...config, LIVE_RATE_LIMITER: denied })).status).toBe(429);
  const response = await worker.fetch(live(), { ...config, PUBLISHER_RATE_LIMITER: denied });
  expect(response.status).toBe(101);
  const socket = response.webSocket!; socket.accept();
  const hydrated = new Promise<MessageEvent>(resolve => socket.addEventListener("message", resolve, { once: true }));
  socket.send(JSON.stringify({ v: 1, type: "auth", protocolVersion: 2, readCapability: read }));
  expect(JSON.parse(String((await hydrated).data))).toMatchObject({ v: 2, death: null });
  socket.close();
  expect(creation).not.toHaveBeenCalled();
});

it("rejects client-supplied policy, credentials, wrong origins and missing admission before authority access", async () => {
  const getByName = vi.fn();
  const config = { ...settings(), PROVISIONING_AUTHORITY: { getByName } as unknown as Env["PROVISIONING_AUTHORITY"] };
  const request = (body: unknown = claim(1), headers: Record<string, string> = {}, origin = "https://overlay.test") =>
    new Request(`${origin}/api/v1/overlays`, { method: "POST", headers: {
      "Content-Type": "application/json", "CF-Connecting-IP": "192.0.2.1", ...headers }, body: JSON.stringify(body) });
  for (const [req, expected] of [[request({ ...claim(1), policy }), 400],
    [request(claim(1), { Authorization: `Bearer ${write}` }), 400],
    [request(claim(1), {}, "https://other.test"), 403]] as const)
    expect((await worker.fetch(req, config)).status).toBe(expected);
  expect((await worker.fetch(request(), { ...config, CREATE_CLIENT_RATE_LIMITER: undefined })).status).toBe(503);
  expect((await worker.fetch(request(), { ...config, CREATE_SERVICE_RATE_LIMITER: undefined })).status).toBe(503);
  expect(getByName).not.toHaveBeenCalled();
});
