import { beforeAll, afterEach, expect, it, vi } from "vitest";
import { build } from "esbuild";
import { Miniflare, convertV4MiniflareOptions, type V4WorkerOptions as WorkerOptions } from "miniflare";
import { Buffer } from "node:buffer";
import { generateKeyPairSync, sign } from "node:crypto";
import gateway from "../../operator_gateway/src/index";
import { checkedApply, checkedStatus } from "../../operator_gateway/src/contract";
import { verifyAccess } from "../../operator_gateway/src/access";

const readKey = "c".repeat(64), applyKey = "d".repeat(64), diagKey = "e".repeat(64);
const issuer = "https://synthetic.cloudflareaccess.com", operator = "operator@example.invalid";
const policy = { v: 1, revision: 1, enabled: true, windowSeconds: 3600, maxNewAllocations: 2 };
const settings = { OPERATOR_ENABLED: "true", OPERATOR_HOST: "operator.example.invalid", ACCESS_ISSUER: issuer,
  ACCESS_READ_AUDIENCE: "read", ACCESS_READ_POLICY: "read-policy", ACCESS_READ_OPERATOR: operator,
  ACCESS_APPLY_AUDIENCE: "apply", ACCESS_APPLY_POLICY: "apply-policy", ACCESS_APPLY_OPERATOR: operator };
const keys = generateKeyPairSync("rsa", { modulusLength: 2048 });
const jwk = { ...keys.publicKey.export({ format: "jwk" }), kid: "synthetic", alg: "RS256", use: "sig" };
const encode = (value: unknown) => Buffer.from(JSON.stringify(value)).toString("base64url");
function jwt(role = "read", overrides: Record<string, unknown> = {}, key = keys.privateKey) {
  const now = Math.floor(Date.now() / 1000);
  const input = `${encode({ alg: "RS256", kid: "synthetic" })}.${encode({ iss: issuer, aud: role, email: operator,
    sub: "synthetic-subject", policy: `${role}-policy`, amr: ["mfa"], exp: now + 300, iat: now - 1, nbf: now - 1,
    auth_time: now - 1, ...overrides })}`;
  return `${input}.${Buffer.from(sign("RSA-SHA256", Buffer.from(input), key)).toString("base64url")}`;
}
function request(role = "read", credential = role === "apply" ? applyKey : readKey, token = jwt(role), body = '{"expectedRevision":0}') {
  return new Request(`https://operator.example.invalid/operator/policy/${role === "apply" ? "apply" : "status"}`, {
    method: role === "apply" ? "POST" : "GET", headers: { Authorization: `Bearer ${credential}`,
      "Cf-Access-Jwt-Assertion": token, ...(role === "apply" ? { "Content-Type": "application/json" } : {}) },
    ...(role === "apply" ? { body } : {}) });
}
let scripts: Record<string, string> = {};
let mf: Miniflare | undefined;
beforeAll(async () => {
  for (const [name, entry] of Object.entries({ target: "test/operator-node/harness.ts", gateway: "operator_gateway/src/index.ts",
    diagnostic: "operator_gateway/src/diagnostic.ts", permanent: "src/index.ts", temporary: "src/diagnostic-index.ts",
    probe: "test/operator-node/binding-probe.ts" })) {
    const result = await build({ entryPoints: [entry], bundle: true, write: false, format: "esm", platform: "neutral",
      target: "es2022", external: ["cloudflare:workers", "node:*"] });
    scripts[name] = result.outputFiles[0].text;
  }
});
afterEach(async () => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); await mf?.dispose(); mf = undefined; });
const outbound = (r: { url: string }) => {
  if (r.url !== `${issuer}/cdn-cgi/access/certs`) throw new Error("external network forbidden");
  return new Response(JSON.stringify({ keys: [jwk] }));
};
function options(temporary = true, targetScript = scripts.target, configuredPolicy?: typeof policy, loseApply = false) {
  const target: WorkerOptions = { name: "authority", script: targetScript, modules: true,
    compatibilityDate: "2026-08-15", compatibilityFlags: ["nodejs_compat"],
    durableObjects: { OVERLAYS: { className: "OverlayState", useSQLite: true }, PROVISIONING_AUTHORITY: { className: "ProvisioningAuthority", useSQLite: true } },
    bindings: { PROVISIONED_IDS: ["1".repeat(32)], PROVISIONING_CONFIGURATION_VERSION: "3", PROVISIONING_CEILING: "100",
      PROVISIONING_HARD_MAXIMUM: "100", PROVISIONING_POLICY_READ_KEY: readKey, PROVISIONING_POLICY_APPLY_KEY: applyKey,
      ...(temporary ? { PROVISIONING_DIAGNOSTIC_READ_KEY: diagKey, PROVISIONING_DIAGNOSTIC_ENABLED: "true" } : {}),
      ...(configuredPolicy === undefined ? {} : { PROVISIONING_ALLOCATION_POLICY: configuredPolicy }) },
    outboundService: () => { throw new Error("external network forbidden"); } };
  const workers: WorkerOptions[] = [target, { name: "gateway", script: scripts.gateway, modules: true,
    compatibilityDate: "2026-08-15", compatibilityFlags: ["nodejs_compat"], bindings: settings, outboundService: outbound,
    serviceBindings: { POLICY_READ: { name: "authority", entrypoint: "PolicyReadEntrypoint" },
      POLICY_APPLY: { name: loseApply ? "binding-probe" : "authority", entrypoint: loseApply ? "LostApplyEntrypoint" : "PolicyApplyEntrypoint" } } },
    { name: "binding-probe", script: scripts.probe, modules: true, compatibilityDate: "2026-08-15",
      compatibilityFlags: ["nodejs_compat"], serviceBindings: {
        READ: { name: "authority", entrypoint: "PolicyReadEntrypoint" }, APPLY: { name: "authority", entrypoint: "PolicyApplyEntrypoint" } },
      outboundService: () => { throw new Error("external network forbidden"); } }];
  if (temporary) workers.push({ name: "diagnostic", script: scripts.diagnostic, modules: true,
    compatibilityDate: "2026-08-15", compatibilityFlags: ["nodejs_compat"], outboundService: outbound,
    bindings: { OPERATOR_ENABLED: "true", OPERATOR_HOST: "diagnostic.example.invalid", ACCESS_ISSUER: issuer,
      ACCESS_DIAGNOSTIC_AUDIENCE: "diagnostic", ACCESS_DIAGNOSTIC_POLICY: "diagnostic-policy", ACCESS_DIAGNOSTIC_OPERATOR: operator },
    serviceBindings: { DIAGNOSTIC_READ: { name: "authority", entrypoint: "DiagnosticReadEntrypoint" } } });
  return convertV4MiniflareOptions({ workers, cf: false });
}
async function start() { mf = new Miniflare(options()); await mf.ready; }
async function fixture(action: string, value?: unknown) {
  const worker = await mf!.getWorker("authority");
  return (await worker.fetch("https://fixture.invalid", { method: "POST", body: JSON.stringify({ action, value }) })).json() as Promise<any>;
}
async function call(req = request()) { return (await mf!.getWorker("gateway")).fetch(req.url, {
  method: req.method, headers: Object.fromEntries(req.headers), ...(req.method === "POST" ? { body: await req.text() } : {}) }); }
async function diagnostic(credential = diagKey) {
  return (await mf!.getWorker("diagnostic")).fetch("https://diagnostic.example.invalid/operator/reconcile", {
    headers: { Authorization: `Bearer ${credential}`, "Cf-Access-Jwt-Assertion": jwt("diagnostic") } });
}

it("uses actual named multi-Worker RPC with separate credentials and no allocation side effects", async () => {
  await start(); const empty = await fixture("snapshot");
  expect((await call()).status).toBe(200);
  expect(await fixture("snapshot")).toEqual(empty);
  await fixture("seed"); await fixture("settings", policy); const before = await fixture("snapshot");
  for (const [role, key] of [["read", applyKey], ["apply", readKey], ["apply", diagKey], ["read", diagKey]])
    expect((await call(request(role, key))).status).toBe(403);
  expect(await fixture("snapshot")).toEqual(before);
  expect(await (await call(request("apply"))).json()).toEqual({ ok: true, status: "applied", revision: 1 });
  const applied = await fixture("snapshot");
  expect(applied.allocations).toEqual(before.allocations);
  expect(applied.authority_configuration).toEqual(before.authority_configuration);
  expect(await (await call()).json()).toMatchObject({ report: { persisted_revision: 1, configured_policy_equal: true, window_count: 0 } });
  expect(await fixture("snapshot")).toEqual(applied);
  expect(await (await call(request("apply"))).json()).toEqual({ ok: true, status: "already_applied", revision: 1 });
  expect(await fixture("snapshot")).toEqual(applied);
});

it("requires Access as well as the target role credential and rejects malformed policy bodies", async () => {
  await start(); await fixture("seed"); await fixture("settings", policy); const before = await fixture("snapshot");
  for (const overrides of [{ aud: "other" }, { amr: undefined }, { amr: [] }, { policy: undefined }, { auth_time: undefined },
    { exp: 1 }, { nbf: 9999999999 }, { auth_time: 1 }, { email: "other@example.invalid" }, { aud: ["read", "apply"] }])
    expect((await call(request("read", readKey, jwt("read", overrides)))).status).toBe(403);
  expect((await call(request("apply", applyKey, jwt("read")))).status).toBe(403);
  expect((await call(request("read", readKey, "forged"))).status).toBe(403);
  const forged = generateKeyPairSync("rsa", { modulusLength: 2048 });
  expect((await call(request("read", readKey, jwt("read", {}, forged.privateKey)))).status).toBe(403);
  for (const body of ['{}', '{"expectedRevision":"0"}', '{"expectedRevision":0,"policy":{}}',
    '{"expectedRevision":0,"expectedRevision":0}', '{"expectedRevision":0e0}', ' '.repeat(257), '{"expectedRevision":-1}'])
    expect((await call(request("apply", applyKey, jwt("apply"), body))).status).toBe(403);
  expect(await fixture("snapshot")).toEqual(before);
});

it("stops through separately staged configuration, explicit apply and independent readback; exact recovery survives", async () => {
  await start(); await fixture("seed"); await fixture("settings", policy);
  await call(request("apply"));
  const first = await fixture("claim", 1); expect(first.ok).toBe(true);
  const before = await fixture("snapshot");
  // Apply cannot change configuration. Supplying the same predecessor just replays.
  expect(await (await call(request("apply"))).json()).toMatchObject({ status: "already_applied" });
  await fixture("settings", { ...policy, revision: 2, enabled: false });
  expect((await fixture("claim", 2)).ok).toBe(false);
  expect(await (await call()).json()).toMatchObject({ report: { configured_policy_equal: false, persisted_revision: 1 } });
  expect(await (await call(request("apply", applyKey, jwt("apply"), '{"expectedRevision":1}'))).json())
    .toEqual({ ok: true, status: "applied", revision: 2 });
  expect(await (await call()).json()).toMatchObject({ report: { configured_policy_equal: true, new_allocation_state: "stopped", window_count: 1 } });
  expect((await fixture("snapshot")).allocation_policy[0].window_ends_ms).toBeGreaterThanOrEqual(before.allocation_policy[0].window_ends_ms);
  expect(await fixture("claim", 1)).toEqual(first);
  expect((await fixture("claim", 2)).ok).toBe(false);
});

it("authenticates direct diagnostic RPC, reports only pre-activation aggregates, and survives diagnostic teardown", async () => {
  await start(); await fixture("seed"); const before = await fixture("snapshot");
  expect(await fixture("directDiagnostic", readKey)).toEqual({ ok: false, error: "forbidden" });
  expect((await diagnostic(applyKey)).status).toBe(403);
  expect(await (await diagnostic()).json()).toMatchObject({ ok: true, report: { ledger_total: 1, unavailable_reason: "none" } });
  expect(await fixture("snapshot")).toEqual(before);
  await fixture("settings", policy); await call(request("apply"));
  expect(await (await diagnostic()).json()).toMatchObject({ report: { ledger_total: null, unavailable_reason: "unsupported_policy" } });
  const applied = await fixture("snapshot");
  await mf!.setOptions(options(false));
  expect(await fixture("directDiagnostic", diagKey)).toEqual({ ok: false, error: "forbidden" });
  expect(await (await call()).json()).toMatchObject({ report: { persisted_revision: 1, configured_policy_equal: true } });
  expect(await fixture("snapshot")).toEqual(applied);
});

it("temporary and permanent production modules retain class linkage; permanent module exports no diagnostic", async () => {
  for (const [temporary, script] of [[true, scripts.temporary], [false, scripts.permanent]] as const) {
    await mf?.dispose(); mf = new Miniflare(options(temporary, script)); await mf.ready;
    expect((await call()).status).toBe(200);
    const result = await (await call()).json() as any;
    expect(result.report.activation_state).toBe("never_activated");
    expect((await call(request("apply"))).status).toBe(409); // no authority initialization
    if (temporary) expect((await diagnostic()).status).toBe(200);
  }
  expect(scripts.permanent).not.toContain("class DiagnosticReadEntrypoint");
  expect(scripts.permanent).not.toContain("readAuthorityAggregate");
});

it("compatible code rollback retains stored stop, consumption, replay and fail-closed stale configuration", async () => {
  await start(); await fixture("seed"); await fixture("settings", policy); await call(request("apply"));
  const first = await fixture("claim", 1);
  await fixture("settings", { ...policy, revision: 2, enabled: false });
  await call(request("apply", applyKey, jwt("apply"), '{"expectedRevision":1}'));
  const before = await fixture("snapshot");
  await mf!.setOptions(options(false, scripts.permanent, { ...policy, revision: 2, enabled: false }));
  expect(await (await call()).json()).toMatchObject({ report: { persisted_revision: 2, configured_policy_equal: true,
    window_count: 1, new_allocation_state: "stopped" } });
  // Return to the test driver solely to inspect all retained rows and replay.
  await mf!.setOptions(options(false));
  expect(await fixture("snapshot")).toEqual(before);
  expect(await fixture("claim", 1)).toEqual(first);
  await fixture("settings", policy); // stale config stays closed, never reverts storage
  expect((await fixture("claim", 2)).ok).toBe(false);
  expect(await fixture("snapshot")).toEqual(before);
  expect(await (await call()).json()).toMatchObject({ report: { persisted_revision: 2, configured_policy_equal: false } });
});

it("binding possession without target credentials supplies no read or apply permission", async () => {
  await start(); const before = await fixture("snapshot");
  const probe = await mf!.getWorker("binding-probe");
  for (const body of [{ apply: false }, { apply: true }, { apply: true, credential: readKey }, { apply: false, credential: applyKey }]) {
    expect(await (await probe.fetch("https://probe.invalid", { method: "POST", body: JSON.stringify(body) })).json())
      .toEqual({ ok: false, error: "forbidden" });
  }
  expect(await fixture("snapshot")).toEqual(before);
});

it("keeps every operator operation outside existing public publisher/browser/create routes", async () => {
  await start(); const before = await fixture("snapshot");
  // Use the actual permanent public router, not the synthetic driver fetch.
  await mf!.setOptions(options(false, scripts.permanent));
  const target = await mf!.getWorker("authority");
  for (const path of ["/operator/policy/status", "/operator/policy/apply", "/operator/reconcile",
    `/api/v1/overlays/${"1".repeat(32)}/applyConfiguredPolicy`, `/api/v1/overlays/${"1".repeat(32)}/policyStatus`])
    expect((await target.fetch(`https://overlay.example.invalid${path}`)).status).toBe(404);
  await mf!.setOptions(options(false));
  expect(await fixture("snapshot")).toEqual(before);
});

it("reports a lost response after a real multi-Worker apply commit without issuing a second application", async () => {
  mf = new Miniflare(options(true, scripts.target, undefined, true)); await mf.ready;
  await fixture("seed"); await fixture("settings", policy);
  const response = await call(request("apply"));
  expect(response.status).toBe(503);
  expect(await response.json()).toEqual({ ok: false, error: "outcome_unknown" });
  expect(await (await call()).json()).toMatchObject({ report: { persisted_revision: 1, configured_policy_equal: true, window_count: 0 } });
  const snapshot = await fixture("snapshot");
  expect(snapshot.allocations).toHaveLength(1);
});

it("denies malformed routes, cross-origin/browser requests, and disabled or colliding role configuration before RPC", async () => {
  const status = vi.fn(), apply = vi.fn();
  const env = { ...settings, POLICY_READ: { status }, POLICY_APPLY: { apply } } as any;
  for (const path of ["/operator/reconcile", "/operator/policy/provision", "/operator/policy/status?x=1", "/api/v1/overlays"])
    expect((await gateway.fetch(new Request(`https://operator.example.invalid${path}`), env)).status).toBe(403);
  for (const header of ["Cookie", "Origin", "Upgrade"]) {
    const req = request(); req.headers.set(header, "synthetic");
    expect((await gateway.fetch(req, env)).status).toBe(403);
  }
  for (const config of [{ ...env, OPERATOR_ENABLED: undefined }, { ...env, ACCESS_APPLY_POLICY: "read-policy" },
    { ...env, ACCESS_READ_AUDIENCE: undefined }]) expect((await gateway.fetch(request(), config)).status).toBe(403);
  expect(status).not.toHaveBeenCalled(); expect(apply).not.toHaveBeenCalled();
});

it("never retries or claims failure on an unresolved apply; readback is separately authenticated", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ keys: [jwk] }))));
  const apply = vi.fn(() => new Promise<never>(() => {}));
  const env = { ...settings, POLICY_APPLY: { apply } } as any;
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  const result = gateway.fetch(request("apply"), env);
  await vi.waitFor(() => expect(apply).toHaveBeenCalledOnce());
  await vi.advanceTimersByTimeAsync(10_000);
  const response = await result;
  expect(response.status).toBe(503);
  expect(await response.json()).toEqual({ ok: false, error: "outcome_unknown" });
  await vi.advanceTimersByTimeAsync(20_000);
  expect(apply).toHaveBeenCalledOnce();
  expect(response.headers.has("Retry-After")).toBe(false);
});

it("rejects malformed downstream responses rather than exposing secret fields or exception text", async () => {
  expect(() => checkedApply({ ok: true, status: "applied", revision: 1, credential: applyKey })).toThrow();
  expect(() => checkedApply({ ok: false, error: applyKey })).toThrow();
  expect(() => checkedStatus({ ok: true, report: { policy_digest: readKey } })).toThrow();
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ keys: [jwk] }))));
  const response = await gateway.fetch(request("apply"), { ...settings,
    POLICY_APPLY: { apply: () => { throw new Error(applyKey); } } } as any);
  expect(await response.text()).not.toContain(applyKey);
  expect(response.headers.get("Cache-Control")).toBe("no-store");
});

it("fails closed for missing claim policy/MFA, duplicate JWT fields, bad keys and JWKS failure", async () => {
  const expected = { issuer, audience: "read", operator, policy: "read-policy" };
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ keys: [jwk] }))));
  await expect(verifyAccess(jwt(), expected)).resolves.toBeUndefined();
  const token = jwt().split(".");
  const duplicated = Buffer.from(Buffer.from(token[1], "base64url").toString().replace('"aud":"read"', '"aud":"read","aud":"read"')).toString("base64url");
  await expect(verifyAccess(`${token[0]}.${duplicated}.${token[2]}`, expected)).rejects.toThrow();
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ keys: [jwk, jwk] }))));
  await expect(verifyAccess(jwt(), expected)).rejects.toThrow();
  vi.stubGlobal("fetch", vi.fn(async () => { throw new Error("synthetic JWKS down"); }));
  await expect(verifyAccess(jwt(), expected)).rejects.toThrow();
  const redirected = vi.fn(async () => new Response(null, { status: 302, headers: { Location: "https://external.invalid" } }));
  vi.stubGlobal("fetch", redirected);
  await expect(verifyAccess(jwt(), expected)).rejects.toThrow();
  expect(redirected).toHaveBeenCalledOnce();
});
