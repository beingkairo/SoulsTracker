import { test, expect } from "@playwright/test";

test.describe.configure({ mode: "serial" });
const origin = "https://localhost:8799";
test.use({ ignoreHTTPSErrors: true });
type Credentials = { id: string; read: string; write: string };
let credentials: Credentials;
let sequence: number;
async function provision(request: any): Promise<Credentials> {
  const response = await request.post(`${origin}/__test/provision`);
  expect(response.status()).toBe(200);
  return response.json();
}
async function acquire(request: any, target: Credentials): Promise<void> {
  const acquired = await request.post(`${origin}/api/v1/overlays/${target.id}/session`, {
    headers: { Authorization: `Bearer ${target.write}` }, data: { v: 1, expectedEpoch: "0", sessionRequestId: "3".repeat(32) }
  });
  expect(acquired.status()).toBe(200);
}
test.beforeEach(async ({ request }) => {
  credentials = await provision(request); sequence = 0;
  await acquire(request, credentials);
});
const address = () => `${origin}/overlay/#id=${credentials.id}&read=${credentials.read}`;
test("hosted viewport centers complete painted content at intrinsic scale", async ({ page, request }, testInfo) => {
  await page.setViewportSize({ width: 640, height: 360 });
  await publish(request, { death: { value: "42", availability: "available" }, appearance: { ...style, padding: 12, backgroundOpacity: 100 } });
  await page.goto(address());
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 42");
  await expect.poll(() => page.locator("h1").evaluate(node => (node as HTMLElement).style.marginLeft)).not.toBe("");
  const pixels = await page.screenshot({ omitBackground: true });
  await testInfo.attach("hosted-viewport", { body: pixels, contentType: "image/png" });
  const paint = await paintedBounds(page, pixels.toString("base64"));
  expect(Math.abs(paint.left + paint.right - 640)).toBeLessThanOrEqual(2);
  expect(Math.abs(paint.top + paint.bottom - 360)).toBeLessThanOrEqual(2);
});

async function paintedBounds(page: any, data: string, crop?: { width: number; height: number }) {
  const probe = await page.context().newPage();
  try { return await probe.evaluate(async ({ data, crop }: { data: string; crop?: { width: number; height: number } }) => {
    const image = new Image(); image.src = `data:image/png;base64,${data}`; await image.decode();
    const width = crop?.width ?? image.width, height = crop?.height ?? image.height;
    const canvas = new OffscreenCanvas(width, height); const context = canvas.getContext("2d")!;
    context.drawImage(image, (width - image.width) / 2, (height - image.height) / 2);
    const pixels = context.getImageData(0, 0, width, height).data;
    let left = width, top = height, right = 0, bottom = 0, maxAlpha = 0;
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      const alpha = pixels[(y * width + x) * 4 + 3]; maxAlpha = Math.max(maxAlpha, alpha);
      if (alpha < 2) continue;
      left = Math.min(left, x); top = Math.min(top, y); right = Math.max(right, x + 1); bottom = Math.max(bottom, y + 1);
    }
    return { left, top, right, bottom, maxAlpha };
  }, { data, crop }); } finally { await probe.close(); }
}
async function expectIntrinsicPlacement(page: any) {
  const placement = await page.getByTestId("total-deaths-overlay").evaluate((node: HTMLElement) => {
    const r = node.getBoundingClientRect(); const transform = new DOMMatrix(getComputedStyle(node).transform);
    return { x: r.x - transform.e, y: r.y - transform.f, width: r.width, height: r.height, viewportWidth: innerWidth, viewportHeight: innerHeight, scale: transform.a };
  });
  expect(placement.x).toBeCloseTo((placement.viewportWidth - placement.width) / 2, 1);
  expect(placement.y).toBeCloseTo((placement.viewportHeight - placement.height) / 2, 1);
  expect(placement.scale).toBe(1);
}
test("hosted transparent composition centers glyphs and directional effects", async ({ page, request }, testInfo) => {
  await page.setViewportSize({ width: 640, height: 360 });
  await publish(request, { death: { value: "0", availability: "available" }, appearance: { ...style, title: "", fontSize: 48,
    shadowEnabled: true, shadowBlur: 0, shadowOffsetX: -20, shadowOffsetY: 20 } });
  await page.goto(address());
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("0");
  await expect.poll(() => page.locator("p").evaluate(node => (node as HTMLElement).style.marginLeft)).not.toBe("");
  const pixels = await page.screenshot({ omitBackground: true });
  await testInfo.attach("hosted-effects", { body: pixels, contentType: "image/png" });
  const paint = await paintedBounds(page, pixels.toString("base64"));
  expect(Math.abs(paint.left + paint.right - 640)).toBeLessThanOrEqual(2);
  expect(Math.abs(paint.top + paint.bottom - 360)).toBeLessThanOrEqual(2);
});
test("background follows current content and skull gaps match", async ({ page, request }, testInfo) => {
  await page.setViewportSize({ width: 4096, height: 720 });
  await publish(request, { death: { value: "7", availability: "available" } });
  await page.goto(address());
  const panel = page.getByTestId("total-deaths-overlay");
  const measurements = [];
  for (const title of ["A long synthetic counter title", "X", ""]) {
    for (const titleIconMode of ["off", "prefixSkull", "skullOnly"]) {
      await publish(request, { appearance: { ...style, title, titleIconMode, padding: 12, backgroundOpacity: 100 } });
      await expect(panel).toHaveText(title && titleIconMode !== "skullOnly" ? `${title}: 7` : "7");
      await expect(panel).toHaveCSS("padding", "12px");
      if (await panel.locator("img").count()) await expect.poll(() => panel.locator("img").evaluate((node: HTMLImageElement) => node.complete && node.naturalWidth > 0)).toBe(true);
      const metrics = await panel.evaluate(node => {
        const content = node.querySelector("h1, p")!;
        const text = document.createRange(); text.selectNodeContents(content.lastChild!);
        text.setStart(content.lastChild!, (content.lastChild!.textContent ?? "").length - (content.lastChild!.textContent ?? "").trimStart().length);
        const glyph = text.getBoundingClientRect();
        const image = content.querySelector("img")?.getBoundingClientRect();
        const bounds = node.getBoundingClientRect();
        return { width: bounds.width, leftBearing: Number.parseFloat(getComputedStyle(content).marginLeft), rightBearing: Number.parseFloat(getComputedStyle(content).marginRight), rightPadding: bounds.right - glyph.right,
          leftPadding: (image?.left ?? glyph.left) - bounds.left, gap: image ? glyph.left - image.right : null };
      });
      measurements.push({ title, titleIconMode, ...metrics });
      // Ink-edge margins supersede advance-box padding; retain configured
      // padding. Independent pixel tests measure the compact Prefix Skull gap.
      expect.soft(metrics.rightPadding - metrics.rightBearing).toBeCloseTo(12, 1);
      expect.soft(metrics.leftPadding - metrics.leftBearing).toBeCloseTo(12, 1);
      await testInfo.attach(`content-${measurements.length}`, { body: await panel.screenshot(), contentType: "image/png" });
    }
  }
  for (const item of measurements.filter(item => item.gap !== null)) {
    // Skull Only retains advance-box adjacency; Prefix Skull now compensates
    // transparent pixels and the title's bearing without changing image size.
    if (item.titleIconMode === "skullOnly") expect.soft(item.gap).toBeCloseTo(0, 2);
    else expect.soft(item.gap!).toBeLessThan(0);
  }
  await testInfo.attach("content-bounds", { body: JSON.stringify(measurements, null, 2), contentType: "application/json" });
});
const style = { enabled: true, title: "Total Deaths", fontFamily: "Arial", fontSize: 24, textColor: "#F7F6FF", textOpacity: 100,
  backgroundColor: "#15171B", backgroundOpacity: 0, padding: 0, cornerRadius: 0, outlineEnabled: true, outlineColor: "#000000", outlineWidth: 0,
  shadowEnabled: false, shadowColor: "#000000", shadowOffsetX: 2, shadowOffsetY: 2, shadowBlur: 4, titleIconMode: "off", iconColor: "#FFFFFF" };
for (const variant of [
  { name: "title zero", value: "0", appearance: {} },
  { name: "blank large", value: "1234567890", appearance: { title: "", fontSize: 96 } },
  { name: "prefix small", value: "42", appearance: { titleIconMode: "prefixSkull", fontSize: 12, padding: 12, backgroundOpacity: 70, textOpacity: 72 } },
  { name: "skull only", value: "123", appearance: { titleIconMode: "skullOnly", padding: 32, backgroundOpacity: 100 } },
  { name: "long large", value: "1234567890", appearance: { title: "W".repeat(40), fontSize: 96, padding: 32, backgroundOpacity: 100 } },
  { name: "outline", value: "42", appearance: { outlineEnabled: true, outlineWidth: 8, fontSize: 48 } },
  { name: "negative shadow", value: "42", appearance: { shadowEnabled: true, shadowBlur: 20, shadowOffsetX: -20, shadowOffsetY: -20 } },
  { name: "positive skull shadow", value: "0", appearance: { titleIconMode: "prefixSkull", shadowEnabled: true, shadowBlur: 20, shadowOffsetX: 20, shadowOffsetY: 20, outlineEnabled: true, outlineWidth: 8 } },
  { name: "tall skull", value: "0", appearance: { titleIconMode: "skullOnly", fontSize: 96, padding: 32, backgroundOpacity: 70, textOpacity: 45 } },
  { name: "Segoe effects", value: "1234567890", appearance: { fontFamily: "Segoe UI", titleIconMode: "prefixSkull", shadowEnabled: true, shadowBlur: 6, shadowOffsetX: -3, shadowOffsetY: 4, padding: 12, backgroundOpacity: 25, textOpacity: 45 } }
]) {
  test(`hosted full-page fitting and oversized placement: ${variant.name}`, async ({ page, request }, testInfo) => {
    const appearance = { ...style, ...variant.appearance };
    await publish(request, { death: { value: variant.value, availability: "available" }, appearance });
    await page.setViewportSize({ width: 6000, height: 1200 });
    await page.goto(address());
    const panel = page.getByTestId("total-deaths-overlay");
    await expect(panel).toBeVisible();
    await expect.poll(() => panel.evaluate(node => (node as HTMLElement).style.transform)).not.toBe("");
    const reference = await panel.boundingBox();
    const referencePixels = (await page.screenshot({ omitBackground: true })).toString("base64");
    const complete = await paintedBounds(page, referencePixels);
    const paintWidth = complete.right - complete.left, paintHeight = complete.bottom - complete.top;
    expect(paintWidth).toBeGreaterThan(0); expect(paintHeight).toBeGreaterThan(0);
    const measurements = [];
    for (const [width, height] of [[125, 100], [256, 100], [800, 155], [1280, 360], [640, 360], [360, 800], [1920, 1080],
      [paintWidth + 4, paintHeight + 4], [Math.max(1, paintWidth - 4), paintHeight + 4],
      [paintWidth + 4, Math.max(1, paintHeight - 4)], [640, 80], [1280, 360]]) {
      await page.setViewportSize({ width, height });
      const geometry = await panel.evaluate(node => {
        const bounds = node.getBoundingClientRect();
        return { x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height, scale: new DOMMatrix(getComputedStyle(node).transform).a,
          documentWidth: document.documentElement.scrollWidth, documentHeight: document.documentElement.scrollHeight,
          clientWidth: document.documentElement.clientWidth, clientHeight: document.documentElement.clientHeight,
          background: getComputedStyle(document.body).backgroundColor, margin: getComputedStyle(document.body).margin,
          overflow: getComputedStyle(document.querySelector("#souls-tracker-overlay")!).overflow };
      });
      const screenshot = await page.screenshot({ omitBackground: true });
      const paint = await paintedBounds(page, screenshot.toString("base64"));
      const projected = await paintedBounds(page, referencePixels, { width, height });
      const fitting = width >= paintWidth && height >= paintHeight;
      measurements.push({ width, height, fitting, paint, geometry, paintWidth, paintHeight });
      await testInfo.attach(`viewport-${width}-${height}`, { body: screenshot, contentType: "image/png" });
      expect.soft(geometry.width).toBeCloseTo(reference!.width, 1); expect.soft(geometry.height).toBeCloseTo(reference!.height, 1);
      expect.soft(geometry.scale).toBe(1);
      expect.soft(geometry.documentWidth).toBe(width); expect.soft(geometry.documentHeight).toBe(height);
      expect.soft(geometry.clientWidth).toBe(width); expect.soft(geometry.clientHeight).toBe(height);
      expect.soft(geometry.background).toBe("rgba(0, 0, 0, 0)"); expect.soft(geometry.margin).toBe("0px");
      // Clipped glyph fragments need not have symmetric ink (for example 3/4).
      // The complete composition retains its centered origin on both axes.
      expect.soft(geometry.x - reference!.x).toBeCloseTo((width - 6000) / 2, 1);
      expect.soft(geometry.y - reference!.y).toBeCloseTo((height - 1200) / 2, 1);
      // Compare actual source pixels with an independent viewport crop of the
      // complete composition, including deliberately oversized configurations.
      for (const edge of ["left", "top", "right", "bottom"] as const)
        expect.soft(Math.abs(paint[edge] - projected[edge])).toBeLessThanOrEqual(2);
      if (fitting) {
        expect.soft(Math.abs(paint.maxAlpha - complete.maxAlpha)).toBeLessThanOrEqual(1);
        expect.soft(Math.abs(paint.left + paint.right - width)).toBeLessThanOrEqual(2);
        expect.soft(Math.abs(paint.top + paint.bottom - height)).toBeLessThanOrEqual(2);
        expect.soft(paint.right - paint.left).toBeGreaterThanOrEqual(paintWidth - 1);
        expect.soft(paint.bottom - paint.top).toBeGreaterThanOrEqual(paintHeight - 1);
      }
    }
    await testInfo.attach("full-page-geometry", { body: JSON.stringify(measurements, null, 2), contentType: "application/json" });
  });
}
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

test("real Worker hydrates missing death as zero then pushes exact totals and reloads without Desktop", async ({ page, request }) => {
  const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
  page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
  await page.goto(address());
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 0");
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
  await request.post(`${origin}/__test/disconnect`, { data: { id: credentials.id } });
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 42");
  await publish(request, { death: { value: "0", availability: "available" } });
  await page.context().setOffline(false);
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 0", { timeout: 10000 });
  await publish(request, { death: { value: null, availability: "unavailable" } });
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 0");
  await request.post(`${origin}/__test/disconnect`, { data: { id: credentials.id } });
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 0");
  await publish(request, { appearance: { ...style, enabled: false } });
  await expect(page.locator("#souls-tracker-overlay")).toBeEmpty();
  await request.post(`${origin}/__test/disconnect`, { data: { id: credentials.id } });
  await expect(page.locator("#souls-tracker-overlay")).toBeEmpty();
});

test("hosted paint survives reload and reconnect without placement drift", async ({ page, request }, testInfo) => {
  await page.setViewportSize({ width: 640, height: 360 });
  await publish(request, { death: { value: "42", availability: "available" }, appearance: { ...style,
    titleIconMode: "prefixSkull", shadowEnabled: true, shadowOffsetX: -20, shadowOffsetY: 20, shadowBlur: 20, textOpacity: 72 } });
  await page.goto(address());
  const panel = page.getByTestId("total-deaths-overlay");
  await expect.poll(() => panel.evaluate(node => (node as HTMLElement).style.transform)).not.toBe("");
  const before = await page.screenshot({ omitBackground: true });
  await page.context().setOffline(true);
  await request.post(`${origin}/__test/disconnect`, { data: { id: credentials.id } });
  expect((await page.screenshot({ omitBackground: true })).equals(before)).toBe(true);
  const hydrated = new Promise<void>(resolve => page.once("websocket", socket => socket.on("framereceived", event => {
    if (String(event.payload).includes('"type":"snapshot"')) resolve();
  })));
  await page.context().setOffline(false);
  await hydrated;
  expect((await page.screenshot({ omitBackground: true })).equals(before)).toBe(true);
  await page.reload();
  await expect.poll(() => panel.evaluate(node => (node as HTMLElement).style.transform)).not.toBe("");
  expect((await page.screenshot({ omitBackground: true })).equals(before)).toBe(true);
  await testInfo.attach("hydrated-reconnected", { body: before, contentType: "image/png" });
});

test("isolated harness identities finish publish after another live session closes", async ({ page, request }) => {
  await publish(request, { death: { value: "7", availability: "available" } });
  const opened = page.waitForEvent("websocket");
  await page.goto(address());
  const firstSocket = await opened;
  await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 7");

  const next = await provision(request);
  expect(next.id).toMatch(/^[0-9a-f]{64}$/);
  expect(next.id).not.toBe(credentials.id);
  await acquire(request, next);

  const closed = firstSocket.waitForEvent("close");
  const disconnected = await request.post(`${origin}/__test/disconnect`, {
    data: { id: credentials.id }, timeout: 10000
  });
  expect(disconnected.status()).toBe(204);
  await closed;

  const completed = await request.put(`${origin}/api/v1/overlays/${next.id}/state`, {
    headers: { Authorization: `Bearer ${next.write}` }, timeout: 10000,
    data: { v: 1, epoch: "1", sessionRequestId: "3".repeat(32), sequence: "1",
      appearance: { ...style, title: "Isolated", fontSize: 24 } }
  });
  expect(completed.status()).toBe(200);
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
    if (!heading) await expect(panel.locator("p")).toHaveCSS("font-size", "160px");
    expect(await panel.evaluate(node => getComputedStyle(node).textShadow.includes("-3px 4px 6px"))).toBe(true);
    const skull = panel.locator("img");
    await expect(skull).toHaveCount(heading && titleIconMode !== "off" ? 1 : 0);
    if (await skull.count()) {
      await expect.poll(() => skull.evaluate((node: HTMLImageElement) => node.complete && node.naturalWidth > 0)).toBe(true);
      await expect(page.locator("feComposite[operator=out]")).toHaveCount(1);
      await expect(page.locator("feDropShadow")).toHaveAttribute("stdDeviation", "3");
    }
    await expectIntrinsicPlacement(page);
    expect(errors).toEqual([]);
  });
}

for (const value of ["0", "42", "9223372036854775807", null]) {
  test(`appearance sizing preserves ${value ?? "Unavailable"} through title and size changes`, async ({ page, request }, testInfo) => {
    test.setTimeout(90000);
    // Leave room for the largest exact total at the maximum selected size.
    await page.setViewportSize({ width: 4096, height: 720 });
    await publish(request, { death: { value, availability: value === null ? "unavailable" : "available" } });
    await page.goto(address());
    const panel = page.getByTestId("total-deaths-overlay");
    const measurements = [];
    for (const variant of [
      { title: "", titleIconMode: "off", padding: 0 },
      { title: "", titleIconMode: "off", padding: 12 },
      { title: "   ", titleIconMode: "off", padding: 12 },
      { title: "", titleIconMode: "prefixSkull", padding: 12 },
      { title: "Custom", titleIconMode: "off", padding: 12 },
      { title: "Custom", titleIconMode: "prefixSkull", padding: 12 },
      { title: "", titleIconMode: "skullOnly", padding: 12 }
    ]) {
      let previous: { width: number; height: number } | undefined;
      for (const fontSize of [12, 24, 48, 96]) {
        await publish(request, { appearance: { ...style, ...variant, fontSize, backgroundOpacity: 100 } });
        await expect(panel).toHaveCSS("font-size", `${fontSize}px`);
        await expect(panel).toHaveCSS("padding", `${variant.padding}px`);
        await expect(panel).toHaveCSS("background-color", "rgb(21, 23, 27)");
        const heading = variant.title.trim() !== "" || variant.titleIconMode === "skullOnly";
        const skull = heading && variant.titleIconMode !== "off";
        const text = value ?? "0";
        await expect(panel).toHaveText(variant.title.trim() && variant.titleIconMode !== "skullOnly" ? `Custom: ${text}` : text);
        await expect(panel.locator("h1")).toHaveCount(heading ? 1 : 0);
        await expect(panel.locator("p")).toHaveCount(heading ? 0 : 1);
        await expect(panel.locator("img")).toHaveCount(skull ? 1 : 0);
        await expect.poll(() => panel.locator("h1, p").evaluate(node => (node as HTMLElement).style.marginLeft)).not.toBe("");
        const metrics = await panel.evaluate(node => {
          const content = node.querySelector("h1, p")!;
          const range = document.createRange(); range.selectNodeContents(content.lastChild!);
          const glyph = range.getBoundingClientRect();
          const bounds = node.getBoundingClientRect();
          const image = node.querySelector("img")?.getBoundingClientRect();
          return { font: parseFloat(getComputedStyle(content).fontSize), minimum: getComputedStyle(node).minWidth,
            x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height,
            textX: glyph.x, textRight: glyph.right, textWidth: glyph.width, textHeight: glyph.height,
            contentHeight: content.getBoundingClientRect().height,
            marginLeft: parseFloat(getComputedStyle(content).marginLeft), marginRight: parseFloat(getComputedStyle(content).marginRight),
            skullWidth: image?.width, skullHeight: image?.height };
        });
        measurements.push({ value, ...variant, fontSize, ...metrics });
        expect.soft(metrics.font).toBeCloseTo(fontSize * (heading ? 1.15 : 2.5), 2);
        await expectIntrinsicPlacement(page);
        expect.soft(metrics.minimum).toBe("0px");
        // Advance boxes include transparent side bearings. Shared background
        // edges now follow ink; independent screenshot tests verify those gaps.
        expect(metrics.textX - metrics.x).toBeGreaterThanOrEqual(variant.padding + metrics.marginLeft - 0.1);
        expect.soft(metrics.textRight - metrics.x).toBeLessThanOrEqual(metrics.width - variant.padding - metrics.marginRight + 0.1);
        if (!heading) {
          expect.soft(metrics.width).toBeCloseTo(metrics.textWidth + metrics.marginLeft + metrics.marginRight + 2 * variant.padding, 1);
          expect(metrics.height).toBeCloseTo(metrics.contentHeight + 2 * variant.padding, 1);
          if (value === "42" && fontSize === 12) expect.soft(metrics.width).toBeLessThan(256);
        } else {
          if (!skull) expect(metrics.width).toBeCloseTo(metrics.textWidth + metrics.marginLeft + metrics.marginRight + 2 * variant.padding, 1);
          if (skull) {
            expect(metrics.skullWidth).toBeCloseTo(fontSize * 1.15 * 2, 1);
            expect(metrics.skullHeight).toBeCloseTo(fontSize * 1.15 * 2, 1);
            await expect(panel.locator("img")).toHaveAttribute("alt", "SoulsTracker skull");
          }
        }
        if (previous) {
          expect.soft(metrics.textWidth).toBeCloseTo(previous.width * 2, 1);
          expect.soft(metrics.textHeight).toBeGreaterThan(previous.height * 1.8);
        }
        previous = { width: metrics.textWidth, height: metrics.textHeight };
        if (!heading && value === "42") {
          await page.setViewportSize({ width: 640, height: 480 });
          const compact = await panel.boundingBox();
          await expectIntrinsicPlacement(page);
          expect(compact?.width).toBeCloseTo(metrics.width, 1);
          await page.setViewportSize({ width: 4096, height: 720 });
        }
      }
    }
    await testInfo.attach("sizing-metrics", { body: JSON.stringify(measurements, null, 2), contentType: "application/json" });
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
