import { env } from "cloudflare:workers";
import { abortAllDurableObjects, reset, runInDurableObject } from "cloudflare:test";
import { afterEach, expect, it, vi } from "vitest";
import { ProvisioningAuthority as Recovery } from "./provisioning-authority";
import { ProvisioningAuthority as Candidate } from "../src/provisioning-authority";
import type { Env } from "../src/index";
import { verifierV2 } from "../src/protocol";
import type {} from "../test/authority.test";

const read = "c".repeat(64), apply = "d".repeat(64);
const policy = { v: 1, revision: 1, enabled: true, windowSeconds: 3600, maxNewAllocations: 2 };
const settings = (overrides: Partial<Env> = {}): Env => ({ ...env, PROVISIONED_IDS: ["1".repeat(32)],
  PROVISIONING_CONFIGURATION_VERSION: "3", PROVISIONING_CEILING: "100", PROVISIONING_HARD_MAXIMUM: "100",
  PROVISIONING_POLICY_READ_KEY: read, PROVISIONING_POLICY_APPLY_KEY: apply, ...overrides });
const claim = (index: number) => {
  const requestId = index.toString(16).padStart(64, "0");
  return { v: 1 as const, requestId, readVerifier: verifierV2(requestId, "read", "a".repeat(64)),
    writeVerifier: verifierV2(requestId, "write", "b".repeat(64)) };
};
const stub = () => env.PROVISIONING_AUTHORITY!.getByName("global");
const provision = (index: number, config = settings(), implementation: typeof Recovery | typeof Candidate = Recovery) =>
  runInDurableObject(stub(), (_, state) => new implementation(state, config).provision(claim(index)));
const snapshot = () => runInDurableObject(stub(), (_, state) => {
  const tables = state.storage.sql.exec<{ name: string }>("SELECT name FROM sqlite_master WHERE type='table' AND name IN ('allocations','authority_configuration','allocation_policy') ORDER BY name").toArray();
  return Object.fromEntries(tables.map(({ name }) => [name, state.storage.sql.exec(`SELECT * FROM ${name} ORDER BY 1`).toArray()]));
});
afterEach(() => { vi.restoreAllMocks(); return reset(); });

it("recovers candidate identity and exact replay across restart without changing authority 3/100/100", async () => {
  const first = await provision(1, settings(), Candidate);
  expect(first.ok).toBe(true);
  const before = await snapshot();
  await abortAllDurableObjects();
  expect(await provision(1)).toEqual(first);
  expect(await snapshot()).toEqual(before);
  expect((await provision(2)).ok).toBe(true);
  expect((await snapshot()).authority_configuration).toMatchObject([{ configuration_version: 3, ceiling: 100, hard_maximum: 100 }]);
});

it("keeps the lifetime ceiling, conflicting replay and configuration skew fail closed", async () => {
  const config = settings({ PROVISIONED_IDS: [], PROVISIONING_CEILING: "1" });
  const first = await provision(1, config);
  expect(first.ok).toBe(true);
  expect((await provision(2, config)).ok).toBe(false);
  expect(await provision(1, config)).toEqual(first);
  const before = await snapshot();
  expect((await provision(1, { ...config, PROVISIONING_CEILING: "2" })).ok).toBe(false);
  await runInDurableObject(stub(), async (_, state) => {
    expect(await new Recovery(state, config).provision({ ...claim(1), readVerifier: "9".repeat(64) })).toEqual({ ok: false });
  });
  expect(await snapshot()).toEqual(before);
});

it("authenticates policy roles before SQL and keeps cold reads/application free of initialization", async () => {
  await runInDurableObject(stub(), async (_, state) => {
    const authority = new Recovery(state, settings({ PROVISIONING_ALLOCATION_POLICY: policy }));
    const sql = vi.spyOn(state.storage.sql, "exec");
    for (const credential of [undefined, "", read, {}])
      expect(await authority.applyConfiguredPolicy(credential, 0)).toEqual({ ok: false, error: "forbidden" });
    for (const credential of [undefined, "", apply, {}])
      expect(await authority.policyStatus(credential)).toEqual({ ok: false, error: "forbidden" });
    expect(sql).not.toHaveBeenCalled();
    sql.mockRestore();
    expect(await authority.applyConfiguredPolicy(apply, 0)).toEqual({ ok: false, error: "authority_configuration_unavailable" });
    expect(await authority.policyStatus(read)).toMatchObject({ report: { activation_state: "never_activated", new_allocation_state: "blocked" } });
  });
  expect(await snapshot()).toEqual({});
});

it("preserves policy revision, consumption and deadline while blocking new recovery allocations", async () => {
  const first = await provision(1, settings(), Candidate);
  const configured = settings({ PROVISIONING_ALLOCATION_POLICY: policy });
  await runInDurableObject(stub(), async (_, state) => {
    expect(await new Candidate(state, configured).applyConfiguredPolicy(apply, 0)).toMatchObject({ status: "applied" });
  });
  expect((await provision(2, configured, Candidate)).ok).toBe(true);
  const before = await snapshot();
  await abortAllDurableObjects();
  for (const config of [settings(), configured, settings({ PROVISIONING_ALLOCATION_POLICY: null })]) {
    expect(await provision(1, config)).toEqual(first);
    expect((await provision(3, config)).ok).toBe(false);
  }
  expect(await snapshot()).toEqual(before);
  await runInDurableObject(stub(), async (_, state) => {
    const authority = new Recovery(state, configured);
    expect(await authority.policyStatus(read)).toMatchObject({ report: { persisted_revision: 1, window_count: 1, new_allocation_state: "blocked" } });
    expect(await authority.applyConfiguredPolicy(apply, 0)).toMatchObject({ status: "already_applied" });
    const stopped = new Recovery(state, settings({ PROVISIONING_ALLOCATION_POLICY: { ...policy, revision: 2, enabled: false } }));
    expect(await stopped.applyConfiguredPolicy(apply, 1)).toMatchObject({ status: "applied", revision: 2 });
    expect(await stopped.policyStatus(read)).toMatchObject({ report: { persisted_revision: 2, window_count: 1, new_allocation_state: "blocked" } });
  });
  const after = await snapshot();
  expect(after.allocations).toEqual(before.allocations);
  expect(after.authority_configuration).toEqual(before.authority_configuration);
  expect(Number(after.allocation_policy[0].window_ends_ms)).toBeGreaterThanOrEqual(Number(before.allocation_policy[0].window_ends_ms));
});

it("blocks configured, empty and corrupt policy state without repairing it or losing exact replay", async () => {
  const first = await provision(1);
  for (const value of [null, {}, policy]) {
    expect((await provision(2, settings({ PROVISIONING_ALLOCATION_POLICY: value }))).ok).toBe(false);
  }
  await runInDurableObject(stub(), (_, state) => state.storage.sql.exec("CREATE TABLE allocation_policy (singleton INTEGER)"));
  for (const corrupt of [false, true]) {
    if (corrupt) await runInDurableObject(stub(), (_, state) => state.storage.sql.exec("INSERT INTO allocation_policy VALUES(2)"));
    const before = await snapshot();
    expect((await provision(2)).ok).toBe(false);
    expect(await provision(1)).toEqual(first);
    expect(await snapshot()).toEqual(before);
  }
});
