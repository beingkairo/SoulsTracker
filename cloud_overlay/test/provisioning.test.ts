import { env } from "cloudflare:workers";
import { reset, runInDurableObject } from "cloudflare:test";
import { afterEach, expect, it, vi } from "vitest";
import worker, { type Env } from "../src/index";
import { OverlayState } from "../src/overlay-state";
import { verifier } from "../src/protocol";
import type {} from "./authority.test";

const id = "1".repeat(32), other = "2".repeat(32), grant = "3".repeat(64);
const readCapability = "4".repeat(64), writeCapability = "5".repeat(64);
const slot = { v: 1 as const, overlayId: id, setupVerifier: verifier(id, "setup", grant) };
const claim = { v: 1, requestId: "6".repeat(32), readVerifier: verifier(id, "read", readCapability),
  writeVerifier: verifier(id, "write", writeCapability) };

afterEach(() => reset());

function request(identity = id, token: string | null = grant, body: unknown = claim) {
  const headers = new Headers({ "Content-Type": "application/json", "Cache-Control": "no-store" });
  if (token !== null) headers.set("Authorization", `Setup ${token}`);
  return new Request(`https://overlay.test/api/v1/overlays/${identity}/provision`, {
    method: "POST", headers, body: JSON.stringify(body)
  });
}

function environment(overrides: Partial<Env> = {}): Env {
  return { ...env, PROVISIONED_IDS: [id], PROVISIONING_SLOTS: [slot], BROWSER_ORIGIN: "https://overlay.test", ...overrides };
}

async function rows() {
  return runInDurableObject(env.OVERLAYS.getByName(id), (_, state) =>
    state.storage.sql.exec<{ key: string; value: string }>("SELECT key,value FROM records ORDER BY key").toArray());
}

it("denies unknown and non-slot identities before limiter and namespace access", async () => {
  const getByName = vi.fn();
  const limit = vi.fn(async () => ({ success: true }));
  for (const [identity, ids, slots] of [[other, [id], [slot]], [id, [id], []]] as const) {
    const result = await worker.fetch(request(identity), environment({ PROVISIONED_IDS: [...ids], PROVISIONING_SLOTS: [...slots],
      OVERLAYS: { getByName } as unknown as Env["OVERLAYS"], PROVISIONING_RATE_LIMITER: { limit } }));
    expect(result.status).toBe(404);
  }
  expect(limit).not.toHaveBeenCalled();
  expect(getByName).not.toHaveBeenCalled();
});

it("admits a known-slot wrong grant before denying it without namespace access", async () => {
  const getByName = vi.fn();
  const limit = vi.fn(async () => ({ success: true }));
  const result = await worker.fetch(request(id, readCapability), environment({
    OVERLAYS: { getByName } as unknown as Env["OVERLAYS"], PROVISIONING_RATE_LIMITER: { limit }
  }));
  expect(result.status).toBe(403);
  expect(limit).toHaveBeenCalledOnce();
  expect(limit).toHaveBeenCalledWith({ key: `overlay-v1:setup:${id}` });
  expect(getByName).not.toHaveBeenCalled();
});

it("does not let missing or malformed setup authorization bypass admission", async () => {
  const getByName = vi.fn();
  const limit = vi.fn(async () => ({ success: true }));
  for (const token of [null, "malformed"] as const) {
    const result = await worker.fetch(request(id, token), environment({
      OVERLAYS: { getByName } as unknown as Env["OVERLAYS"], PROVISIONING_RATE_LIMITER: { limit }
    }));
    expect(result.status).toBe(403);
  }
  expect(limit).toHaveBeenCalledTimes(2);
  expect(getByName).not.toHaveBeenCalled();
});

it("fails closed on rate denial or unavailable admission before comparing the grant", async () => {
  const getByName = vi.fn();
  const deniedLimit = vi.fn(async () => ({ success: false }));
  const denied = await worker.fetch(request(id, readCapability), environment({
    OVERLAYS: { getByName } as unknown as Env["OVERLAYS"], PROVISIONING_RATE_LIMITER: { limit: deniedLimit }
  }));
  expect(denied.status).toBe(429);
  expect(await denied.json()).toEqual({ error: "rate_limited" });
  expect(denied.headers.get("Retry-After")).toBe("60");
  expect(deniedLimit).toHaveBeenCalledOnce();

  for (const replacement of [undefined, { limit: async () => ({}) }, { limit: async () => { throw new Error("private"); } }] as const) {
    const result = await worker.fetch(request(id, readCapability), environment({
      OVERLAYS: { getByName } as unknown as Env["OVERLAYS"],
      PROVISIONING_RATE_LIMITER: replacement as unknown as RateLimit
    }));
    expect(result.status).toBe(503);
    expect(await result.json()).toEqual({ error: "admission_unavailable" });
    expect(result.headers.get("Retry-After")).toBe("60");
  }
  expect(getByName).not.toHaveBeenCalled();
});

it("transactionally provisions once and exact replay is idempotent without publisher state", async () => {
  const first = await worker.fetch(request(), environment());
  expect(first.status).toBe(200);
  expect(await first.json()).toEqual({ v: 1, status: "provisioned" });
  const stored = await rows();
  expect(stored.map(row => row.key)).toEqual(["appearance", "control", "death"]);
  const control = JSON.parse(stored.find(row => row.key === "control")!.value);
  expect(control.readVerifier).toBe(claim.readVerifier);
  expect(control.writeVerifier).toBe(claim.writeVerifier);
  expect(control.epoch).toBe("0");
  expect(control.session).toBeNull();
  expect(control.last).toBeNull();
  expect(stored.find(row => row.key === "death")!.value).toBe("null");
  expect((await worker.fetch(request(), environment())).status).toBe(200);
  expect(await rows()).toEqual(stored);
});

it("denies changed reuse existing and orphan state without mutation", async () => {
  expect((await worker.fetch(request(), environment())).status).toBe(200);
  const before = await rows();
  for (const body of [{ ...claim, requestId: "7".repeat(32) }, { ...claim, readVerifier: verifier(id, "read", "8".repeat(64)) }]) {
    expect((await worker.fetch(request(id, grant, body), environment())).status).toBe(409);
    expect(await rows()).toEqual(before);
  }

  await reset();
  await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) =>
    state.storage.sql.exec("INSERT INTO records (key,value) VALUES ('death','null')"));
  const orphan = await worker.fetch(request(), environment());
  expect(orphan.status).toBe(409);
  expect((await rows()).map(row => row.key)).toEqual(["death"]);
});

it("denies exact replay when initialized storage is missing malformed or extended", async () => {
  const mutations = [
    (state: DurableObjectState) => state.storage.sql.exec("DELETE FROM records WHERE key='death'"),
    (state: DurableObjectState) => state.storage.sql.exec("UPDATE records SET value='{}' WHERE key='death'"),
    (state: DurableObjectState) => state.storage.sql.exec("UPDATE records SET value='{}' WHERE key='appearance'"),
    (state: DurableObjectState) => {
      const row = state.storage.sql.exec<{ value: string }>("SELECT value FROM records WHERE key='control'").toArray()[0];
      state.storage.sql.exec("UPDATE records SET value=? WHERE key='control'", JSON.stringify({ ...JSON.parse(row.value), extra: true }));
    }
  ];
  for (const mutate of mutations) {
    expect((await worker.fetch(request(), environment())).status).toBe(200);
    await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => mutate(state));
    const partial = await rows();
    expect((await worker.fetch(request(), environment())).status).toBe(409);
    expect(await rows()).toEqual(partial);
    await reset();
  }
});

it("selects one concurrent claim and rolls back partial storage failure", async () => {
  const alternate = { ...claim, requestId: "9".repeat(32), readVerifier: verifier(id, "read", "a".repeat(64)) };
  const results = await Promise.all([worker.fetch(request(), environment()), worker.fetch(request(id, grant, alternate), environment())]);
  expect(results.map(result => result.status).sort()).toEqual([200, 409]);
  expect((await rows()).map(row => row.key)).toEqual(["appearance", "control", "death"]);

  await reset();
  await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) =>
    state.storage.sql.exec("CREATE TRIGGER fail_appearance BEFORE INSERT ON records WHEN NEW.key='appearance' BEGIN SELECT RAISE(ABORT,'synthetic'); END"));
  expect((await worker.fetch(request(), environment())).status).toBe(500);
  expect(await rows()).toEqual([]);
});

it("strictly rejects malformed bodies and role-confused verifier pairs", async () => {
  const malformed: unknown[] = [null, {}, { ...claim, v: 2 }, { ...claim, extra: true },
    { ...claim, requestId: "A".repeat(32) }, { ...claim, readVerifier: "bad" },
    { ...claim, writeVerifier: claim.readVerifier }];
  for (const body of malformed) {
    const result = await worker.fetch(request(id, grant, body), environment());
    expect(result.status).toBe(400);
    expect(await rows()).toEqual([]);
  }
});

it("the object independently rejects cross-ID setup authorization", async () => {
  await runInDurableObject(env.OVERLAYS.getByName(id), async (_, state) => {
    const instance = new OverlayState(state, environment({ PROVISIONING_SLOTS: [{ ...slot, setupVerifier: verifier(other, "setup", grant) }] }));
    expect((await instance.fetch(request())).status).toBe(403);
    expect(state.storage.sql.exec("SELECT key FROM records").toArray()).toEqual([]);
  });
});
