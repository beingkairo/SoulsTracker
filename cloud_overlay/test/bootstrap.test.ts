import { env } from "cloudflare:workers";
import { abortAllDurableObjects, reset, runInDurableObject } from "cloudflare:test";
import { afterEach, expect, it, vi } from "vitest";
import { OverlayState } from "../src/overlay-state";
import { verifier } from "../src/protocol";
import worker from "../src/index";
import type {} from "./authority.test";

const id = "1".repeat(32), read = "2".repeat(64), write = "3".repeat(64);
const bootstrap = { v: 1, overlayId: id, readVerifier: verifier(id, "read", read), writeVerifier: verifier(id, "write", write) };
afterEach(() => reset());
it("rejects alternate request origins before touching a provisioned object", async () => {
  const getByName = vi.fn(() => ({ fetch: async () => new Response(null, { status: 200 }) }));
  for (const origin of ["https://overlay.beingkairo.com.evil.test", "https://arbitrary.test", "http://overlay.beingkairo.com"]) {
    const result = await worker.fetch(new Request(`${origin}/api/v1/overlays/${id}/publisher`, {
      headers: { Authorization: `Bearer ${write}` }
    }), { ...env, BROWSER_ORIGIN: "https://overlay.beingkairo.com", OVERLAYS: { getByName } as unknown as typeof env.OVERLAYS });
    expect(result.status).not.toBe(200);
    expect([400, 403]).toContain(result.status);
  }
  expect(getByName).not.toHaveBeenCalled();
});
it("never adopts orphaned channels as a new identity", async () => {
  await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => {
    state.storage.sql.exec("INSERT INTO records (key,value) VALUES ('death','null')");
  });
  const result = await request();
  expect(result.status).toBe(403);
  expect(result.rows).toEqual([{ key: "death", value: "null" }]);
});
async function request(config: unknown = bootstrap, token = write) {
  return runInDurableObject(env.OVERLAYS.getByName(id), async (_, state) => {
    const instance = new OverlayState(state, { ...env, ...{ BOOTSTRAP: config } });
    const result = await instance.fetch(new Request(`https://overlay.test/api/v1/overlays/${id}/publisher`, {
      headers: { Authorization: `Bearer ${token}` }
    }));
    return { status: result.status, body: await result.json(), rows: state.storage.sql.exec("SELECT key,value FROM records ORDER BY key").toArray() };
  });
}
it("initializes only the exact write-authorized identity with empty channels", async () => {
  const result = await request();
  expect(result.status).toBe(200);
  expect(result.rows).toHaveLength(3);
  const control = JSON.parse(String(result.rows.find(row => row.key === "control")!.value));
  expect(control).toEqual({ readVerifier: bootstrap.readVerifier, writeVerifier: bootstrap.writeVerifier,
    epoch: "0", generation: "0", readGeneration: "0", session: null, last: null, rotation: null });
});

it("fails closed for absent malformed wrong-ID role and credential configuration without mutation", async () => {
  for (const config of [null, {}, "{}", { ...bootstrap, v: 2 }, { ...bootstrap, extra: true },
    { ...bootstrap, overlayId: "4".repeat(32) }, { ...bootstrap, readVerifier: "invalid" },
    { ...bootstrap, writeVerifier: verifier(id, "read", write) },
    { ...bootstrap, writeVerifier: verifier("4".repeat(32), "write", write) },
    { ...bootstrap, readVerifier: verifier(id, "read", write) }]) {
    const result = await request(config);
    expect(result.status).toBe(403); expect(result.rows).toEqual([]);
  }
  for (const token of [read, "5".repeat(64)]) {
    const result = await request(bootstrap, token);
    expect(result.status).toBe(403); expect(result.rows).toEqual([]);
  }
});

it("provisions exactly once concurrently and ignores replacement or removed bindings on restart", async () => {
  const results = await Promise.all(Array.from({ length: 8 }, () => request()));
  expect(results.every(result => result.status === 200)).toBe(true);
  expect(results.every(result => JSON.stringify(result.rows) === JSON.stringify(results[0].rows))).toBe(true);
  await abortAllDurableObjects();
  for (const config of [null, { ...bootstrap, writeVerifier: verifier(id, "write", read) }])
    expect((await request(config)).rows).toEqual(results[0].rows);
});

it("rolls back control insertion if initial channel storage fails", async () => {
  await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => {
    state.storage.sql.exec("CREATE TRIGGER fail_appearance BEFORE INSERT ON records WHEN NEW.key='appearance' BEGIN SELECT RAISE(ABORT,'synthetic'); END");
  });
  const failed = await request();
  expect(failed.status).toBe(500); expect(failed.rows).toEqual([]);
  await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => state.storage.sql.exec("DROP TRIGGER fail_appearance"));
  expect((await request()).status).toBe(200);
});

it("preserves rotations sessions and channels across replay restart and bootstrap removal with read hydration", async () => {
  await request();
  const replacement = "6".repeat(64), sessionRequestId = "7".repeat(32);
  await runInDurableObject(env.OVERLAYS.getByName(id), async (_, state) => {
    const instance = new OverlayState(state, { ...env, ...{ BOOTSTRAP: bootstrap } });
    const send = async (action: string, body: unknown, token: string) => {
      const result = await instance.fetch(new Request(`https://overlay.test/api/v1/overlays/${id}/${action}`, {
        method: action === "state" ? "PUT" : "POST",
        headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, body: JSON.stringify(body)
      }));
      expect(result.status).toBe(200); await result.text();
    };
    await send("credentials", { v: 1, expectedGeneration: "0", rotationId: "8".repeat(32), writeCapability: replacement }, write);
    await send("session", { v: 1, expectedEpoch: "1", sessionRequestId }, replacement);
    await send("state", { v: 1, epoch: "2", sessionRequestId, sequence: "1", death: { value: "9", availability: "available" } }, replacement);
  });
  const before = await request(null, replacement);
  await abortAllDurableObjects();
  expect((await request()).status).toBe(403);
  expect((await request(bootstrap, replacement)).rows).toEqual(before.rows);
  expect((await request(null, replacement)).rows).toEqual(before.rows);
  const response = await env.OVERLAYS.getByName(id).fetch(`https://overlay.test/api/v1/overlays/${id}/live`, { headers: { Upgrade: "websocket" } });
  const socket = response.webSocket!; socket.accept();
  const snapshot = new Promise<string>(resolve => socket.addEventListener("message", event => resolve(String(event.data)), { once: true }));
  socket.send(JSON.stringify({ v: 1, type: "auth", readCapability: read }));
  expect(JSON.parse(await snapshot).death).toEqual({ revision: "1", value: "9", availability: "available" });
  socket.close();
});
