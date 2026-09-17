import { test, expect } from "@playwright/test";

test.describe.configure({ mode: "serial" });
const origin = "https://localhost:8799";
test.use({ ignoreHTTPSErrors: true });
let credentials: { id: string; read: string; write: string };
let sequence: number;
test.beforeEach(async ({ request }) => {
  credentials = await (await request.post(`${origin}/__test/provision`)).json(); sequence = 0;
  const acquired = await request.post(`${origin}/api/v1/overlays/${credentials.id}/session`, {
    headers: { Authorization: `Bearer ${credentials.write}` }, data: { v: 1, expectedEpoch: "0", sessionRequestId: "3".repeat(32) }
  });
  expect(acquired.status()).toBe(200);
});
const address = () => `${origin}/overlay/#id=${credentials.id}&read=${credentials.read}`;
const style = { enabled: true, title: "Total Deaths", fontFamily: "Arial", fontSize: 24, textColor: "#F7F6FF", textOpacity: 100,
  backgroundColor: "#15171B", backgroundOpacity: 0, padding: 0, cornerRadius: 0, outlineEnabled: true, outlineColor: "#000000", outlineWidth: 0,
  shadowEnabled: false, shadowColor: "#000000", shadowOffsetX: 2, shadowOffsetY: 2, shadowBlur: 4, titleIconMode: "off", iconColor: "#FFFFFF" };
async function publish(request: any, channels: object) {
  const response = await request.put(`${origin}/api/v1/overlays/${credentials.id}/state`, {
    headers: { Authorization: `Bearer ${credentials.write}` }, data: { v: 1, epoch: "1", sessionRequestId: "3".repeat(32), sequence: String(++sequence), ...channels }
  });
  expect(response.status()).toBe(200);
}
// The hosted wire carries display values, not the local game or reader identity.
for (const value of ["7", "42", "0"]) {
  test(`retained counter rendering displays ${value} through the hosted transport`, async ({ page, request }) => {
    await publish(request, { death: { value, availability: "available" } });
    await page.goto(address());
    await expect(page.getByTestId("total-deaths-overlay")).toHaveText(`Total Deaths: ${value}`);
  });
}

test("real Worker hydrates transparently then pushes exact totals and reloads without Desktop", async ({ page, request }) => {
  const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
  page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
  await page.goto(address());
  await expect(page.locator("#souls-tracker-overlay")).toBeEmpty();
  await publish(request, { death: { value: "9223372036854775807", availability: "available" } });
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 9223372036854775807");
  await page.reload();
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 9223372036854775807");
  expect(errors).toEqual([]);
});

test("reconnects to durable state while retaining the DOM through connection loss", async ({ page, request }) => {
  await publish(request, { death: { value: "42", availability: "available" } });
  await page.goto(address()); await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 42");
  await page.context().setOffline(true);
  await request.post(`${origin}/__test/disconnect`);
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 42");
  await publish(request, { death: { value: "0", availability: "available" } });
  await page.context().setOffline(false);
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 0", { timeout: 10000 });
  await publish(request, { death: { value: null, availability: "unavailable" } });
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: Unavailable");
  await request.post(`${origin}/__test/disconnect`);
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: Unavailable");
  await publish(request, { appearance: { ...style, enabled: false } });
  await expect(page.locator("#souls-tracker-overlay")).toBeEmpty();
  await request.post(`${origin}/__test/disconnect`);
  await expect(page.locator("#souls-tracker-overlay")).toBeEmpty();
});

for (const titleIconMode of ["off", "prefixSkull", "skullOnly"]) for (const title of ["Custom", ""]) {
  test(`real appearance ${titleIconMode} with ${title ? "title" : "blank title"}`, async ({ page, request }) => {
    const errors: string[] = [];
    page.on("pageerror", error => errors.push(error.message));
    page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
    await publish(request, { death: { value: "9007199254740993", availability: "available" }, appearance: { ...style, title, titleIconMode,
      fontFamily: "Missing Synthetic Font", fontSize: 64, textColor: "#12AB34", textOpacity: 45,
      backgroundColor: "#345678", backgroundOpacity: 25, padding: 12, cornerRadius: 8,
      outlineWidth: 2, shadowEnabled: true, shadowOffsetX: -3, shadowOffsetY: 4, shadowBlur: 6, iconColor: "#FFAA33" } });
    await page.goto(address());
    const panel = page.getByTestId("total-deaths-overlay");
    const heading = title !== "" || titleIconMode === "skullOnly";
    await expect(panel).toHaveText(title && titleIconMode !== "skullOnly" ? "Custom: 9007199254740993" : "9007199254740993");
    await expect(panel).toHaveCSS("font-size", "64px");
    await expect(panel).toHaveCSS("font-family", '"Missing Synthetic Font", sans-serif');
    await expect(panel).toHaveCSS("color", "rgb(18, 171, 52)");
    await expect(panel).toHaveCSS("background-color", "rgba(52, 86, 120, 0.25)");
    await expect(panel).toHaveCSS("padding", "12px"); await expect(panel).toHaveCSS("border-radius", "8px");
    await expect(panel).toHaveCSS("text-align", "left");
    await expect(panel.locator(heading ? "h1" : "p")).toHaveCSS("opacity", "0.45");
    if (!heading) await expect(panel.locator("p")).toHaveCSS("font-size", "40px");
    expect(await panel.evaluate(node => getComputedStyle(node).textShadow.includes("-3px 4px 6px"))).toBe(true);
    const skull = panel.locator("img");
    await expect(skull).toHaveCount(heading && titleIconMode !== "off" ? 1 : 0);
    if (await skull.count()) {
      await expect.poll(() => skull.evaluate((node: HTMLImageElement) => node.complete && node.naturalWidth > 0)).toBe(true);
      await expect(page.locator("feComposite[operator=out]")).toHaveCount(1);
      await expect(page.locator("feDropShadow")).toHaveAttribute("stdDeviation", "3");
    }
    const bounds = await panel.boundingBox(); expect(bounds?.x).toBe(0); expect(bounds?.y).toBe(0);
    expect(errors).toEqual([]);
  });
}

test("credential-free shell and hashed assets have cache and same-origin security headers", async ({ request }) => {
  const shell = await request.get(`${origin}/overlay/`); expect(shell.status()).toBe(200);
  expect(shell.headers()["cache-control"]).toBe("public, max-age=0, must-revalidate");
  expect(shell.headers()["etag"]).toBeTruthy();
  expect(shell.headers()["referrer-policy"]).toBe("no-referrer");
  expect(shell.headers()["x-content-type-options"]).toBe("nosniff");
  expect(shell.headers()["content-security-policy"]).toContain("connect-src 'self'");
  const text = await shell.text();
  expect(text.includes(credentials.read) || text.includes(credentials.write)).toBe(false);
  const cached = await request.get(`${origin}/overlay/`, { headers: { "If-None-Match": shell.headers()["etag"] } });
  expect(cached.status()).toBe(304);
  const pending = [...text.matchAll(/(?:src|href)="(\/assets\/[^\"]+)"/g)].map(match => match[1]);
  const seen = new Set<string>();
  while (pending.length) {
    const path = pending.pop()!; if (seen.has(path)) continue; seen.add(path);
    expect(path).toMatch(/\.[a-f0-9]{64}\.(js|css|png)$/);
    const asset = await request.get(origin + path); expect(asset.status()).toBe(200);
    expect(asset.headers()["cache-control"]).toBe("public, max-age=31536000, immutable");
    if (!path.endsWith(".png")) {
      const content = await asset.text(); expect(content.includes(credentials.read) || content.includes(credentials.write)).toBe(false);
      pending.push(...[...content.matchAll(/"(\/assets\/[^\"]+)"/g)].map(match => match[1]));
    }
  }
  expect(seen.size).toBe(6);
  const publisher = await request.get(`${origin}/api/v1/overlays/${credentials.id}/publisher`, { headers: { Authorization: `Bearer ${credentials.write}` } });
  expect(publisher.headers()["cache-control"]).toBe("no-store");
  expect(publisher.headers()["access-control-allow-origin"]).toBeUndefined();
  expect(publisher.headers()["set-cookie"]).toBeUndefined();
});

test("URL styles and duplicate credentials do not open a read connection or render", async ({ page }) => {
  let sockets = 0; page.on("websocket", () => sockets++);
  await page.goto(address() + "&title=Changed");
  await expect(page.locator("#souls-tracker-overlay")).toBeEmpty();
  await page.goto(address() + "&read=invalid");
  await expect(page.locator("#souls-tracker-overlay")).toBeEmpty();
  expect(sockets).toBe(0);
});

test("a corrected read fragment starts one new client after invalid credentials", async ({ page, request }) => {
  await publish(request, { death: { value: "7", availability: "available" } });
  const opened = page.waitForEvent("websocket");
  await page.goto(`${origin}/overlay/#id=${credentials.id}&read=${"0".repeat(64)}`);
  const socket = await opened; if (!socket.isClosed()) await socket.waitForEvent("close");
  await expect(page.locator("#souls-tracker-overlay")).toBeEmpty();
  let reconnects = 0; page.on("websocket", () => reconnects++);
  await page.evaluate(fragment => { window.location.hash = fragment; }, `id=${credentials.id}&read=${credentials.read}`);
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 7");
  expect(reconnects).toBe(1);
});
