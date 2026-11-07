import { env, exports } from "cloudflare:workers";
import { abortAllDurableObjects, listDurableObjectIds, reset, runInDurableObject } from "cloudflare:test";
import { afterEach, beforeEach, expect, it } from "vitest";
import { verifierV2 } from "../src/protocol";
import type {} from "./authority.test";

const origin = "https://overlay.test";
let previousTargets = 0;
// reset clears storage, while the runtime's namespace inventory retains IDs.
beforeEach(async () => { previousTargets = (await listDurableObjectIds(env.OVERLAYS)).length; });
const targetCount = async () => (await listDurableObjectIds(env.OVERLAYS)).length - previousTargets;
const authority = () => env.PROVISIONING_AUTHORITY!.getByName("global");
const credentials = (index: number) => {
  const requestId = index.toString(16).padStart(64, "0");
  const read = (index * 2 + 1).toString(16).padStart(64, "0");
  const write = (index * 2 + 2).toString(16).padStart(64, "0");
  return { read, write, claim: { v: 1, requestId,
    readVerifier: verifierV2(requestId, "read", read), writeVerifier: verifierV2(requestId, "write", write) } };
};
const create = (index: number) => exports.default.fetch(`${origin}/api/v1/overlays`, {
  method: "POST", headers: { "Content-Type": "application/json", "CF-Connecting-IP": "192.0.2.1" },
  body: JSON.stringify(credentials(index).claim)
});
async function created(index: number): Promise<string> {
  const result = await create(index);
  expect(result.status).toBe(200);
  expect(result.headers.get("Cache-Control")).toBe("no-store");
  const body = await result.json() as { overlayId: string };
  expect(Object.keys(body).sort()).toEqual(["overlayId", "status", "v"]);
  expect(body.overlayId).toMatch(/^[0-9a-f]{64}$/);
  return body.overlayId;
}
async function activate() {
  // Pending policy denies the public claim, but establishes the retained legacy
  // authority configuration. Only the explicit operator method applies policy.
  expect((await create(0)).status).toBe(409);
  expect(await authority().applyConfiguredPolicy(env.PROVISIONING_POLICY_APPLY_KEY, 0))
    .toMatchObject({ ok: true, status: "applied", revision: 1 });
}
const status = () => authority().policyStatus(env.PROVISIONING_POLICY_READ_KEY);
const sql = (query: string) => runInDurableObject(authority(), (_, state) => state.storage.sql.exec(query).toArray());
async function expireWindow() {
  const now = Date.now();
  await runInDurableObject(authority(), (_, state) => state.storage.sql.exec(
    "UPDATE allocation_policy SET window_ends_ms=?,last_observed_ms=?", now - 1, now - 1000));
}
const publisher = (id: string, token: string) => exports.default.fetch(`${origin}/api/v1/overlays/${id}/publisher`, {
  headers: { Authorization: `Bearer ${token}`, "X-SoulsTracker-Protocol": "2" }
});
afterEach(() => reset());

it("public requests cannot activate a configured policy or initialize overlay objects while pending", async () => {
  expect((await create(1)).status).toBe(409);
  expect(await status()).toMatchObject({ ok: true, report: { activation_state: "never_activated",
    configured_policy_state: "valid", new_allocation_state: "blocked" } });
  expect(await targetCount()).toBe(0);
  expect(await sql("SELECT * FROM allocations")).toEqual([]);
});

it("admits exactly the window allowance under concurrent public requests and preserves retries after restart", async () => {
  await activate();
  const results = await Promise.all(Array.from({ length: 12 }, (_, i) => create(i + 1)));
  expect(results.filter(r => r.status === 200)).toHaveLength(2);
  expect(results.filter(r => r.status === 409)).toHaveLength(10);
  const winners = await Promise.all(results.flatMap((r, i) => r.status === 200 ? [(async () =>
    ({ index: i + 1, id: (await r.json() as { overlayId: string }).overlayId }))()] : []));
  expect(new Set(winners.map(w => w.id)).size).toBe(2);
  expect(await targetCount()).toBe(2);
  await abortAllDurableObjects();
  for (const winner of winners) expect(await created(winner.index)).toBe(winner.id);
  expect(await status()).toMatchObject({ report: { window_count: 2, new_allocation_state: "blocked" } });
});

it("grows past the historical lifetime ceiling across windows without replacing existing identities", async () => {
  await activate();
  const ids: string[] = [];
  for (let index = 1; index <= 102; index++) {
    if (index > 1 && index % 2 === 1) await expireWindow();
    ids.push(await created(index));
  }
  expect(new Set(ids).size).toBe(102);
  expect(await sql("SELECT COUNT(*) AS count FROM allocations")).toEqual([{ count: 102 }]);
  expect((await create(103)).status).toBe(409);
  await abortAllDurableObjects();
  expect(await created(1)).toBe(ids[0]);
  expect((await publisher(ids[0], credentials(1).write)).status).toBe(200);
  expect(await status()).toMatchObject({ report: { window_count: 2, new_allocation_state: "blocked" } });
}, 30_000);

it("rolls back the budget when storage rejects a reservation and retries with no extra allocation", async () => {
  await activate();
  await sql("CREATE TRIGGER synthetic_quota BEFORE INSERT ON allocations BEGIN SELECT RAISE(ABORT,'synthetic quota detail'); END");
  const failed = await create(1);
  expect(failed.status).toBe(503);
  expect(await failed.json()).toEqual({ error: "storage_failure" });
  expect(await status()).toMatchObject({ report: { window_count: 0 } });
  expect(await targetCount()).toBe(0);
  await sql("DROP TRIGGER synthetic_quota");
  await created(1);
  expect(await status()).toMatchObject({ report: { window_count: 1 } });
});

it("recovers a reserved identity after target initialization and interrupted finalization", async () => {
  await activate();
  await sql("CREATE TRIGGER synthetic_quota BEFORE UPDATE ON allocations BEGIN SELECT RAISE(ABORT,'synthetic quota detail'); END");
  expect((await create(1)).status).toBe(503);
  const reserved = await sql("SELECT overlay_id,phase FROM allocations");
  expect(reserved).toMatchObject([{ phase: "reserved" }]);
  expect(await status()).toMatchObject({ report: { window_count: 1 } });
  await abortAllDurableObjects();
  await sql("DROP TRIGGER synthetic_quota");
  expect(await created(1)).toBe(reserved[0].overlay_id);
  expect(await sql("SELECT phase FROM allocations")).toEqual([{ phase: "active" }]);
  expect(await targetCount()).toBe(1);
  expect(await status()).toMatchObject({ report: { window_count: 1 } });
});

it("fails closed on corrupt policy state while preserving an allocated publisher", async () => {
  await activate();
  const id = await created(1);
  await sql("UPDATE allocation_policy SET policy_digest='invalid'");
  expect((await create(2)).status).toBe(409);
  expect(await created(1)).toBe(id);
  expect((await publisher(id, credentials(1).write)).status).toBe(200);
  expect(await targetCount()).toBe(1);
  expect(await status()).toMatchObject({ report: { unavailable_reason: "invalid_state" } });
});

it("keeps public-created identities and role capabilities isolated through live delivery", async () => {
  await activate();
  const first = await created(1), second = await created(2);
  expect((await publisher(first, credentials(2).write)).status).toBe(403);
  expect((await publisher(first, credentials(1).read)).status).toBe(403);
  const connected = await exports.default.fetch(`${origin}/api/v1/overlays/${first}/live`, {
    headers: { Upgrade: "websocket", Origin: origin }
  });
  expect(connected.status).toBe(101);
  const socket = connected.webSocket!; socket.accept();
  const snapshot = new Promise<MessageEvent>(resolve => socket.addEventListener("message", resolve, { once: true }));
  socket.send(JSON.stringify({ v: 1, type: "auth", protocolVersion: 2, readCapability: credentials(1).read }));
  expect(JSON.parse(String((await snapshot).data))).toMatchObject({ type: "snapshot", death: null });
  const send = (action: string, body: unknown) => exports.default.fetch(`${origin}/api/v1/overlays/${first}/${action}`, {
    method: action === "session" ? "POST" : "PUT",
    headers: { Authorization: `Bearer ${credentials(1).write}`, "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });
  expect((await send("session", { v: 1, protocolVersion: 2, expectedEpoch: "0", sessionRequestId: "3".repeat(32) })).status).toBe(200);
  const update = new Promise<MessageEvent>(resolve => socket.addEventListener("message", resolve, { once: true }));
  expect((await send("state", { v: 1, protocolVersion: 2, epoch: "1", sessionRequestId: "3".repeat(32), sequence: "1",
    death: { value: "42", availability: "available" } })).status).toBe(200);
  const delivered = String((await update).data);
  expect(JSON.parse(delivered)).toMatchObject({ type: "update", death: { value: "42" } });
  for (const token of [credentials(1).read, credentials(1).write, credentials(2).write]) expect(delivered).not.toContain(token);
  expect(await runInDurableObject(env.OVERLAYS.get(env.OVERLAYS.idFromString(second)), (_, state) =>
    state.storage.sql.exec("SELECT * FROM records WHERE key='death'").toArray())).toEqual([{ key: "death", value: "null" }]);
  socket.close();
});
