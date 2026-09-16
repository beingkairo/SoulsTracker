import { env, exports } from "cloudflare:workers";
import { reset, runInDurableObject, evictDurableObject, abortAllDurableObjects, listDurableObjectIds } from "cloudflare:test";
import { afterEach, expect, it, vi } from "vitest";
import { randomBytes } from "node:crypto";
import { defaultAppearance, verifier } from "../src/protocol";
import type {} from "./authority.test";
import worker from "../src/index";

const id = "11111111111111111111111111111111";
const read = Buffer.from(randomBytes(32)).toString("hex"), write = Buffer.from(randomBytes(32)).toString("hex");
afterEach(() => reset());
async function provision() {
  await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => {
    state.storage.sql.exec("INSERT INTO records (key,value) VALUES ('control',?)", JSON.stringify({
      readVerifier: verifier(id, "read", read), writeVerifier: verifier(id, "write", write),
      epoch: "0", generation: "0", readGeneration: "0", session: null, last: null, rotation: null
    }));
  });
}
function next(socket: WebSocket): Promise<string> {
  return new Promise(resolve => socket.addEventListener("message", event => resolve(String(event.data)), { once: true }));
}
const sessionRequestId = "3".repeat(32);
async function send(action: string, body: unknown, token = write) {
  const result = await exports.default.fetch(`https://overlay.test/api/v1/overlays/${id}/${action}`, {
    method: action === "state" ? "PUT" : "POST",
    headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, body: JSON.stringify(body)
  });
  return { status: result.status, body: await result.json() };
}
const acquire = () => send("session", { v: 1, expectedEpoch: "0", sessionRequestId });
const candidate = (sequence = "1", value = "9007199254740993") => ({ v: 1, epoch: "1", sessionRequestId, sequence,
  death: { value, availability: "available" } });
async function authenticate(socket: WebSocket, token = read) {
  const snapshot = next(socket);
  socket.send(JSON.stringify({ v: 1, type: "auth", readCapability: token }));
  return JSON.parse(await snapshot);
}
async function quiet(socket: WebSocket) {
  const messages: string[] = [];
  const listener = (event: MessageEvent) => { messages.push(String(event.data)); };
  socket.addEventListener("message", listener);
  const barrier = next(socket); socket.send("ping"); await barrier;
  socket.removeEventListener("message", listener);
  expect(messages).toEqual(["pong"]);
}
async function connect() {
  const response = await exports.default.fetch(`https://overlay.test/api/v1/overlays/${id}/live`, {
    headers: { Upgrade: "websocket", Origin: "https://overlay.test" }
  });
  expect(response.status).toBe(101);
  const socket = response.webSocket!;
  socket.accept();
  return socket;
}
it("hydrates an authenticated read socket with empty death and complete default appearance", async () => {
  await provision();
  const socket = await connect();
  const snapshot = next(socket);
  socket.send(JSON.stringify({ v: 1, type: "auth", readCapability: read }));
  expect(JSON.parse(await snapshot)).toEqual({ v: 1, type: "snapshot", death: null, appearance: defaultAppearance });
  socket.close();
});

it("broadcasts committed complete channels once and never broadcasts retries, no-ops or SQL rollback", async () => {
  await provision(); await acquire();
  const socket = await connect(); await authenticate(socket);
  const received: string[] = [];
  socket.addEventListener("message", event => { if (event.data !== "pong") received.push(String(event.data)); });
  const update = next(socket);
  expect((await send("state", candidate())).status).toBe(200);
  expect(JSON.parse(await update)).toEqual({ v: 1, type: "update", death: { ...candidate().death, revision: "1" } });
  expect((await send("state", candidate())).status).toBe(200); await quiet(socket);
  expect((await send("state", candidate("2"))).status).toBe(200); await quiet(socket);
  expect(received).toHaveLength(1);
  await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => {
    state.storage.sql.exec("CREATE TRIGGER fail_control BEFORE UPDATE ON records WHEN NEW.key='control' BEGIN SELECT RAISE(ABORT,'synthetic'); END");
  });
  const { revision: _, ...style } = defaultAppearance;
  const both = { ...candidate("3", "0"), appearance: { ...style, title: "Changed" } };
  expect((await send("state", both)).status).toBe(500); await quiet(socket);
  expect(received).toHaveLength(1);
  await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => { state.storage.sql.exec("DROP TRIGGER fail_control"); });
  const combined = next(socket);
  expect((await send("state", both)).status).toBe(200);
  expect(JSON.parse(await combined)).toEqual({ v: 1, type: "update", death: { ...both.death, revision: "2" },
    appearance: { ...both.appearance, revision: "1" } });
  expect(received).toHaveLength(2);
  socket.close();
});

const closed = (socket: WebSocket): Promise<CloseEvent> => new Promise(resolve => socket.addEventListener("close", resolve, { once: true }));
it("rejects origins and credential-bearing upgrades before object allocation", async () => {
  const before = await listDurableObjectIds(env.OVERLAYS);
  for (const [origin, identity, extra, expected] of [
    ["https://wrong.test", id, {}, 403], ["", id, {}, 403],
    ["https://overlay.test", "0".repeat(32), {}, 404],
    ["https://overlay.test", id, { Cookie: "read=synthetic" }, 400],
    ["https://overlay.test", id, { "Sec-WebSocket-Protocol": "synthetic" }, 400],
    ["https://overlay.test", id, { Authorization: "synthetic" }, 400]
  ] as const) {
    const result = await exports.default.fetch(`https://overlay.test/api/v1/overlays/${identity}/live`, {
      headers: { Upgrade: "websocket", Origin: origin, ...extra }
    });
    expect(result.status).toBe(expected); await result.text();
  }
  const request = new Request(`https://overlay.test/api/v1/overlays/${id}/live`, { headers: { Upgrade: "websocket", Origin: "https://overlay.test" } });
  expect((await worker.fetch(request, { ...env, BROWSER_ORIGIN: undefined })).status).toBe(403);
  expect(await listDurableObjectIds(env.OVERLAYS)).toEqual(before);
});

it("default-denies malformed configured browser origins", async () => {
  for (const origin of ["null", "http://overlay.test", "https://overlay.test/path", "https://overlay.test/"]) {
    const request = new Request(`https://overlay.test/api/v1/overlays/${id}/live`, { headers: { Upgrade: "websocket", Origin: origin } });
    const allocate = vi.fn(() => { throw new Error("must not allocate"); });
    const result = await worker.fetch(request, { ...env, BROWSER_ORIGIN: origin, OVERLAYS: { getByName: allocate } as unknown as typeof env.OVERLAYS });
    expect(result.status).toBe(403); await result.text();
    expect(allocate).not.toHaveBeenCalled();
  }
});

it("sends no state before auth and rejects role, cross-ID, malformed and write inputs without mutation", async () => {
  await provision(); await acquire();
  const rows = () => runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => state.storage.sql.exec("SELECT * FROM records ORDER BY key").toArray());
  const before = await rows();
  const auth = JSON.stringify({ v: 1, type: "auth", readCapability: read });
  for (const message of ["{", "null", auth.replace('"v":1', '"v":1,"v":1'), auth.replace('"v":1', '"v":1.0'),
    auth.replace('"v":1', '"v":2'), auth.replace('"type":"auth"', '"type":"write"'),
    auth.replace('"v":1', '"v":1,"writeCapability":"synthetic"'), auth + " ".repeat(8193),
    auth + "界".repeat(3000), auth.replace(read, "\\ud800"), new Uint8Array([255]).buffer]) {
    const socket = await connect(); await quiet(socket);
    const end = closed(socket); socket.send(message);
    expect((await end).code).toBe(4400);
  }
  for (const token of [write, Buffer.from(randomBytes(32)).toString("hex")]) {
    const socket = await connect(); const end = closed(socket);
    socket.send(JSON.stringify({ v: 1, type: "auth", readCapability: token }));
    expect((await end).code).toBe(4401);
  }
  // A read verifier copied from a different identity is not valid here.
  await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => {
    const control = JSON.parse(state.storage.sql.exec<{ value: string }>("SELECT value FROM records WHERE key='control'").one().value);
    control.readVerifier = verifier("2".repeat(32), "read", read);
    state.storage.sql.exec("UPDATE records SET value=? WHERE key='control'", JSON.stringify(control));
  });
  const cross = await connect(); const end = closed(cross);
  cross.send(auth); expect((await end).code).toBe(4401);
  await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => {
    state.storage.sql.exec("UPDATE records SET value=? WHERE key='control'", before.find(row => row.key === "control")!.value);
  });
  const socket = await connect(); await authenticate(socket);
  const denied = closed(socket); socket.send(JSON.stringify(candidate())); expect((await denied).code).toBe(4400);
  expect(await rows()).toEqual(before);
});

it("caps pending and authenticated sockets separately", async () => {
  await provision();
  const first = await connect(), second = await connect(), excess = await connect();
  expect((await closed(excess)).code).toBe(4429);
  await authenticate(first); await authenticate(second);
  const readers = [first, second];
  for (let i = 0; i < 6; i++) { const socket = await connect(); await authenticate(socket); readers.push(socket); }
  const ninth = await connect(); const end = closed(ninth);
  ninth.send(JSON.stringify({ v: 1, type: "auth", readCapability: read }));
  expect((await end).code).toBe(4429);
  readers.forEach(socket => socket.close());
});

it("expires pending authentication despite automatic pings", async () => {
  await provision(); const socket = await connect();
  const end = closed(socket);
  const ping = setInterval(() => { if (socket.readyState === WebSocket.OPEN) socket.send("ping"); }, 100);
  try { expect((await end).code).toBe(4401); } finally { clearInterval(ping); }
}, 10000);

for (const mode of ["read", "write", "both"]) it(`rotates ${mode} with connected and pending readers, retaining latest retry behavior`, async () => {
  await provision(); await acquire(); await send("state", candidate());
  const socket = await connect(); await authenticate(socket);
  const pending = await connect();
  const newRead = Buffer.from(randomBytes(32)).toString("hex"), newWrite = Buffer.from(randomBytes(32)).toString("hex");
  const rotation = { v: 1, rotationId: "a".repeat(32), expectedGeneration: "0",
    ...(mode !== "write" ? { readCapability: newRead } : {}), ...(mode !== "read" ? { writeCapability: newWrite } : {}) };
  const end = mode !== "write" ? closed(socket) : undefined;
  const result = await send("credentials", rotation); expect(result.status).toBe(200);
  if (end) expect((await end).code).toBe(4401); else await quiet(socket);
  if (mode !== "write") {
    const denied = closed(pending); pending.send(JSON.stringify({ v: 1, type: "auth", readCapability: read }));
    expect((await denied).code).toBe(4401);
  } else { await authenticate(pending); pending.close(); }
  const fresh = await connect(); const snapshot = await authenticate(fresh, mode === "write" ? read : newRead);
  expect(snapshot.death.revision).toBe("1");
  const currentWrite = mode === "read" ? write : newWrite;
  expect((await send("session", { v: 1, expectedEpoch: "2", sessionRequestId }, currentWrite)).status).toBe(200);
  expect(await send("credentials", rotation, currentWrite)).toEqual(result);
  await quiet(fresh);
  const update = next(fresh);
  expect((await send("state", { ...candidate("1", "0"), epoch: "3" }, currentWrite)).status).toBe(200);
  expect(JSON.parse(await update).death.value).toBe("0");
  fresh.close(); if (mode === "write") socket.close();
});

it("restores durable latest hydration after local eviction and forced object restart", async () => {
  await provision(); await acquire(); await send("state", candidate());
  await evictDurableObject(env.OVERLAYS.getByName(id));
  const first = await connect(); expect((await authenticate(first)).death.value).toBe(candidate().death.value); first.close();
  await abortAllDurableObjects();
  const second = await connect(); expect((await authenticate(second)).death.revision).toBe("1"); second.close();
});

it("serializes hydration with racing writes without a subscribe gap", async () => {
  await provision(); await acquire();
  const socket = await connect(); const received: any[] = [];
  socket.addEventListener("message", event => { if (event.data !== "pong") received.push(JSON.parse(String(event.data))); });
  const hydration = authenticate(socket);
  const writes = Array.from({ length: 8 }, (_, i) => send("state", candidate(String(i + 1), String(i + 1))));
  await Promise.all([hydration, ...writes]);
  const barrier = next(socket); socket.send("ping"); await barrier;
  expect(received[0].type).toBe("snapshot");
  expect(received.at(-1).death.value).toBe("8");
  socket.close();
});

it("reconstructs authenticated attachments and filters revoked generations after local hibernation", async () => {
  await provision(); await acquire();
  const socket = await connect(); await authenticate(socket);
  const stub = env.OVERLAYS.getByName(id);
  await evictDurableObject(stub, { webSockets: "hibernate" });
  const update = next(socket); expect((await send("state", candidate())).status).toBe(200);
  expect(JSON.parse(await update).death.revision).toBe("1");
  await runInDurableObject(stub, (_, state) => {
    const sockets = state.getWebSockets(); expect(sockets).toHaveLength(1);
    expect(sockets[0].deserializeAttachment()).toEqual({ phase: "authenticated", generation: "0" });
    const control = JSON.parse(state.storage.sql.exec<{ value: string }>("SELECT value FROM records WHERE key='control'").one().value);
    control.readGeneration = "1";
    state.storage.sql.exec("UPDATE records SET value=? WHERE key='control'", JSON.stringify(control));
  });
  await evictDurableObject(stub, { webSockets: "hibernate" });
  const end = closed(socket);
  expect((await send("state", candidate("2", "0"))).status).toBe(200);
  expect((await end).code).toBe(4401);
});

it("expires an injected persisted pending deadline on constructor wake", async () => {
  await provision(); const socket = await connect(); const stub = env.OVERLAYS.getByName(id);
  await authenticate(socket);
  await runInDurableObject(stub, (_, state) => {
    const server = state.getWebSockets()[0];
    server.serializeAttachment({ phase: "pending", id, deadline: Date.now() - 1 });
  });
  const end = closed(socket);
  await evictDurableObject(stub, { webSockets: "hibernate" });
  await acquire(); expect((await end).code).toBe(4401);
});

it("keeps capabilities out of socket output, attachments, storage and application logs", async () => {
  await provision();
  const logs = [vi.spyOn(console, "log"), vi.spyOn(console, "warn"), vi.spyOn(console, "error"), vi.spyOn(console, "info"), vi.spyOn(console, "debug")];
  try {
    const socket = await connect(); const snapshot = await authenticate(socket);
    const serialized = await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => JSON.stringify({
      records: state.storage.sql.exec("SELECT * FROM records").toArray(),
      attachments: state.getWebSockets().map(s => s.deserializeAttachment())
    }));
    for (const token of [read, write]) {
      expect(serialized.includes(token)).toBe(false);
      expect(JSON.stringify(snapshot).includes(token)).toBe(false);
    }
    const end = closed(socket); socket.send(JSON.stringify({ v: 1, type: "auth", readCapability: read }));
    const event = await end; expect(event.code).toBe(4400);
    expect(event.reason.includes(read) || event.reason.includes(write)).toBe(false);
    for (const log of logs) expect(log).not.toHaveBeenCalled();
  } finally { logs.forEach(log => log.mockRestore()); }
});

it("a failed read rotation leaves connected readers authorized", async () => {
  await provision(); await acquire();
  const socket = await connect(); await authenticate(socket);
  await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => {
    state.storage.sql.exec("CREATE TRIGGER fail_rotation BEFORE UPDATE ON records WHEN NEW.key='control' BEGIN SELECT RAISE(ABORT,'synthetic'); END");
  });
  expect((await send("credentials", { v: 1, rotationId: "a".repeat(32), expectedGeneration: "0",
    readCapability: Buffer.from(randomBytes(32)).toString("hex") })).status).toBe(500);
  await quiet(socket);
  await runInDurableObject(env.OVERLAYS.getByName(id), (_, state) => { state.storage.sql.exec("DROP TRIGGER fail_rotation"); });
  const update = next(socket); expect((await send("state", candidate())).status).toBe(200);
  expect(JSON.parse(await update).death.revision).toBe("1"); socket.close();
});
