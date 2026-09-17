import { expect, it, vi } from "vitest";
import worker, { type Env } from "../src/index";
const id = "1".repeat(32), otherId = "2".repeat(32);
const actions = { publisher: "GET", session: "POST", state: "PUT", credentials: "POST", live: "GET" };
function makeRequest(action: keyof typeof actions, identity = id) {
  return new Request(`https://overlay.test/api/v1/overlays/${identity}/${action}`, {
    method: actions[action], headers: action === "live"
      ? { Upgrade: "websocket", Origin: "https://overlay.test" }
      : { Authorization: `Bearer ${"3".repeat(64)}`, "Content-Type": "application/json" }
  });
}
function harness() {
  // Exact counters exist only in this deterministic router double, not production.
  const budget = (maximum: number) => {
    const counts = new Map<string, number>();
    return { limit: vi.fn(async ({ key }: { key: string }) => {
      const count = (counts.get(key) ?? 0) + 1;
      counts.set(key, count);
      return { success: count <= maximum };
    }) };
  };
  const fetch = vi.fn(async () => new Response(null, { status: 204 }));
  const getByName = vi.fn(() => ({ fetch }));
  const publisher = budget(60), live = budget(120);
  const environment = { OVERLAYS: { getByName }, PROVISIONED_IDS: [id, otherId], BROWSER_ORIGIN: "https://overlay.test",
    PUBLISHER_RATE_LIMITER: publisher, LIVE_RATE_LIMITER: live } as unknown as Env;
  return { environment, publisher, live, getByName, fetch };
}

it("aggregates alternating publisher endpoints per overlay independently of live admission", async () => {
  const h = harness();
  const publisherActions = ["publisher", "session", "state", "credentials"] as const;
  for (let i = 0; i < 60; i++)
    expect((await worker.fetch(makeRequest(publisherActions[i % 4]), h.environment)).status).toBe(204);
  for (const action of publisherActions)
    expect((await worker.fetch(makeRequest(action), h.environment)).status).toBe(429);
  expect(h.getByName).toHaveBeenCalledTimes(60);
  expect((await worker.fetch(makeRequest("publisher", otherId), h.environment)).status).toBe(204);
  for (let i = 0; i < 120; i++)
    expect((await worker.fetch(makeRequest("live"), h.environment)).status).toBe(204);
  expect((await worker.fetch(makeRequest("live"), h.environment)).status).toBe(429);
  expect((await worker.fetch(makeRequest("live", otherId), h.environment)).status).toBe(204);
  expect((await worker.fetch(makeRequest("state", otherId), h.environment)).status).toBe(204);
  expect(h.getByName).toHaveBeenCalledTimes(183);
  for (const binding of [h.publisher, h.live])
    expect(new Set(binding.limit.mock.calls.map(([input]) => input.key))).toEqual(new Set([`overlay-v1:${id}`, `overlay-v1:${otherId}`]));
});

it("rejects cheap invalid routes methods origins and headers before either budget", async () => {
  const h = harness();
  const url = `https://overlay.test/api/v1/overlays/${id}`;
  const invalid = [
    new Request(`${url}/publisher?token=synthetic`), new Request(`${url}/publisher`, { method: "DELETE" }),
    new Request(`${url}/publisher`), makeRequest("publisher", "0".repeat(32)),
    new Request(`${url}/live`), new Request(`${url}/live`, { headers: { Upgrade: "websocket", Origin: "https://wrong.test" } }),
    new Request(`http://overlay.test/api/v1/overlays/${id}/publisher`),
    new Request(`${url}/publisher`, { headers: { Authorization: "Bearer malformed" } }),
    new Request(`https://wrong.test/api/v1/overlays/${id}/publisher`, { headers: { Authorization: `Bearer ${"3".repeat(64)}` } })
  ];
  for (const name of ["Cookie", "Authorization", "Sec-WebSocket-Protocol"])
    invalid.push(new Request(`${url}/live`, { headers: { Upgrade: "websocket", Origin: "https://overlay.test", [name]: "synthetic" } }));
  for (const request of invalid) expect((await worker.fetch(request, h.environment)).status).toBeGreaterThanOrEqual(400);
  expect(h.publisher.limit).not.toHaveBeenCalled(); expect(h.live.limit).not.toHaveBeenCalled();
  expect(h.getByName).not.toHaveBeenCalled();
});

for (const action of Object.keys(actions) as Array<keyof typeof actions>) {
  for (const mode of ["denied", "missing", "throws", "malformed"] as const) {
    it(`fails closed for ${action} ${mode} admission without namespace or response leakage`, async () => {
      const h = harness();
      const binding = action === "live" ? "LIVE_RATE_LIMITER" : "PUBLISHER_RATE_LIMITER";
      const replacement = mode === "missing" ? undefined : { limit: async () => {
        if (mode === "throws") throw new Error(`private ${id}`);
        return mode === "malformed" ? {} : { success: false };
      } };
      const result = await worker.fetch(makeRequest(action), { ...h.environment, [binding]: replacement } as Env);
      expect(result.status).toBe(mode === "denied" ? 429 : 503);
      expect(await result.text()).toBe(JSON.stringify({ error: mode === "denied" ? "rate_limited" : "admission_unavailable" }));
      expect(Object.fromEntries(result.headers)).toEqual({
        "cache-control": "no-store", "content-type": "application/json", "referrer-policy": "no-referrer",
        "retry-after": "60", "x-content-type-options": "nosniff"
      });
      expect(h.getByName).not.toHaveBeenCalled(); expect(h.fetch).not.toHaveBeenCalled();
    });
  }
}
