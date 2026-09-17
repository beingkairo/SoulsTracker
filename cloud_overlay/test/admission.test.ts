import { expect, it, vi } from "vitest";
import worker, { type Env } from "../src/index";

const id = "1".repeat(32);
const request = () => new Request(`https://overlay.test/api/v1/overlays/${id}/publisher`, {
  headers: { Authorization: `Bearer ${"3".repeat(64)}` }
});

it("denies publisher admission before namespace access with a private bounded retry", async () => {
  const getByName = vi.fn(() => ({ fetch: vi.fn(() => new Response(null, { status: 204 })) }));
  const limiter = { limit: vi.fn(async () => ({ success: false })) };
  const environment = { OVERLAYS: { getByName }, PROVISIONED_IDS: [id], BROWSER_ORIGIN: "https://overlay.test",
    PUBLISHER_RATE_LIMITER: limiter } as unknown as Env;
  const result = await worker.fetch(request(), environment);
  expect(result.status).toBe(429);
  expect(getByName).not.toHaveBeenCalled();
  expect(limiter.limit).toHaveBeenCalledWith({ key: `overlay-v1:${id}` });
  expect(result.headers.get("Retry-After")).toBe("60");
  expect(result.headers.get("Cache-Control")).toBe("no-store");
  expect(await result.json()).toEqual({ error: "rate_limited" });
});
