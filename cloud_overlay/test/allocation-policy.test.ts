import { env } from "cloudflare:workers";
import { abortAllDurableObjects, reset, runInDurableObject } from "cloudflare:test";
import { afterEach, expect, it, vi } from "vitest";
import worker, { type Env } from "../src/index";
import { ProvisioningAuthority } from "../src/provisioning-authority";
import { verifierV2 } from "../src/protocol";
import { reconcileAuthority } from "../src/reconciliation";
import type { AllocationPolicy } from "../src/allocation-policy";
import type {} from "./authority.test";

const seed = "1".repeat(32), read = "a".repeat(64), write = "b".repeat(64);
const start = 1_000_000;
const policy: AllocationPolicy = { v: 1, revision: 1, enabled: true, windowSeconds: 10, maxNewAllocations: 2 };
const allowed = { limit: async () => ({ success: true }) } as RateLimit;
const settings = (value: unknown = policy): Env => ({ ...env, PROVISIONED_IDS: [seed],
  BROWSER_ORIGIN: "https://overlay.test", PROVISIONING_CONFIGURATION_VERSION: "3",
  PROVISIONING_CEILING: "100", PROVISIONING_HARD_MAXIMUM: "100", PROVISIONING_ALLOCATION_POLICY: value,
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
const allocations = () => runInDurableObject(stub(), (_, state) =>
  state.storage.sql.exec("SELECT * FROM allocations ORDER BY request_id").toArray());
const budget = () => runInDurableObject(stub(), (_, state) =>
  state.storage.sql.exec("SELECT * FROM allocation_policy").toArray());
const targetRows = (id: string) => runInDurableObject(env.OVERLAYS.get(env.OVERLAYS.idFromString(id)), (_, state) =>
  state.storage.sql.exec("SELECT * FROM records ORDER BY key").toArray());
afterEach(() => { vi.restoreAllMocks(); return reset(); });

it("requires a complete revision-one opt-in and never creates policy state for invalid activation", async () => {
  for (const value of [null, {}, { ...policy, revision: 2 }, { ...policy, enabled: undefined }])
    expect((await provision(1, settings(value))).ok).toBe(false);
  await runInDurableObject(stub(), (_, state) => {
    expect(state.storage.sql.exec("SELECT name FROM sqlite_master WHERE name='allocation_policy'").toArray()).toEqual([]);
  });
  expect(await allocations()).toHaveLength(1); // retained legacy seed only
  expect((await provision(1, settings(), Number.MAX_SAFE_INTEGER)).ok).toBe(false);
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
  await expect(provision(1, config)).rejects.toThrow("synthetic target failure");
  expect(await budget()).toMatchObject([{ window_count: 1 }]);
  expect(await allocations()).toContainEqual(expect.objectContaining({ overlay_id: id.toString(), phase: "reserved" }));
  await abortAllDurableObjects();
  const stopped = { ...config, PROVISIONING_ALLOCATION_POLICY: { ...policy, revision: 2, enabled: false, maxNewAllocations: 1 } };
  expect((await provision(2, stopped)).ok).toBe(false);
  expect(await provision(1, stopped)).toEqual({ ok: true, overlayId: id.toString() });
  expect(unique).toHaveBeenCalledOnce();
  expect(target).toHaveBeenCalledTimes(2);
  expect(await budget()).toMatchObject([{ revision: 2, window_count: 1 }]);
  expect(await allocations()).toContainEqual(expect.objectContaining({ overlay_id: id.toString(), phase: "active" }));
});

it("denies exhausted budgets before generating an identity or accessing a target", async () => {
  const config = settings({ ...policy, maxNewAllocations: 1 });
  await provision(1, config);
  const newUniqueId = vi.fn(), get = vi.fn(), idFromString = vi.fn();
  const denied = { ...config, OVERLAYS: { newUniqueId, get, idFromString } as unknown as Env["OVERLAYS"] };
  expect((await provision(2, denied)).ok).toBe(false);
  expect(newUniqueId).not.toHaveBeenCalled();
  expect(get).not.toHaveBeenCalled();
  expect(idFromString).not.toHaveBeenCalled();
  expect(await budget()).toMatchObject([{ window_count: 1 }]);
});

it("rolls back a budget charge and policy revision if the allocation INSERT fails", async () => {
  expect((await provision(1, settings({ ...policy, enabled: false }))).ok).toBe(false);
  const before = await budget();
  await runInDurableObject(stub(), (_, state) => state.storage.sql.exec(
    "CREATE TRIGGER fail_allocation BEFORE INSERT ON allocations WHEN NEW.request_id NOT LIKE 'legacy:%' BEGIN SELECT RAISE(ABORT, 'synthetic insert failure'); END"));
  const updated = settings({ ...policy, revision: 2 });
  await expect(provision(1, updated)).rejects.toThrow("synthetic insert failure");
  expect(await budget()).toEqual(before);
  expect(await allocations()).toHaveLength(1);
  await runInDurableObject(stub(), (_, state) => state.storage.sql.exec("DROP TRIGGER fail_allocation"));
  expect((await provision(1, updated)).ok).toBe(true);
  expect(await budget()).toMatchObject([{ revision: 2, window_count: 1 }]);
});

it("recovers a lost active-phase commit without changing target credentials or spending another slot", async () => {
  await provision(99, settings({ ...policy, enabled: false }));
  await runInDurableObject(stub(), (_, state) => state.storage.sql.exec(
    "CREATE TRIGGER fail_active BEFORE UPDATE OF phase ON allocations BEGIN SELECT RAISE(ABORT, 'synthetic phase failure'); END"));
  const config = settings({ ...policy, revision: 2 });
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
  expect((await provision(1)).ok).toBe(true);
  expect((await provision(2)).ok).toBe(true);
  expect((await provision(3, settings({ ...policy, revision: 2, enabled: false }), start + 1_000)).ok).toBe(false);
  const resumed = { ...policy, revision: 3, windowSeconds: 1 };
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
    await provision(1);
    await runInDurableObject(stub(), (_, state) => state.storage.sql.exec(sql));
    const before = await budget();
    expect((await provision(2)).ok).toBe(false);
    const absent = settings(); delete absent.PROVISIONING_ALLOCATION_POLICY;
    expect((await provision(2, absent)).ok).toBe(false);
    expect(await budget()).toEqual(before);
  });

it("keeps publisher and reader admission independent of the new-allocation stop", async () => {
  const first = await provision(1);
  if (!first.ok) throw new Error("synthetic setup failed");
  const stopped = settings({ ...policy, revision: 2, enabled: false });
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
