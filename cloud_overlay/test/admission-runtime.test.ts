import { env } from "cloudflare:workers";
import { reset, runInDurableObject } from "cloudflare:test";
import { afterEach, expect, it, vi } from "vitest";
import worker from "../src/index";
import { verifier } from "../src/protocol";
import type {} from "./authority.test";

const id = "1".repeat(32), other = "2".repeat(32), write = "3".repeat(64), read = "4".repeat(64);
afterEach(() => reset());
const request = (identity = id, token = write) => new Request(`https://overlay.test/api/v1/overlays/${identity}/publisher`, {
  headers: { Authorization: `Bearer ${token}` }
});
async function provision() {
  await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => {
    state.storage.sql.exec("INSERT INTO records (key,value) VALUES ('control',?)", JSON.stringify({
      readVerifier: verifier(id, "read", read), writeVerifier: verifier(id, "write", write),
      epoch: "0", generation: "0", readGeneration: "0", session: null, last: null, rotation: null
    }));
  });
}
const state = () => runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => ({
  rows: state.storage.sql.exec("SELECT * FROM records ORDER BY key").toArray(),
  sockets: state.getWebSockets().map(socket => socket.deserializeAttachment())
}));

it("charges valid-shaped wrong capabilities while retaining real verifier denial and unchanged SQLite state", async () => {
  await provision();
  const before = await state();
  const limit = vi.fn(env.PUBLISHER_RATE_LIMITER.limit.bind(env.PUBLISHER_RATE_LIMITER));
  const result = await worker.fetch(request(id, read), { ...env, PUBLISHER_RATE_LIMITER: { limit } });
  expect(result.status).toBe(403); expect(await result.json()).toEqual({ error: "forbidden" });
  expect(limit).toHaveBeenCalledExactlyOnceWith({ key: `overlay-v1:${id}` });
  expect(await state()).toEqual(before);
  expect((await worker.fetch(request(), env)).status).toBe(200);
});

for (const kind of ["publisher", "live"] as const) {
  it(`uses the real local ${kind} binding to deny before namespace access without changing stored state or readers`, async () => {
    await provision(); await worker.fetch(request(), env);
    const liveRequest = () => new Request(`https://overlay.test/api/v1/overlays/${id}/live`, {
      headers: { Upgrade: "websocket", Origin: "https://overlay.test" }
    });
    const connected = await worker.fetch(liveRequest(), env);
    expect(connected.status).toBe(101);
    const socket = connected.webSocket!; socket.accept();
    const snapshot = new Promise(resolve => socket.addEventListener("message", resolve, { once: true }));
    socket.send(JSON.stringify({ v: 1, type: "auth", readCapability: read })); await snapshot;
    const before = await state();
    const binding = kind === "publisher" ? env.PUBLISHER_RATE_LIMITER : env.LIVE_RATE_LIMITER;
    // Bounded local-emulator saturation, not an assertion of a distributed hard cap.
    let denied = false;
    for (let attempt = 0; attempt < 300; attempt++) {
      if (!(await binding.limit({ key: `overlay-v1:${id}` })).success) { denied = true; break; }
    }
    expect(denied).toBe(true);
    const getByName = vi.fn(() => { throw new Error("must not access namespace"); });
    const result = await worker.fetch(kind === "publisher" ? request() : liveRequest(), {
      ...env, OVERLAYS: { getByName } as unknown as typeof env.OVERLAYS
    });
    expect(result.status).toBe(429); expect(result.headers.get("Retry-After")).toBe("60");
    expect(getByName).not.toHaveBeenCalled();
    expect(await state()).toEqual(before);
    expect((await binding.limit({ key: `overlay-v1:${other}` })).success).toBe(true);
    const independent = kind === "publisher" ? env.LIVE_RATE_LIMITER : env.PUBLISHER_RATE_LIMITER;
    expect((await independent.limit({ key: `overlay-v1:${id}` })).success).toBe(true);
    const pong = new Promise<string>(resolve => socket.addEventListener("message", event => resolve(String(event.data)), { once: true }));
    socket.send("ping"); expect(await pong).toBe("pong"); socket.close();
  });
}
