import { afterEach, beforeAll, expect, it } from "vitest";
import { build } from "esbuild";
import { Miniflare, convertV4MiniflareOptions, type V4WorkerOptions } from "miniflare";
import { readFileSync, mkdtempSync, rmSync } from "node:fs";
import path from "node:path";
import { tmpdir } from "node:os";
import { createHash } from "node:crypto";
import { verifierV2 } from "../src/protocol";
import type { ProvisioningAuthority as RecoveryAuthority } from "./provisioning-authority";

const base = "cb612a7a72ad024c3c0e209156ec6ba2da309138";
const candidate = path.resolve(`dist/operator-candidate/${base}-b950f1ba731b`);
if (!process.env.RECOVERY_PACKAGE) throw Error("RECOVERY_PACKAGE must identify the exact frozen recovery under test");
const recovery = path.resolve(process.env.RECOVERY_PACKAGE);
const readKey = "c".repeat(64), applyKey = "d".repeat(64);
const read = "a".repeat(64), write = "b".repeat(64), session = "3".repeat(32);
const policy = { v: 1, revision: 1, enabled: true, windowSeconds: 3600, maxNewAllocations: 5 };
const sha = (data: string | Buffer) => createHash("sha256").update(data).digest("hex");
const bundle = (directory: string) => readFileSync(path.join(directory, "worker/index.js"), "utf8");
const claim = (index = 1) => {
  const requestId = index.toString(16).padStart(64, "0");
  return { v: 1, requestId, readVerifier: verifierV2(requestId, "read", read), writeVerifier: verifierV2(requestId, "write", write) };
};
let scripts: Record<string, string> = {}, mf: Miniflare | undefined, persist: string | undefined;
let currentOptions: { policy?: typeof policy | null; failInit?: boolean; keys?: boolean; ceiling?: string; seeds?: string[] } = {};
let currentScript = "candidate";

// Test-only inspection subclasses; their production methods come from the exact
// frozen bundles. No fixture method is included in either deployment artifact.
beforeAll(async () => {
  for (const [name, directory] of Object.entries({ candidate, recovery })) {
    const result = await build({ stdin: { contents: `
      import handler, { OverlayState as Overlay, ProvisioningAuthority as Authority,
        PolicyReadEntrypoint, PolicyApplyEntrypoint } from ${JSON.stringify(path.join(directory, "worker/index.js"))};
      export { PolicyReadEntrypoint, PolicyApplyEntrypoint };
      export class OverlayState extends Overlay {
        async fixtureRows() { return this.ctx.storage.sql.exec("SELECT key,value FROM records ORDER BY key").toArray(); }
        async provisionV2(...args) { return this.env.TEST_FAIL_INIT ? false : super.provisionV2(...args); }
      }
      export class ProvisioningAuthority extends Authority {
        async fixtureRows() {
          const names = this.ctx.storage.sql.exec("SELECT name FROM sqlite_master WHERE type='table' AND name IN ('allocations','authority_configuration','allocation_policy') ORDER BY name").toArray();
          return Object.fromEntries(names.map(({name}) => [name,this.ctx.storage.sql.exec('SELECT * FROM '+name+' ORDER BY 1').toArray()]));
        }
        async fixturePolicyTable(kind) {
          this.ctx.storage.sql.exec("CREATE TABLE allocation_policy (singleton INTEGER, revision INTEGER)");
          if(kind === 'corrupt') this.ctx.storage.sql.exec("INSERT INTO allocation_policy VALUES (1,-1)");
        }
      }
      export default handler;
    `, resolveDir: process.cwd() }, bundle: true, write: false, format: "esm", platform: "neutral", target: "es2022",
      external: ["cloudflare:workers", "node:*"] });
    scripts[name] = result.outputFiles[0].text;
  }
  // A synthetic regression at the candidate-only allocation admission boundary.
  // Recovery must restore real public creation without executing that function.
  expect(scripts.candidate).toContain("function reserveNewAllocation(");
  scripts.fault = scripts.candidate.replace(/(function reserveNewAllocation\([^)]*\)\s*\{)/,
    '$1 throw new Error("synthetic allocation regression");');
});
afterEach(async () => {
  await mf?.dispose(); mf = undefined;
  if (persist) {
    if (path.dirname(path.resolve(persist)) !== path.resolve(tmpdir()) || !path.basename(persist).startsWith("soulstracker-recovery-"))
      throw Error("unexpected test persistence path");
    rmSync(persist, { recursive: true, force: true });
  }
  persist = undefined; currentOptions = {}; currentScript = "candidate";
});
function options(name = currentScript, changes = currentOptions) {
  const worker: V4WorkerOptions = { name: "public", script: scripts[name], modules: true,
    compatibilityDate: "2026-08-15", compatibilityFlags: ["nodejs_compat"],
    durableObjects: { OVERLAYS: { className: "OverlayState", useSQLite: true },
      PROVISIONING_AUTHORITY: { className: "ProvisioningAuthority", useSQLite: true } },
    bindings: { PROVISIONED_IDS: changes.seeds ?? ["1".repeat(32)], PROVISIONING_CONFIGURATION_VERSION: "3",
      PROVISIONING_CEILING: changes.ceiling ?? "100", PROVISIONING_HARD_MAXIMUM: "100", BROWSER_ORIGIN: "https://overlay.example.invalid",
      ...(changes.keys ? { PROVISIONING_POLICY_READ_KEY: readKey, PROVISIONING_POLICY_APPLY_KEY: applyKey } : {}),
      ...(changes.policy === undefined ? {} : { PROVISIONING_ALLOCATION_POLICY: changes.policy }),
      TEST_FAIL_INIT: !!changes.failInit },
    ratelimits: Object.fromEntries(["PUBLISHER", "LIVE", "CREATE_CLIENT", "CREATE_SERVICE"].map((n, i) =>
      [`${n}_RATE_LIMITER`, { namespace_id: String(990001 + i), simple: { limit: 1000, period: 60 as const } }])),
    outboundService: () => { throw Error("network forbidden"); } };
  return convertV4MiniflareOptions({ workers: [worker], cf: false, resourcePersistencePath: persist });
}
async function start(changes = {}) {
  persist = mkdtempSync(path.join(tmpdir(), "soulstracker-recovery-"));
  currentOptions = changes; mf = new Miniflare(options()); await mf.ready;
}
async function switchTo(name = "recovery", changes = currentOptions) {
  currentScript = name; currentOptions = changes;
  await mf!.setOptions(options()); await mf!.ready;
}
async function authority() {
  return (await mf!.getDurableObjectNamespace("PROVISIONING_AUTHORITY", "public")).getByName("global") as any;
}
async function overlay(id: string) {
  const ns = await mf!.getDurableObjectNamespace("OVERLAYS", "public");
  return ns.get(ns.idFromString(id)) as any;
}
async function create(index = 1) {
  const response = await (await mf!.getWorker("public")).fetch("https://overlay.example.invalid/api/v1/overlays", {
    method: "POST", headers: { "Content-Type": "application/json", "CF-Connecting-IP": "192.0.2.1" }, body: JSON.stringify(claim(index)) });
  return { status: response.status, body: await response.json() as any };
}
async function publish(id: string, action: string, body?: unknown, token = write, protocol = true) {
  return (await mf!.getWorker("public")).fetch(`https://overlay.example.invalid/api/v1/overlays/${id}/${action}`, {
    method: action === "publisher" ? "GET" : action === "state" ? "PUT" : "POST",
    headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json", ...(protocol ? { "X-SoulsTracker-Protocol": "2" } : {}) },
    ...(body === undefined ? {} : { body: JSON.stringify({ ...(body as object), ...(protocol ? { protocolVersion: 2 } : {}) }) }) });
}
const acquire = (id: string) => publish(id, "session", { v: 1, expectedEpoch: "0", sessionRequestId: session });
const state = (sequence = "1") => ({ v: 1, epoch: "1", sessionRequestId: session, sequence,
  death: { value: "9007199254740993", availability: "available" } });
const detach = (value: unknown) => JSON.parse(JSON.stringify(value));
const rows = async (id: string) => detach(await (await overlay(id)).fixtureRows());
const snapshot = async () => detach(await (await authority()).fixtureRows());

it("freezes different authority code, exact approved deployment configuration and every asset", () => {
  const manifest = JSON.parse(readFileSync(path.join(recovery, "manifest.json"), "utf8"));
  expect(manifest.source_sha).toBe(base);
  expect(manifest.historical_authority_sha).toBe("a9ce3693077c8adf9fd12d8cd1234fee0c8d2fbf");
  for (const item of manifest.files) {
    const data = readFileSync(path.join(recovery, item.path));
    expect(data.length).toBe(item.bytes); expect(sha(data)).toBe(item.sha256);
    if (item.path.startsWith("assets/") || item.path === "config/target.json")
      expect(sha(data)).toBe(sha(readFileSync(path.join(candidate, item.path))));
  }
  expect(sha(bundle(recovery))).not.toBe(sha(bundle(candidate)));
  expect(bundle(recovery)).not.toContain("function reserveNewAllocation(");
  expect(bundle(recovery)).not.toContain("fixtureRows");
  expect(bundle(recovery)).not.toContain("readAuthorityAggregate");
});

it("retains candidate-written publisher state, accepts the next sequence and survives restart", async () => {
  await start(); const created = await create(); expect(created.status).toBe(200); const id = created.body.overlayId;
  expect((await acquire(id)).status).toBe(200); expect((await publish(id, "state", state())).status).toBe(200);
  // Provisioning must not reset an already-used overlay to its initial state.
  const candidateReplay = await create(); expect(candidateReplay.status).toBe(409);
  const before = await rows(id), ledger = await snapshot();
  await switchTo(); expect(await rows(id)).toEqual(before); expect(await snapshot()).toEqual(ledger);
  expect((await publish(id, "state", state())).status).toBe(200); // exact publisher retry
  expect((await publish(id, "state", state("2"))).status).toBe(200);
  const after = await rows(id); await mf!.dispose(); mf = new Miniflare(options()); await mf.ready;
  expect(await rows(id)).toEqual(after); expect(await snapshot()).toEqual(ledger);
  expect(await create()).toEqual(candidateReplay); expect(await rows(id)).toEqual(after);
});

it("rejects legacy and wrong-role writes without changing candidate-written payload or authority", async () => {
  await start(); const id = (await create()).body.overlayId; await acquire(id); await publish(id, "state", state());
  await switchTo(); const before = await rows(id), ledger = await snapshot();
  const legacy = await publish(id, "state", state("2"), write, false);
  expect(legacy.status).toBe(409); expect(await legacy.json()).toEqual({ error: "client_update_required" });
  const after = await rows(id);
  expect(after.filter((r: any) => r.key !== "control")).toEqual(before.filter((r: any) => r.key !== "control"));
  const control = (data: any[]) => JSON.parse(data.find(r => r.key === "control").value);
  const changed = control(after); expect(changed.legacyContactExpiresAt).toBeGreaterThan(Date.now());
  delete changed.legacyContactExpiresAt; expect(changed).toEqual(control(before));
  expect((await publish(id, "state", state("2"), read)).status).toBe(403);
  expect((await publish(id, "publisher", undefined, write, false)).status).toBe(409);
  expect(await snapshot()).toEqual(ledger);
});

it("authenticates a viewer after recovery with candidate-written snapshot and original presence lease", async () => {
  await start(); const id = (await create()).body.overlayId; await acquire(id); await publish(id, "state", state());
  const before = await rows(id); await switchTo();
  const result = await mf!.dispatchFetch(`https://overlay.example.invalid/api/v1/overlays/${id}/live`, {
    headers: { Upgrade: "websocket", Origin: "https://overlay.example.invalid" } });
  expect(result.status).toBe(101); const socket = result.webSocket!; socket.accept();
  const next = new Promise<string>(resolve => socket.addEventListener("message", e => resolve(String(e.data)), { once: true }));
  socket.send(JSON.stringify({ v: 1, type: "auth", protocolVersion: 2, readCapability: read }));
  const control = JSON.parse(before.find((r: any) => r.key === "control").value);
  expect(JSON.parse(await next)).toMatchObject({ v: 2, death: state().death,
    presence: { confirmed: true, leaseExpiresAt: control.leaseExpiresAt } });
  socket.close(); expect(await rows(id)).toEqual(before);
});

it("completes a candidate-written reserved allocation with the same identity and verifier binding", async () => {
  await start({ failInit: true }); expect((await create()).status).toBe(409);
  const before = await snapshot(), reserved = before.allocations.find((r: any) => r.request_id === claim().requestId);
  expect(reserved.phase).toBe("reserved"); await switchTo("recovery", {});
  expect((await create()).body.overlayId).toBe(reserved.overlay_id);
  const after = await snapshot(); expect(after.allocations.length).toBe(before.allocations.length);
  expect(after.allocations.find((r: any) => r.request_id === claim().requestId)).toEqual({ ...reserved, phase: "active" });
  expect(after.authority_configuration).toEqual(before.authority_configuration);
  const a = await authority(); expect(await a.provision({ ...claim(), readVerifier: "f".repeat(64) })).toEqual({ ok: false });
});

it.each([undefined, { ...policy, revision: 2 }, policy])("preserves candidate-applied policy and consumption, rejecting new allocations with config %j", async configured => {
  await start({ keys: true }); const id = (await create()).body.overlayId;
  await switchTo("candidate", { keys: true, policy });
  expect(await (await authority()).applyConfiguredPolicy(applyKey, 0)).toMatchObject({ ok: true });
  expect((await create(2)).status).toBe(200); const before = await snapshot();
  expect(before.allocation_policy[0].window_count).toBe(1);
  await switchTo("recovery", { keys: true, policy: configured });
  expect((await create(3)).status).toBe(409); expect(await snapshot()).toEqual(before);
  expect((await create()).body.overlayId).toBe(id); expect(await snapshot()).toEqual(before);
  const status = await (await authority()).policyStatus(readKey);
  expect(status.report).toMatchObject({ new_allocation_state: "blocked", persisted_revision: 1, window_count: 1 });
  expect(await snapshot()).toEqual(before);
});

it.each(["empty", "corrupt"])("denies staged policy and %s policy storage without initializing or resetting policy", async kind => {
  await start(); await create(); await switchTo("recovery", { policy }); const before = await snapshot();
  expect((await create(2)).status).toBe(409); expect(await snapshot()).toEqual(before);
  await switchTo("recovery", {}); await (await authority()).fixturePolicyTable(kind);
  const empty = await snapshot(); expect((await create(2)).status).toBe(409); expect(await snapshot()).toEqual(empty);
});

it("retains explicit policy authorization, predecessor checks and fail-closed role separation", async () => {
  await start({ keys: true }); await create(); await switchTo("recovery", { keys: true, policy }); const before = await snapshot();
  const a = await authority();
  for (const key of [undefined, "forged", readKey]) expect(await a.applyConfiguredPolicy(key, 0)).toEqual({ ok: false, error: "forbidden" });
  for (const key of [undefined, "forged", applyKey]) expect(await a.policyStatus(key)).toEqual({ ok: false, error: "forbidden" });
  expect(await snapshot()).toEqual(before);
  expect(await a.applyConfiguredPolicy(applyKey, 2)).toMatchObject({ ok: false });
  expect(await a.applyConfiguredPolicy(applyKey, 0)).toMatchObject({ ok: true, status: "applied" });
  const applied = await snapshot(); expect(await a.applyConfiguredPolicy(applyKey, 0)).toMatchObject({ ok: true, status: "already_applied" });
  expect((await create(2)).status).toBe(409); expect(await snapshot()).toEqual(applied);
});

it("restores public creation after a regression in candidate-only allocation admission", async () => {
  await start(); const original = await create(); expect(original.status).toBe(200);
  await switchTo("fault"); expect((await create(2)).status).toBe(503); const before = await snapshot();
  await switchTo(); const restored = await create(2); expect(restored.status).toBe(200);
  expect((await create()).body.overlayId).toBe(original.body.overlayId);
  expect((await snapshot()).allocations.length).toBe(before.allocations.length + 1);
});

it("keeps operator operations outside public HTTP routes and denies policy calls without keys", async () => {
  await start(); await create(); await switchTo(); const before = await snapshot(); const a = await authority();
  expect(await a.applyConfiguredPolicy(applyKey, 0)).toEqual({ ok: false, error: "forbidden" });
  expect(await a.policyStatus(readKey)).toEqual({ ok: false, error: "forbidden" });
  for (const operation of ["status", "apply"])
    expect((await (await mf!.getWorker("public")).fetch(`https://overlay.example.invalid/operator/policy/${operation}`)).status).toBe(404);
  expect(await snapshot()).toEqual(before);
});

it("preserves the historical ceiling and fails closed on seed drift without changing retained rows", async () => {
  await start({ ceiling: "2" }); const original = await create(); expect(original.status).toBe(200);
  await switchTo("recovery", { ceiling: "2" }); const before = await snapshot();
  expect((await create(2)).status).toBe(409); expect(await snapshot()).toEqual(before);
  expect((await create()).body.overlayId).toBe(original.body.overlayId);
  await switchTo("recovery", { ceiling: "2", seeds: ["2".repeat(32)] });
  expect((await create(2)).status).toBe(409); expect(await snapshot()).toEqual(before);
});

// Type-check the separate recovery authority alongside the existing project.
const recoveryType: typeof RecoveryAuthority | undefined = undefined;
void recoveryType;
