import { env, exports } from "cloudflare:workers";
import { abortAllDurableObjects, listDurableObjectIds, reset, runInDurableObject, evictDurableObject } from "cloudflare:test";
import { afterEach, expect, it, vi } from "vitest";
import { createHash, randomBytes } from "node:crypto";
import type { Env } from "../src/index";
import worker from "../src/index";
import corpus from "../../tests/fixtures/hosted-overlay/contracts.json";

declare global {
  namespace Cloudflare {
    interface Env extends importEnv {}
    interface GlobalProps { mainModule: typeof import("../src/index"); durableNamespaces: "OverlayState"; }
  }
}
type importEnv = Env;
const id = "11111111111111111111111111111111";
const otherId = "22222222222222222222222222222222";
const write = Buffer.from(randomBytes(32)).toString("hex"), read = Buffer.from(randomBytes(32)).toString("hex");
const hash = (text: string) => createHash("sha256").update(text).digest("hex");
const verifier = (role: string, capability: string, identity = id) => hash(`overlay-v1:${identity}:${role}:${capability}`);
const stub = () => env.OVERLAYS.getByName(id);
async function provision(identity = id) {
  await runInDurableObject(env.OVERLAYS.getByName(identity), (_instance, state) => {
    state.storage.sql.exec("INSERT INTO records (key, value) VALUES ('control', ?)", JSON.stringify({
      readVerifier: verifier("read", read, identity), writeVerifier: verifier("write", write, identity),
      epoch: "0", generation: "0", readGeneration: "0", session: null, last: null, rotation: null
    }));
  });
}
const request = async (route = "publisher", method = "GET", body?: unknown, capability = write, identity = id) => {
  const response = await exports.default.fetch(`https://overlay.test/api/v1/overlays/${identity}/${route}`, {
    method, headers: { Authorization: `Bearer ${capability}`, "Content-Type": "application/json" },
    ...(body === undefined ? {} : { body: JSON.stringify(body) })
  });
  return new Response(await response.text(), response);
};
async function stored() {
  return runInDurableObject(stub(), (_instance, state) => state.storage.sql.exec("SELECT key, value FROM records ORDER BY key").toArray());
}
afterEach(() => reset());

async function replaceRecord(key: string, transform: (value: any) => unknown) {
  await runInDurableObject(stub(), (_instance, state) => {
    const current = JSON.parse(state.storage.sql.exec<{ value: string }>("SELECT value FROM records WHERE key = ?", key).one().value);
    state.storage.sql.exec("UPDATE records SET value = ? WHERE key = ?", JSON.stringify(transform(current)), key);
  });
}

it("rejects malformed routes and unconfigured admission before namespace access", async () => {
  const before = (await listDurableObjectIds(env.OVERLAYS)).map(id => id.toString());
  for (const path of ["/", `/api/v1/overlays/${id}/live`, `/api/v1/overlays/${id}/publisher?token=synthetic`,
    `/api/v1/overlays/${id}0/publisher`, "/api/v1/overlays/%31/publisher", `/api/v1/overlays/${id}/publisher/`]) {
    const result = await exports.default.fetch(`https://overlay.test${path}`);
    expect(result.status).toBe(404); await result.text();
  }
  const valid = new Request(`https://overlay.test/api/v1/overlays/${id}/publisher`, { headers: { Authorization: `Bearer ${write}` } });
  for (const ids of [[], Array(17).fill(id), [id, "invalid"], "invalid", null]) {
    const result = await worker.fetch(valid.clone(), { ...env, PROVISIONED_IDS: ids as string[] });
    expect(result.status).toBe(404);
  }
  expect((await listDurableObjectIds(env.OVERLAYS)).map(id => id.toString())).toEqual(before);
});

it("denies cross-identity, swapped-role, missing and malformed credentials on every publisher endpoint", async () => {
  await provision(); await acquire(); await publish();
  await runInDurableObject(env.OVERLAYS.getByName(otherId), (_instance, state) => {
    state.storage.sql.exec("INSERT INTO records (key, value) VALUES ('control', ?)", JSON.stringify({
      readVerifier: verifier("read", read, otherId), writeVerifier: verifier("write", Buffer.from(randomBytes(32)).toString("hex"), otherId),
      epoch: "0", generation: "0", readGeneration: "0", session: null, last: null, rotation: null
    }));
  });
  const before = await stored();
  for (const [route, method, body] of [["publisher", "GET", undefined], ["session", "POST", acquisition],
    ["state", "PUT", candidate("2")], ["credentials", "POST", { v: 1, rotationId: "8".repeat(32), expectedGeneration: "0", readCapability: read }]] as const) {
    for (const token of [read, "", "bad", `${write}, ${write}`]) {
      const result = await request(route, method, body, token);
      expect(result.status).toBe(403);
      expect(result.headers.get("Cache-Control")).toBe("no-store");
      expect(result.headers.has("Access-Control-Allow-Origin")).toBe(false);
      expect(result.headers.has("Set-Cookie")).toBe(false);
      expect(await result.text()).toBe('{"error":"forbidden"}');
    }
    expect((await request(route, method, body, write, otherId)).status).toBe(403);
  }
  expect(await stored()).toEqual(before);
});

it("rejects wrong methods, plaintext transport and cookie-only authority", async () => {
  await provision(); await request(); const before = await stored();
  for (const [route, correct] of [["publisher", "GET"], ["session", "POST"], ["state", "PUT"], ["credentials", "POST"]]) {
    for (const method of ["GET", "POST", "PUT", "DELETE", "OPTIONS", "PATCH"]) {
      if (method === correct) continue;
      expect((await request(route, method)).status).toBe(405);
    }
  }
  const result = await exports.default.fetch(`http://overlay.test/api/v1/overlays/${id}/publisher`, { headers: { Authorization: `Bearer ${write}` } });
  expect(result.status).toBe(400); await result.text();
  const cookie = await exports.default.fetch(`https://overlay.test/api/v1/overlays/${id}/publisher`, { headers: { Cookie: `write=${write}` } });
  expect(cookie.status).toBe(403); await cookie.text();
  expect(await stored()).toEqual(before);
});

async function raw(body: BodyInit, contentType = "application/json", extraHeaders = {}) {
  const result = await exports.default.fetch(`https://overlay.test/api/v1/overlays/${id}/state`, { method: "PUT",
    headers: { Authorization: `Bearer ${write}`, "Content-Type": contentType, ...extraHeaders }, body });
  return new Response(await result.text(), result);
}

it("bounds raw bodies, rejects duplicates and malformed UTF-8 without writes or secret output", async () => {
  await provision(); await acquire(); const before = await stored();
  const logs = [vi.spyOn(console, "log"), vi.spyOn(console, "warn"), vi.spyOn(console, "error"), vi.spyOn(console, "info"), vi.spyOn(console, "debug")];
  try {
    const json = JSON.stringify(candidate());
    for (const body of ["{", "null", "[]", json.replace('"v":1', '"v":1,"v":1'),
      json.replace('"v":1', '"v":1,"\\u0076":1'), json.replace('"v":1', '"v":1.0'),
      json.replace('"value":', '"value":"0","value":'), json.replace('"v":1', '"v":1e0'),
      new Uint8Array([0xc0, 0xaf]), '\ufeff' + json]) expect((await raw(body)).status).toBe(400);
    expect((await raw(json, "text/plain")).status).toBe(415);
    expect((await raw(json, "application/json", { "Content-Encoding": "gzip" })).status).toBe(415);
    expect((await raw(json + " ".repeat(8193))).status).toBe(413);
    expect((await raw(json + "界".repeat(3000))).status).toBe(413);
    expect(await stored()).toEqual(before);
    expect((await raw(json + " ".repeat(8192 - new TextEncoder().encode(json).length))).status).toBe(200);
    for (const spy of logs) expect(spy).not.toHaveBeenCalled();
  } finally { logs.forEach(spy => spy.mockRestore()); }
});

it("preserves the accepted complete-channel, Int64 and Unicode validation corpus", async () => {
  await provision(); await acquire(); const before = await stored();
  const invalid: unknown[] = [ { ...candidate(), v: 2 }, { ...candidate(), death: null }, { ...candidate(), appearance: null },
    { ...candidate(), death: { ...candidate().death, revision: "0" } }, { ...candidate(), source: "synthetic" } ];
  const { death: _death, ...base } = candidate();
  invalid.push(base);
  for (const field of ["epoch", "sequence"]) for (const value of corpus.invalidDecimals) invalid.push({ ...candidate(), [field]: value });
  for (const [field, values] of Object.entries(corpus.invalidDeath)) {
    if (field === "revision") continue;
    for (const value of values) invalid.push({ ...candidate(), death: { ...candidate().death, [field]: value } });
  }
  for (const [field, values] of Object.entries(corpus.invalidAppearance)) for (const value of values)
    invalid.push({ ...base, appearance: { ...style(), [field]: value } });
  for (const field of ["title", "fontFamily"]) for (const sample of corpus.invalidUnicode)
    invalid.push({ ...base, appearance: { ...style(), [field]: String.fromCharCode(...sample.codeUnits) } });
  for (const key of Object.keys(style())) { const appearance: Record<string, unknown> = { ...style() }; delete appearance[key]; invalid.push({ ...base, appearance }); }
  for (const key of corpus.forbiddenFields) invalid.push({ ...base, appearance: { ...style(), [key]: "synthetic" } });
  for (const body of invalid) expect((await publish(body)).status).toBe(400);
  expect(await stored()).toEqual(before);
  expect((await publish(candidate("9223372036854775807", "9223372036854775807"))).status).toBe(200);
  expect((await publish(candidate("9223372036854775808"))).status).toBe(400);
});

it("rolls back channel changes when the actual SQLite control write fails and permits a lost-response retry", async () => {
  await provision(); await acquire(); const before = await stored();
  await runInDurableObject(stub(), (_instance, state) => {
    state.storage.sql.exec("CREATE TRIGGER fail_control BEFORE UPDATE ON records WHEN NEW.key = 'control' BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END");
  });
  const both = { ...candidate(), appearance: { ...style(), title: "Changed" } };
  const failed = await publish(both);
  expect(failed.status).toBe(500);
  expect(await failed.text()).toBe('{"error":"storage_failure"}');
  expect(await stored()).toEqual(before);
  await runInDurableObject(stub(), (_instance, state) => { state.storage.sql.exec("DROP TRIGGER fail_control"); });
  // Consume and discard the first successful acknowledgement, then restart.
  const lost = await publish(both); expect(lost.status).toBe(200); await lost.text();
  const committed = await stored();
  await abortAllDurableObjects();
  expect((await publish(both)).status).toBe(200);
  expect(await stored()).toEqual(committed);
});

for (const channel of ["death", "appearance"]) {
  it(`fails ${channel} revision overflow atomically without wrapping`, async () => {
    await provision(); await acquire(); await publish();
    await replaceRecord(channel, value => ({ ...value, revision: "9223372036854775807" }));
    const before = await stored();
    const result = await publish({ ...candidate("2", "0"), appearance: { ...style(), title: "Changed" } });
    expect(result.status).toBe(409);
    expect(await result.json()).toEqual({ error: "counter_exhausted" });
    expect(await stored()).toEqual(before);
  });
}

it("fails session and rotation counter exhaustion without changing any state", async () => {
  await provision(); await request();
  for (const field of ["epoch", "generation", "readGeneration"]) {
    await replaceRecord("control", value => ({ ...value, epoch: "0", generation: "0", readGeneration: "0", [field]: "9223372036854775807" }));
    const before = await stored();
    if (field === "epoch") expect((await acquire({ ...acquisition, expectedEpoch: "9223372036854775807" })).status).toBe(409);
    const result = await request("credentials", "POST", { v: 1, rotationId: "8".repeat(32),
      expectedGeneration: field === "generation" ? "9223372036854775807" : "0", readCapability: Buffer.from(randomBytes(32)).toString("hex") });
    expect(result.status).toBe(409);
    expect(await result.json()).toEqual({ error: "counter_exhausted" });
    expect(await stored()).toEqual(before);
  }
});

it("emits a committed change result only once and does not write unchanged channels", async () => {
  await provision(); await acquire();
  const results: Array<{ changed: unknown }> = [];
  await runInDurableObject(stub(), (_instance, state) => {
    const transaction = state.storage.transactionSync.bind(state.storage);
    vi.spyOn(state.storage, "transactionSync").mockImplementation(callback => {
      const result = transaction(callback);
      results.push(result as { changed: unknown });
      return result;
    });
  });
  try {
    const first = await publish(); expect(first.status).toBe(200);
    expect(results[0].changed).toEqual({ v: 1, type: "update", death: { revision: "1", ...candidate().death } });
    const ack = await first.json();
    const retry = await publish(); expect(await retry.json()).toEqual(ack);
    expect(results[1].changed).toBeNull();
    await runInDurableObject(stub(), (_instance, state) => {
      state.storage.sql.exec("CREATE TRIGGER deny_channel_write BEFORE UPDATE ON records WHEN NEW.key != 'control' BEGIN SELECT RAISE(ABORT, 'unexpected channel write'); END");
    });
    expect(await (await publish(candidate("2"))).json()).toMatchObject({ changed: [], death: { revision: "1" } });
    expect(results[2].changed).toBeNull();
  } finally {
    await runInDurableObject(stub(), (_instance, state) => { vi.mocked(state.storage.transactionSync).mockRestore(); });
  }
});

it("serializes conflicting same-sequence writes and accepts decreasing totals at higher sequence", async () => {
  await provision(); await acquire();
  const raced = await Promise.all([publish(candidate("10", "100")), publish(candidate("10", "200"))]);
  expect(raced.map(r => r.status).sort()).toEqual([200, 409]);
  expect(await (await publish(candidate("11", "0"))).json()).toMatchObject({ sequence: "11", death: { revision: "2" } });
  expect((await publish(candidate("10", "9223372036854775807"))).status).toBe(409);
  const identical = await Promise.all([publish(candidate("12", "1")), publish(candidate("12", "1"))]);
  expect(identical.map(r => r.status)).toEqual([200, 200]);
  expect(await identical[0].json()).toEqual(await identical[1].json());
  await acquire({ ...acquisition, expectedEpoch: "1", sessionRequestId: "9".repeat(32) });
  expect((await publish(candidate("13", "999"))).status).toBe(409);
  expect(await (await request()).json()).toMatchObject({ epoch: "2", death: { revision: "3" } });
});

it("rolls back credential rotation on storage failure and fences racing old writes", async () => {
  await provision(); await acquire(); await publish();
  const before = await stored();
  const nextWrite = Buffer.from(randomBytes(32)).toString("hex");
  const rotate = { v: 1, rotationId: "a".repeat(32), expectedGeneration: "0", writeCapability: nextWrite };
  await runInDurableObject(stub(), (_instance, state) => {
    state.storage.sql.exec("CREATE TRIGGER fail_rotation BEFORE UPDATE ON records WHEN NEW.key = 'control' BEGIN SELECT RAISE(ABORT, 'synthetic'); END");
  });
  expect((await request("credentials", "POST", rotate)).status).toBe(500);
  expect(await stored()).toEqual(before);
  expect((await request()).status).toBe(200);
  expect((await request("publisher", "GET", undefined, nextWrite)).status).toBe(403);
  await runInDurableObject(stub(), (_instance, state) => { state.storage.sql.exec("DROP TRIGGER fail_rotation"); });
  const [rotated, oldWrite] = await Promise.all([request("credentials", "POST", rotate), publish(candidate("2", "0"))]);
  expect(rotated.status).toBe(200);
  expect([200, 403]).toContain(oldWrite.status);
  const committed = await stored();
  expect((await publish(candidate("3", "999"))).status).toBe(403);
  expect((await acquire()).status).toBe(403);
  await abortAllDurableObjects();
  expect((await request("credentials", "POST", rotate, nextWrite)).status).toBe(200);
  expect(await stored()).toEqual(committed);
});

it("rejects session and rotation schema errors and capabilities reused across roles", async () => {
  await provision(); await request(); const before = await stored();
  for (const body of [null, {}, { ...acquisition, v: 2 }, { ...acquisition, expectedEpoch: "01" },
    { ...acquisition, sessionRequestId: "wrong" }, { ...acquisition, total: "1" }])
    expect((await request("session", "POST", body)).status).toBe(400);
  const rotation = { v: 1, rotationId: "b".repeat(32), expectedGeneration: "0" };
  const same = Buffer.from(randomBytes(32)).toString("hex");
  for (const body of [rotation, { ...rotation, v: 2, readCapability: same }, { ...rotation, writeCapability: null },
    { ...rotation, writeCapability: "a".repeat(63) }, { ...rotation, readCapability: write },
    { ...rotation, writeCapability: read }, { ...rotation, readCapability: read }, { ...rotation, writeCapability: write },
    { ...rotation, readCapability: same, writeCapability: same }, { ...rotation, readCapability: same, unexpected: true }])
    expect((await request("credentials", "POST", body)).status).toBe(400);
  expect(await stored()).toEqual(before);
  // A verifier moved into the other role must not gain write authority.
  await replaceRecord("control", control => ({ ...control, writeVerifier: control.readVerifier }));
  expect((await request("publisher", "GET", undefined, read)).status).toBe(403);
});

it("preserves complete valid appearance bounds and explicit unavailable zero independently", async () => {
  await provision(); await acquire();
  let sequence = 0n;
  for (const field of ["title", "fontFamily"]) for (const sample of corpus.validUnicode) {
    const limit = field === "title" ? 40 : 128;
    const text = String.fromCharCode(...sample.codeUnits).repeat(limit / sample.codeUnits.length);
    const { death: _death, ...base } = candidate((++sequence).toString());
    expect((await publish({ ...base, appearance: { ...style(), [field]: text } })).status).toBe(200);
    expect((await publish({ ...base, appearance: { ...style(), [field]: text + "A" } })).status).toBe(400);
  }
  expect(await (await request()).json()).toMatchObject({ death: { revision: "0", digest: hash("null") } });
  expect((await publish({ ...candidate((++sequence).toString()), death: { availability: "unavailable", value: "0" } })).status).toBe(200);
  expect(JSON.parse((await stored())[2].value as string)).toEqual({ revision: "1", availability: "unavailable", value: "0" });
});

const sessionId = "33333333333333333333333333333333";
const acquisition = { v: 1, expectedEpoch: "0", sessionRequestId: sessionId };
const acquire = (body = acquisition) => request("session", "POST", body);

const candidate = (sequence = "1", value = "9007199254740993") => ({ v: 1, epoch: "1", sessionRequestId: sessionId,
  sequence, death: { value, availability: "available" } });
const publish = (body: unknown = candidate()) => request("state", "PUT", body);
const style = () => { const { revision: _revision, ...appearance } = corpus.valid[4].appearance!; return appearance; };

it("rejects invalid or stale generation before the first mutation initializes channels", async () => {
  await provision();
  const before = await stored();
  const rotation = { v: 1, rotationId: "a".repeat(32), readCapability: Buffer.from(randomBytes(32)).toString("hex") };
  for (const value of [undefined, ...corpus.invalidDecimals]) {
    expect((await request("credentials", "POST", { ...rotation, expectedGeneration: value })).status).toBe(400);
    expect(await stored()).toEqual(before);
  }
  for (const expectedGeneration of ["1", "9223372036854775807"]) {
    const rejected = await request("credentials", "POST", { ...rotation, expectedGeneration });
    expect(rejected.status).toBe(409);
    expect(await rejected.json()).toEqual({ error: "rotation_conflict" });
    expect(await stored()).toEqual(before);
  }
  await runInDurableObject(stub(), (_instance, state) => {
    state.storage.sql.exec("CREATE TRIGGER fail_first_rotation BEFORE UPDATE ON records WHEN NEW.key = 'control' BEGIN SELECT RAISE(ABORT, 'synthetic'); END");
  });
  expect((await request("credentials", "POST", { ...rotation, expectedGeneration: "0" })).status).toBe(500);
  expect(await stored()).toEqual(before);
  await runInDurableObject(stub(), (_instance, state) => { state.storage.sql.exec("DROP TRIGGER fail_first_rotation"); });
  expect((await request("credentials", "POST", { ...rotation, expectedGeneration: "0" })).status).toBe(200);
});

for (const mode of ["read", "write", "both"]) {
  it(`serializes same-generation ${mode} rotations and permits an explicit fresh rotation`, async () => {
    await provision(); await acquire(); await publish();
    const channels = (await stored()).filter(row => row.key !== "control");
    const rotations = ["a", "b"].map(c => ({ v: 1, rotationId: c.repeat(32), expectedGeneration: "0",
      ...(mode !== "write" ? { readCapability: Buffer.from(randomBytes(32)).toString("hex") } : {}),
      ...(mode !== "read" ? { writeCapability: Buffer.from(randomBytes(32)).toString("hex") } : {}) }));
    const raced = await Promise.all(rotations.map(body => request("credentials", "POST", body)));
    // Replaced write authority is rejected before the generation precondition.
    expect(raced.map(result => result.status).sort()).toEqual(mode === "read" ? [200, 409] : [200, 403]);
    const winnerIndex = raced.findIndex(result => result.status === 200);
    const winner = rotations[winnerIndex], loser = rotations[1 - winnerIndex];
    const currentWrite = winner.writeCapability ?? write;
    const before = await stored();
    expect((await request("credentials", "POST", loser, currentWrite)).status).toBe(409);
    expect(await stored()).toEqual(before);
    expect(await (await request("publisher", "GET", undefined, currentWrite)).json()).toMatchObject({ generation: "1" });
    const next = await request("credentials", "POST", { ...loser, expectedGeneration: "1" }, currentWrite);
    expect(next.status).toBe(200);
    expect(await next.json()).toMatchObject({ generation: "2", readGeneration: mode === "write" ? "0" : "2" });
    const after = await stored();
    expect(after.filter(row => row.key !== "control")).toEqual(channels);
    const control = JSON.parse(after[1].value as string);
    expect(control.readVerifier).toBe(verifier("read", loser.readCapability ?? read));
    expect(control.writeVerifier).toBe(verifier("write", loser.writeCapability ?? write));
  });
}

it("rejects superseded read rotation replay without fencing a newer publisher", async () => {
  await provision(); await acquire(); await publish();
  const first = { v: 1, rotationId: "a".repeat(32), expectedGeneration: "0",
    readCapability: Buffer.from(randomBytes(32)).toString("hex") };
  const second = { ...first, rotationId: "b".repeat(32), expectedGeneration: "1",
    readCapability: Buffer.from(randomBytes(32)).toString("hex") };
  expect((await request("credentials", "POST", first)).status).toBe(200);
  expect((await request("credentials", "POST", second)).status).toBe(200);
  expect((await acquire({ ...acquisition, expectedEpoch: "3" })).status).toBe(200);
  const before = await stored();
  await abortAllDurableObjects();
  expect((await request("credentials", "POST", first)).status).toBe(409);
  expect(await stored()).toEqual(before);
  expect(JSON.parse(before[1].value as string).readVerifier).toBe(verifier("read", second.readCapability));
  const published = await publish({ ...candidate("1", "42"), epoch: "4" });
  expect(published.status).toBe(200);
  expect(await published.json()).toMatchObject({ generation: "2" });
});

for (const mode of ["read", "write", "both"]) {
  it(`rotates ${mode} authority atomically and recovers only the matching retry`, async () => {
    await provision(); await acquire(); await publish();
    const newRead = Buffer.from(randomBytes(32)).toString("hex"), newWrite = Buffer.from(randomBytes(32)).toString("hex");
    const rotation = { v: 1, rotationId: "6".repeat(32), expectedGeneration: "0",
      ...(mode !== "write" ? { readCapability: newRead } : {}),
      ...(mode !== "read" ? { writeCapability: newWrite } : {}) };
    const currentWrite = mode === "read" ? write : newWrite;
    const rotated = await request("credentials", "POST", rotation);
    expect(rotated.status).toBe(200);
    const ack = await rotated.json();
    expect(ack).toEqual({ v: 1, rotationId: rotation.rotationId, epoch: "2", generation: "1", readGeneration: mode === "write" ? "0" : "1" });
    const rows = await stored();
    const control = JSON.parse(rows[1].value as string);
    expect(control.session).toBeNull();
    expect(control.last).toBeNull();
    expect(control.readVerifier).toBe(verifier("read", mode === "write" ? read : newRead));
    expect(control.writeVerifier).toBe(verifier("write", currentWrite));
    const serialized = JSON.stringify(rows);
    for (const token of [read, write, newRead, newWrite]) expect(serialized.includes(token)).toBe(false);
    await evictDurableObject(stub());
    expect(await (await request("credentials", "POST", rotation, currentWrite)).json()).toEqual(ack);
    expect(await stored()).toEqual(rows);
    if (mode !== "read") {
      expect((await request()).status).toBe(403);
      expect((await request("credentials", "POST", rotation)).status).toBe(403);
    }
    expect((await request("state", "PUT", candidate("2"), currentWrite)).status).toBe(409);
    expect((await request("credentials", "POST", { ...rotation, readCapability: read }, currentWrite)).status).toBe(409);
    expect((await request("credentials", "POST", { ...rotation, expectedGeneration: "1" }, currentWrite)).status).toBe(409);
    expect(await (await request("publisher", "GET", undefined, currentWrite)).json()).toMatchObject({ epoch: "2", death: { revision: "1" }, appearance: { revision: "0" } });
    const acquired = await request("session", "POST", { ...acquisition, expectedEpoch: "2", sessionRequestId: "7".repeat(32) }, currentWrite);
    expect(acquired.status).toBe(200);
    const afterSession = await stored();
    await abortAllDurableObjects();
    expect(await (await request("credentials", "POST", rotation, currentWrite)).json()).toEqual(ack);
    expect(await stored()).toEqual(afterSession);
    expect(await (await request("publisher", "GET", undefined, currentWrite)).json()).toMatchObject({ epoch: "3" });
  });
}

it("commits split channels idempotently with normalized digests and durable ordering", async () => {
  await provision();
  const sessionAck = await (await acquire()).json();
  const first = await publish();
  expect(first.status).toBe(200);
  const ack = await first.json();
  expect(ack).toMatchObject({ epoch: "1", sequence: "1", death: { revision: "1" }, appearance: { revision: "0" }, changed: ["death"] });
  const firstRows = await stored();
  await evictDurableObject(stub());
  expect(await (await publish()).json()).toEqual(ack);
  expect(await stored()).toEqual(firstRows);
  expect(await (await acquire()).json()).toEqual(sessionAck);
  expect((await publish(candidate("1", "42"))).status).toBe(409);
  expect((await publish(candidate("0"))).status).toBe(409);
  expect((await publish({ ...candidate("2"), epoch: "0" })).status).toBe(409);
  expect((await publish({ ...candidate("2"), sessionRequestId: "f".repeat(32) })).status).toBe(409);
  expect(await stored()).toEqual(firstRows);
  expect(await (await publish(candidate("2"))).json()).toMatchObject({ changed: [], death: { revision: "1" } });
  const { death: _death, ...control } = candidate("3");
  const appearance = { ...style(), title: " New title ", textColor: "#abcdef" };
  const styled = await (await publish({ ...control, appearance })).json();
  expect(styled).toMatchObject({ changed: ["appearance"], death: { revision: "1" }, appearance: { revision: "1" } });
  expect(await (await publish({ appearance: { ...appearance, title: "New title", textColor: "#ABCDEF" }, ...control })).json()).toEqual(styled);
  expect(await (await publish({ ...candidate("4", "0"), appearance: style() })).json()).toMatchObject({
    changed: ["death", "appearance"], death: { revision: "2" }, appearance: { revision: "2" }
  });
  expect(await (await publish({ ...candidate("5"), death: { availability: "unavailable", value: null } })).json()).toMatchObject({ death: { revision: "3" } });
  await evictDurableObject(stub());
  expect(await (await request()).json()).toMatchObject({ death: { revision: "3" }, appearance: { revision: "2" } });
});

it("acquires exactly once under concurrent CAS and rejects superseded acquisition retries", async () => {
  await provision();
  const first = await acquire();
  expect(first.status).toBe(200);
  const ack = await first.json();
  expect(ack).toMatchObject({ epoch: "1", sessionRequestId: sessionId });
  expect(await (await acquire()).json()).toEqual(ack);
  const contenders = ["4", "5"].map(c => ({ ...acquisition, expectedEpoch: "1", sessionRequestId: c.repeat(32) }));
  const raced = await Promise.all(contenders.map(acquire));
  expect(raced.map(r => r.status).sort()).toEqual([200, 409]);
  expect((await acquire()).status).toBe(409);
  await evictDurableObject(stub());
  expect((await acquire()).status).toBe(409);
  expect(await (await request()).json()).toMatchObject({ epoch: "2", death: { revision: "0" }, appearance: { revision: "0" } });
});

it("denies unknown identities without creating an object", async () => {
  const before = (await listDurableObjectIds(env.OVERLAYS)).map(id => id.toString());
  const response = await exports.default.fetch("https://overlay.test/api/v1/overlays/00000000000000000000000000000000/publisher");
  expect(response.status).toBe(404);
  expect(response.headers.get("Cache-Control")).toBe("no-store");
  expect((await listDurableObjectIds(env.OVERLAYS)).map(id => id.toString())).toEqual(before);
});

it("authenticates provisioned write authority and persists default channels across recreation", async () => {
  await provision();
  const response = await request();
  expect(response.status).toBe(200);
  const initial = await response.json();
  expect(initial).toEqual({ v: 1, epoch: "0", generation: "0", death: { revision: "0", digest: hash("null") },
    appearance: { revision: "0", digest: hash(JSON.stringify(corpus.valid[4].appearance)) } });
  const rows = await stored();
  expect(rows.map(row => row.key)).toEqual(["appearance", "control", "death"]);
  expect(JSON.parse(rows[2].value as string)).toBeNull();
  expect(JSON.parse(rows[0].value as string)).toEqual(corpus.valid[4].appearance);
  await evictDurableObject(stub());
  expect(await (await request()).json()).toEqual(initial);
  expect(await stored()).toEqual(rows);
  expect((await request("publisher", "GET", undefined, read)).status).toBe(403);
  expect((await request("publisher", "GET", undefined, write, otherId)).status).toBe(403);
});
