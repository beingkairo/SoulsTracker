import { env } from "cloudflare:workers";
import { reset, runInDurableObject } from "cloudflare:test";
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
    PROVISIONING_CEILING: "100", CREATE_CLIENT_RATE_LIMITER: admitted,
    CREATE_SERVICE_RATE_LIMITER: admitted, ...overrides };
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
  for (const malformed of [
    request(claim, { method: "GET" }),
    request(claim, { suffix: "?unexpected=1" }),
    request(claim, { headers: { Cookie: "x=1" } }),
    request(claim, { headers: { Authorization: "Bearer secret" } }),
    request(claim, { headers: { "Content-Type": "text/plain" } }),
    request({ ...claim, extra: true })
  ]) {
    const result = await worker.fetch(malformed, environment({ PROVISIONING_AUTHORITY: authority,
      CREATE_CLIENT_RATE_LIMITER: { limit: client } as unknown as RateLimit,
      CREATE_SERVICE_RATE_LIMITER: { limit: service } as unknown as RateLimit }));
    expect(result.status).toBeGreaterThanOrEqual(400);
  }
  expect(client).toHaveBeenCalledTimes(6);
  expect(service).toHaveBeenCalledTimes(6);
  expect(authority!.getByName).not.toHaveBeenCalled();
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
});

it("supports version-2 publisher authentication and rejects role or domain swaps", async () => {
  const created = await (await worker.fetch(request(), environment())).json() as { overlayId: string };
  const publisher = (capability: string) => new Request(
    `https://overlay.test/api/v1/overlays/${created.overlayId}/publisher`,
    { headers: { Authorization: `Bearer ${capability}` } });
  expect((await worker.fetch(publisher(writeCapability), environment())).status).toBe(200);
  expect((await worker.fetch(publisher(readCapability), environment())).status).toBe(403);
  expect((await worker.fetch(publisher("5".repeat(64)), environment())).status).toBe(403);
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

it("rejects changed request reuse and invalid ceiling without allocating", async () => {
  for (const configured of [undefined, "0", "01", "bad", "2147483648"]) {
    const authorityStub = env.PROVISIONING_AUTHORITY!.getByName(`invalid-${configured ?? "missing"}`);
    const result = await runInDurableObject(authorityStub, (_, state) =>
      new ProvisioningAuthority(state, environment({ PROVISIONING_CEILING: configured })).provision(claim));
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

it("does not expose the removed per-ID provisioning route", async () => {
  const old = new Request(`https://overlay.test/api/v1/overlays/${legacyId}/provision`, {
    method: "POST", headers: { Authorization: `Setup ${"a".repeat(64)}`, "Content-Type": "application/json" },
    body: JSON.stringify(claim)
  });
  expect((await worker.fetch(old, environment())).status).toBe(404);
});