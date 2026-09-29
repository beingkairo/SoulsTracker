import { env } from "cloudflare:workers";
import { abortAllDurableObjects, reset, runInDurableObject } from "cloudflare:test";
import { afterEach, expect, it, vi } from "vitest";
import worker, { type Env } from "../src/index";
import { ProvisioningAuthority } from "../src/provisioning-authority";
import { verifier, verifierV2 } from "../src/protocol";
import type {} from "./authority.test";

const legacyId = "1".repeat(32);
const requestId = "2".repeat(64), readCapability = "3".repeat(64), writeCapability = "4".repeat(64);
const claim = { v: 1 as const, requestId, readVerifier: verifierV2(requestId, "read", readCapability),
  writeVerifier: verifierV2(requestId, "write", writeCapability) };
const admitted = { limit: vi.fn(async () => ({ success: true })) } as unknown as RateLimit;

afterEach(() => { vi.restoreAllMocks(); return reset(); });

function request(body: unknown = claim, options: { method?: string; headers?: Record<string, string>; suffix?: string } = {}) {
  return new Request(`https://overlay.test/api/v1/overlays${options.suffix ?? ""}`, {
    method: options.method ?? "POST",
    headers: { "Content-Type": "application/json", "Cache-Control": "no-store", "CF-Connecting-IP": "192.0.2.1",
      ...options.headers }, body: options.method === "GET" ? undefined : JSON.stringify(body)
  });
}

function environment(overrides: Partial<Env> = {}): Env {
  return { ...env, BROWSER_ORIGIN: "https://overlay.test", PROVISIONED_IDS: [legacyId],
    PROVISIONING_CONFIGURATION_VERSION: "1", PROVISIONING_CEILING: "100", PROVISIONING_HARD_MAXIMUM: "1000",
    CREATE_CLIENT_RATE_LIMITER: admitted,
    CREATE_SERVICE_RATE_LIMITER: admitted, ...overrides };
}

function rawCreate(body: BodyInit | null, options: { method?: string; headers?: Record<string, string>; suffix?: string } = {}) {
  return new Request(`https://overlay.test/api/v1/overlays${options.suffix ?? ""}`, {
    method: options.method ?? "POST", headers: { "Content-Type": "application/json", "Cache-Control": "no-store",
      "CF-Connecting-IP": "192.0.2.1", ...options.headers }, body
  });
}

async function targetRows(overlayId: string) {
  return runInDurableObject(env.OVERLAYS.get(env.OVERLAYS.idFromString(overlayId)), (_, state) =>
    state.storage.sql.exec<{ key: string; value: string }>("SELECT key,value FROM records ORDER BY key").toArray());
}

it("derives fixed versioned role verifiers", () => {
  expect(claim.readVerifier).toBe("d3b6d9f873f0c801d98a1d99cc381682ce31039be7c602fb8fbffc2bb11c2949");
  expect(claim.writeVerifier).toBe("bddf3b0a076271254e91c9197b7c4fe0aadcaa79a72d19824903cdd45b7a4bb8");
  expect(verifier(legacyId, "read", readCapability)).not.toBe(claim.readVerifier);
});

it("consumes both admission layers before rejecting malformed create requests", async () => {
  const client = vi.fn(async () => ({ success: true }));
  const service = vi.fn(async () => ({ success: true }));
  const authority = { getByName: vi.fn() } as unknown as Env["PROVISIONING_AUTHORITY"];
  const encoded = JSON.stringify(claim);
  for (const malformed of [
    request(claim, { method: "GET" }),
    request(claim, { suffix: "?unexpected=1" }),
    request(claim, { headers: { Cookie: "x=1" } }),
    request(claim, { headers: { Authorization: "Bearer secret" } }),
    request(claim, { headers: { "Content-Type": "text/plain" } }),
    request(claim, { headers: { "Content-Encoding": "gzip" } }),
    request({ ...claim, extra: true }),
    rawCreate(encoded.replace('"v":1', '"v":1,"v":1')),
    rawCreate(new Uint8Array([0xc0, 0xaf])),
    rawCreate("{"),
    rawCreate(encoded + " ".repeat(8193))
  ]) {
    const result = await worker.fetch(malformed, environment({ PROVISIONING_AUTHORITY: authority,
      CREATE_CLIENT_RATE_LIMITER: { limit: client } as unknown as RateLimit,
      CREATE_SERVICE_RATE_LIMITER: { limit: service } as unknown as RateLimit }));
    expect(result.status).toBeGreaterThanOrEqual(400);
  }
  expect(client).toHaveBeenCalledTimes(11);
  expect(service).toHaveBeenCalledTimes(11);
  expect(authority!.getByName).not.toHaveBeenCalled();
});

it("does not read the body or touch either namespace when either admission layer denies", async () => {
  for (const denied of ["client", "service"] as const) {
    const overlays = { newUniqueId: vi.fn(), get: vi.fn(), idFromString: vi.fn() } as unknown as Env["OVERLAYS"];
    const authority = { getByName: vi.fn() } as unknown as Env["PROVISIONING_AUTHORITY"];
    const deny = { limit: vi.fn(async () => ({ success: false })) } as unknown as RateLimit;
    const result = await worker.fetch(new Request("https://overlay.test/api/v1/overlays", { method: "POST",
      headers: { "Content-Type": "application/json", "CF-Connecting-IP": "192.0.2.1" }, body: JSON.stringify(claim) }),
      environment({ OVERLAYS: overlays, PROVISIONING_AUTHORITY: authority,
        ...(denied === "client" ? { CREATE_CLIENT_RATE_LIMITER: deny } : { CREATE_SERVICE_RATE_LIMITER: deny }) }));
    expect(result.status).toBe(429);
    expect(authority!.getByName).not.toHaveBeenCalled();
    expect(overlays.newUniqueId).not.toHaveBeenCalled();
    expect(overlays.get).not.toHaveBeenCalled();
  }
});

it("fails closed when trusted metadata or either admission dependency is unavailable", async () => {
  const authority = { getByName: vi.fn() } as unknown as Env["PROVISIONING_AUTHORITY"];
  const cases: Partial<Env>[] = [
    { CREATE_CLIENT_RATE_LIMITER: undefined },
    { CREATE_SERVICE_RATE_LIMITER: undefined },
    { CREATE_CLIENT_RATE_LIMITER: { limit: async () => { throw new Error("private"); } } as unknown as RateLimit },
    { CREATE_SERVICE_RATE_LIMITER: { limit: async () => ({}) } as unknown as RateLimit }
  ];
  for (const overrides of cases) {
    const result = await worker.fetch(request(), environment({ PROVISIONING_AUTHORITY: authority, ...overrides }));
    expect(result.status).toBe(503);
    expect(await result.json()).toEqual({ error: "admission_unavailable" });
  }
  const missingMetadata = request();
  missingMetadata.headers.delete("CF-Connecting-IP");
  expect((await worker.fetch(missingMetadata, environment({ PROVISIONING_AUTHORITY: authority }))).status).toBe(503);
  expect(authority!.getByName).not.toHaveBeenCalled();
});

it("returns the same namespace identity for exact replay and stores verifier-only target state", async () => {
  const first = await worker.fetch(request(), environment());
  expect(first.status).toBe(200);
  const body = await first.json() as { v: number; status: string; overlayId: string };
  expect(body.v).toBe(1);
  expect(body.status).toBe("provisioned");
  expect(body.overlayId).toMatch(/^[0-9a-f]{64}$/);
  const replay = await worker.fetch(request(), environment());
  expect(await replay.json()).toEqual(body);
  const rows = await targetRows(body.overlayId);
  expect(rows.map(row => row.key)).toEqual(["appearance", "control", "death"]);
  const control = JSON.parse(rows.find(row => row.key === "control")!.value);
  expect(control).toMatchObject({ authVersion: 2, requestId, readVerifier: claim.readVerifier,
    writeVerifier: claim.writeVerifier, epoch: "0", generation: "0" });
  expect(JSON.stringify(rows)).not.toContain(readCapability);
  expect(JSON.stringify(rows)).not.toContain(writeCapability);
  await abortAllDurableObjects();
  expect(await (await worker.fetch(request(), environment())).json()).toEqual(body);
  expect(await targetRows(body.overlayId)).toEqual(rows);
});

it("supports version-2 publisher authentication and rejects role or domain swaps", async () => {
  const created = await (await worker.fetch(request(), environment())).json() as { overlayId: string };
  const authorized = (action: string, method: string, capability: string, body?: unknown) => new Request(
    `https://overlay.test/api/v1/overlays/${created.overlayId}/${action}`,
    { method, headers: { Authorization: "Bearer " + capability, "Content-Type": "application/json" },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
  const publisher = (capability: string) => authorized("publisher", "GET", capability);
  expect((await worker.fetch(publisher(writeCapability), environment())).status).toBe(200);
  expect((await worker.fetch(publisher(readCapability), environment())).status).toBe(403);
  expect((await worker.fetch(publisher("5".repeat(64)), environment())).status).toBe(403);
  const sessionId = "6".repeat(32);
  const acquisition = { v: 1, expectedEpoch: "0", sessionRequestId: sessionId };
  expect((await worker.fetch(authorized("session", "POST", readCapability, acquisition), environment())).status).toBe(403);
  expect((await worker.fetch(authorized("session", "POST", writeCapability, acquisition), environment())).status).toBe(200);
  const update = { v: 1, epoch: "1", sessionRequestId: sessionId, sequence: "1",
    death: { value: "7", availability: "available" } };
  expect((await worker.fetch(authorized("state", "PUT", writeCapability, update), environment())).status).toBe(200);

  const live = await worker.fetch(new Request(`https://overlay.test/api/v1/overlays/${created.overlayId}/live`,
    { headers: { Upgrade: "websocket", Origin: "https://overlay.test" } }), environment());
  expect(live.status).toBe(101);
  const socket = live.webSocket!; socket.accept();
  const snapshot = new Promise<MessageEvent>(resolve => socket.addEventListener("message", resolve, { once: true }));
  socket.send(JSON.stringify({ v: 1, type: "auth", readCapability }));
  expect(JSON.parse(String((await snapshot).data)).death).toEqual({ revision: "1", value: "7", availability: "available" });
  socket.close();

  const deniedLive = await worker.fetch(new Request(`https://overlay.test/api/v1/overlays/${created.overlayId}/live`,
    { headers: { Upgrade: "websocket", Origin: "https://overlay.test" } }), environment());
  const deniedSocket = deniedLive.webSocket!; deniedSocket.accept();
  const closed = new Promise<CloseEvent>(resolve => deniedSocket.addEventListener("close", resolve, { once: true }));
  deniedSocket.send(JSON.stringify({ v: 1, type: "auth", readCapability: writeCapability }));
  expect((await closed).code).toBe(4401);
});

it("enforces the serialized hard ceiling while preserving exact replay", async () => {
  const authorityStub = env.PROVISIONING_AUTHORITY!.getByName("ceiling-one");
  const custom = environment({ PROVISIONED_IDS: [], PROVISIONING_CEILING: "1" });
  const secondId = "6".repeat(64);
  const second = { ...claim, requestId: secondId,
    readVerifier: verifierV2(secondId, "read", "7".repeat(64)),
    writeVerifier: verifierV2(secondId, "write", "8".repeat(64)) };
  const results = await runInDurableObject(authorityStub, async (_, state) => {
    const authority = new ProvisioningAuthority(state, custom);
    return Promise.all([authority.provision(claim), authority.provision(second)]);
  });
  expect(results.filter(result => result.ok)).toHaveLength(1);
  const accepted = results[0].ok ? claim : second;
  const replay = await runInDurableObject(authorityStub, (_, state) =>
    new ProvisioningAuthority(state, custom).provision(accepted));
  expect(replay.ok).toBe(true);
  expect(replay).toEqual(results.find(result => result.ok));
  await runInDurableObject(authorityStub, (_, state) => {
    const rows = state.storage.sql.exec("SELECT request_id,phase FROM allocations").toArray();
    expect(rows).toHaveLength(1);
    expect(rows[0].phase).toBe("active");
  });
});

it("counts seeded legacy identities against the ceiling", async () => {
  const authorityStub = env.PROVISIONING_AUTHORITY!.getByName("seeded-legacy");
  const result = await runInDurableObject(authorityStub, (_, state) =>
    new ProvisioningAuthority(state, environment({ PROVISIONING_CEILING: "1" })).provision(claim));
  expect(result.ok).toBe(false);
  await runInDurableObject(authorityStub, (_, state) => {
    const rows = state.storage.sql.exec<{ request_id: string; overlay_id: string; phase: string }>(
      "SELECT request_id,overlay_id,phase FROM allocations").toArray();
    expect(rows).toEqual([{ request_id: `legacy:${legacyId}`, overlay_id: legacyId, phase: "active" }]);
  });
});

it("denies at ceiling before identity generation, ledger insertion, or target access", async () => {
  const authorityStub = env.PROVISIONING_AUTHORITY!.getByName("deny-without-target");
  const overlays = { newUniqueId: vi.fn(), idFromString: vi.fn(), get: vi.fn() } as unknown as Env["OVERLAYS"];
  const result = await runInDurableObject(authorityStub, (_, state) => new ProvisioningAuthority(state,
    environment({ OVERLAYS: overlays, PROVISIONED_IDS: [legacyId], PROVISIONING_CEILING: "1" })).provision(claim));
  expect(result.ok).toBe(false);
  expect(overlays.newUniqueId).not.toHaveBeenCalled();
  expect(overlays.idFromString).not.toHaveBeenCalled();
  expect(overlays.get).not.toHaveBeenCalled();
  await runInDurableObject(authorityStub, (_, state) => expect(state.storage.sql.exec(
    "SELECT request_id FROM allocations WHERE request_id NOT LIKE 'legacy:%'").toArray()).toEqual([]));
});

it("admits only the remaining last slot across concurrent requests and survives restart", async () => {
  const authorityStub = env.PROVISIONING_AUTHORITY!.getByName("last-slot-race");
  const claims = Array.from({ length: 8 }, (_, index) => {
    const id = (index + 2).toString(16).repeat(64);
    return { ...claim, requestId: id, readVerifier: verifierV2(id, "read", "a".repeat(64)),
      writeVerifier: verifierV2(id, "write", "b".repeat(64)) };
  });
  const custom = environment({ PROVISIONED_IDS: [legacyId], PROVISIONING_CEILING: "2" });
  const results = await runInDurableObject(authorityStub, async (_, state) => Promise.all(
    claims.map(value => new ProvisioningAuthority(state, custom).provision(value))));
  expect(results.filter(result => result.ok)).toHaveLength(1);
  await abortAllDurableObjects();
  await runInDurableObject(env.PROVISIONING_AUTHORITY!.getByName("last-slot-race"), (_, state) => expect(state.storage.sql.exec(
    "SELECT request_id FROM allocations").toArray()).toHaveLength(2));
});

it("retains a counted reservation and reuses its identity after target initialization failure", async () => {
  const authorityStub = env.PROVISIONING_AUTHORITY!.getByName("reserved-retry");
  const overlayId = "a".repeat(64);
  const provisionV2 = vi.fn()
    .mockRejectedValueOnce(new Error("target unavailable"))
    .mockResolvedValueOnce(true);
  const unique = vi.fn(() => ({ toString: () => overlayId }));
  const overlays = {
    newUniqueId: unique,
    idFromString: vi.fn((id: string) => ({ toString: () => id })),
    get: vi.fn(() => ({ provisionV2 }))
  } as unknown as Env["OVERLAYS"];
  const custom = environment({ OVERLAYS: overlays, PROVISIONED_IDS: [], PROVISIONING_CEILING: "1" });
  await expect(runInDurableObject(authorityStub, (_, state) =>
    new ProvisioningAuthority(state, custom).provision(claim))).rejects.toThrow("target unavailable");
  await runInDurableObject(authorityStub, (_, state) => {
    expect(state.storage.sql.exec<{ overlay_id: string; phase: string }>(
      "SELECT overlay_id,phase FROM allocations").toArray()).toEqual([{ overlay_id: overlayId, phase: "reserved" }]);
  });
  const replay = await runInDurableObject(authorityStub, (_, state) =>
    new ProvisioningAuthority(state, custom).provision(claim));
  expect(replay).toEqual({ ok: true, overlayId });
  expect(unique).toHaveBeenCalledTimes(1);
  expect(provisionV2).toHaveBeenCalledTimes(2);
});

it("rejects changed request reuse and invalid authority configuration without allocating", async () => {
  for (const overrides of [
    { PROVISIONING_CONFIGURATION_VERSION: undefined }, { PROVISIONING_CEILING: undefined },
    { PROVISIONING_CEILING: "0" }, { PROVISIONING_CEILING: "01" }, { PROVISIONING_CEILING: "bad" },
    { PROVISIONING_HARD_MAXIMUM: undefined }, { PROVISIONING_HARD_MAXIMUM: "0" },
    { PROVISIONING_CEILING: "101", PROVISIONING_HARD_MAXIMUM: "100" }
  ] satisfies Partial<Env>[]) {
    const authorityStub = env.PROVISIONING_AUTHORITY!.getByName(`invalid-${JSON.stringify(overrides)}`);
    const result = await runInDurableObject(authorityStub, (_, state) =>
      new ProvisioningAuthority(state, environment(overrides)).provision(claim));
    expect(result.ok).toBe(false);
    await runInDurableObject(authorityStub, (_, state) =>
      expect(state.storage.sql.exec("SELECT request_id FROM allocations").toArray()).toEqual([]));
  }

  const authorityStub = env.PROVISIONING_AUTHORITY!.getByName("changed-reuse");
  const first = await runInDurableObject(authorityStub, (_, state) =>
    new ProvisioningAuthority(state, environment()).provision(claim));
  expect(first.ok).toBe(true);
  const changed = { ...claim, readVerifier: "9".repeat(64) };
  const conflict = await runInDurableObject(authorityStub, (_, state) =>
    new ProvisioningAuthority(state, environment()).provision(changed));
  expect(conflict.ok).toBe(false);
});

it("fails closed on unexpected configuration changes and permits a versioned lower ceiling without breaking replay", async () => {
  const authorityStub = env.PROVISIONING_AUTHORITY!.getByName("configuration-consistency");
  const initial = environment({ PROVISIONED_IDS: [], PROVISIONING_CEILING: "2" });
  const first = await runInDurableObject(authorityStub, (_, state) => new ProvisioningAuthority(state, initial).provision(claim));
  expect(first.ok).toBe(true);
  for (const changed of [
    environment({ PROVISIONED_IDS: [], PROVISIONING_CEILING: "1" }),
    environment({ PROVISIONED_IDS: [legacyId], PROVISIONING_CEILING: "2" }),
    environment({ PROVISIONED_IDS: [], PROVISIONING_CEILING: "2", PROVISIONING_HARD_MAXIMUM: "999" })
  ]) expect((await runInDurableObject(authorityStub, (_, state) =>
    new ProvisioningAuthority(state, changed).provision(claim))).ok).toBe(false);

  const lowered = environment({ PROVISIONED_IDS: [], PROVISIONING_CONFIGURATION_VERSION: "2", PROVISIONING_CEILING: "1" });
  expect(await runInDurableObject(authorityStub, (_, state) =>
    new ProvisioningAuthority(state, lowered).provision(claim))).toEqual(first);
  const other = { ...claim, requestId: "c".repeat(64),
    readVerifier: verifierV2("c".repeat(64), "read", "d".repeat(64)),
    writeVerifier: verifierV2("c".repeat(64), "write", "e".repeat(64)) };
  expect((await runInDurableObject(authorityStub, (_, state) =>
    new ProvisioningAuthority(state, lowered).provision(other))).ok).toBe(false);
});

it("rejects target adoption, partial state, claim tampering, and public internal routes without mutation", async () => {
  const targetId = env.OVERLAYS.newUniqueId();
  const target = env.OVERLAYS.get(targetId);
  await runInDurableObject(target, (_, state) => state.storage.sql.exec(
    "INSERT INTO records(key,value) VALUES('death','null')"));
  expect(await target.provisionV2(targetId.toString(), claim)).toBe(false);
  expect(await runInDurableObject(target, (_, state) => state.storage.sql.exec(
    "SELECT key,value FROM records").toArray())).toEqual([{ key: "death", value: "null" }]);
  expect(await target.provisionV2(env.OVERLAYS.newUniqueId().toString(), claim)).toBe(false);

  const cleanId = env.OVERLAYS.newUniqueId();
  const clean = env.OVERLAYS.get(cleanId);
  await runInDurableObject(clean, (_, state) => state.storage.sql.exec(
    "CREATE TRIGGER fail_target BEFORE INSERT ON records WHEN NEW.key='death' BEGIN SELECT RAISE(ABORT, 'synthetic'); END"));
  expect(await clean.provisionV2(cleanId.toString(), claim)).toBe(false);
  expect(await runInDurableObject(clean, (_, state) => state.storage.sql.exec(
    "SELECT key FROM records").toArray())).toEqual([]);
  await runInDurableObject(clean, (_, state) => state.storage.sql.exec("DROP TRIGGER fail_target"));
  expect(await clean.provisionV2(cleanId.toString(), claim)).toBe(true);
  expect(await clean.provisionV2(cleanId.toString(), { ...claim, writeVerifier: "f".repeat(64) })).toBe(false);
  expect(await targetRows(cleanId.toString())).toHaveLength(3);
  for (const suffix of ["provision", "initialize", "reconcile", "allocations", "count", "reset", "delete"]) {
    const result = await worker.fetch(new Request(`https://overlay.test/api/v1/overlays/${cleanId}/${suffix}`), environment());
    expect(result.status).toBe(404);
  }
});

it("does not expose the removed per-ID provisioning route", async () => {
  const old = new Request(`https://overlay.test/api/v1/overlays/${legacyId}/provision`, {
    method: "POST", headers: { Authorization: `Setup ${"a".repeat(64)}`, "Content-Type": "application/json" },
    body: JSON.stringify(claim)
  });
  expect((await worker.fetch(old, environment())).status).toBe(404);
});