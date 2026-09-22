import { test, expect } from "@playwright/test";
import { readFileSync, mkdtempSync, rmSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import process from "node:process";

const temporary = mkdtempSync(join(tmpdir(), "souls-preview-layout-"));
const documentPath = join(temporary, "preview.html");
execFileSync(process.execPath, [fileURLToPath(new URL("../scripts/build-preview.mjs", import.meta.url)), documentPath, "--no-compile"]);
const html = readFileSync(documentPath, "utf8").replace("<script>", `<script>window.chrome={webview:{addEventListener:(name,fn)=>window.previewMessage=fn,postMessage:()=>{}}};</script><script>`);
const corpus = JSON.parse(readFileSync(new URL("../../tests/fixtures/hosted-overlay/contracts.json", import.meta.url), "utf8"));
test.afterAll(() => rmSync(temporary, { recursive: true, force: true }));

for (const deviceScaleFactor of [1, 2]) {
  test(`measurement matches actual browser paint at DPI ${deviceScaleFactor}`, async ({ browser }, testInfo) => {
    const context = await browser.newContext({ deviceScaleFactor, viewport: { width: 440, height: 180 } });
    try {
      for (const titleIconMode of ["off", "prefixSkull", "skullOnly"]) for (const fontFamily of ["Arial", "Segoe UI"]) {
        const page = await context.newPage();
        await page.setContent(html);
        await page.evaluate(() => {
          const original = CanvasRenderingContext2D.prototype.getImageData;
          CanvasRenderingContext2D.prototype.getImageData = function (...args: Parameters<typeof original>) {
            (window as any).measurement = { png: this.canvas.toDataURL(), width: this.canvas.width, height: this.canvas.height };
            return original.apply(this, args);
          };
        });
        const appearance = { ...corpus.valid[4].appearance, enabled: true, title: "Wgj Deaths", titleIconMode, fontFamily, fontSize: 24, textOpacity: 72,
          padding: 12, backgroundOpacity: titleIconMode === "off" ? 80 : 0, outlineEnabled: true, outlineWidth: 8, shadowEnabled: true, shadowBlur: 20, shadowOffsetX: -20, shadowOffsetY: 20 };
        await page.evaluate(appearance => (window as any).previewMessage({ data: { appearance, value: "123" } }), appearance);
        await expect(page.locator("main")).toHaveAttribute("data-fit", "painted");
        const measurement = await page.evaluate(() => (window as any).measurement);
        // Freeze measurement; compare the actual visible renderer at the scratch
        // image's origin. The independent oracle is a Chromium screenshot.
        await page.evaluate(() => { dispatchEvent(new PageTransitionEvent("pagehide")); document.querySelector<HTMLElement>("main")!.style.transform = "translate(128px, 128px)"; });
        await page.setViewportSize({ width: measurement.width / deviceScaleFactor, height: measurement.height / deviceScaleFactor });
        const screenshot = await page.screenshot({ omitBackground: true });
        const differences = await page.evaluate(async ({ actual, expected }) => {
          const decode = async (src: string) => { const image = new Image(); image.src = src; await image.decode(); const canvas = new OffscreenCanvas(image.width, image.height); const ctx = canvas.getContext("2d")!; ctx.drawImage(image, 0, 0); return ctx.getImageData(0, 0, image.width, image.height).data; };
          const a = await decode(actual), b = await decode(expected);
          let differences = 0;
          for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) differences++;
          return { differences, sameLength: a.length === b.length };
        }, { actual: `data:image/png;base64,${screenshot.toString("base64")}`, expected: measurement.png });
        await testInfo.attach(`${titleIconMode}-${fontFamily}`, { body: screenshot, contentType: "image/png" });
        expect(differences).toEqual({ differences: 0, sameLength: true });
        await page.close();
      }
    } finally { await context.close(); }
  });
}

test("measurement coalesces changes and discards stale work on resize and disposal", async ({ page }) => {
  await page.setContent(html);
  await page.evaluate(() => {
    const decode = HTMLImageElement.prototype.decode;
    (window as any).held = [];
    HTMLImageElement.prototype.decode = function () {
      if (!this.src.startsWith("data:image/svg+xml")) return decode.call(this);
      (window as any).measuringImage = this;
      return new Promise<void>((resolve, reject) => (window as any).held.push(() => decode.call(this).then(resolve, reject)));
    };
  });
  const appearance = { ...corpus.valid[4].appearance, enabled: true, title: "First", titleIconMode: "off", fontFamily: "Arial", fontSize: 24 };
  await page.evaluate(appearance => (window as any).previewMessage({ data: { appearance, value: "0" } }), appearance);
  await expect.poll(() => page.evaluate(() => (window as any).held.length)).toBe(1);
  await page.evaluate(appearance => { for (let i = 0; i < 100; i++) (window as any).previewMessage({ data: { appearance: { ...appearance, title: `Latest ${i}` }, value: "123" } }); }, appearance);
  await page.setViewportSize({ width: 210, height: 180 });
  expect(await page.evaluate(() => (window as any).held.length)).toBe(1);
  await expect(page.locator(".overlay-heading")).toHaveText("First: 0");
  await page.evaluate(() => (window as any).held.shift()());
  await expect(page.locator(".overlay-heading")).toHaveText("Latest 99: 123");
  await expect.poll(() => page.evaluate(() => (window as any).held.length)).toBe(1);
  await page.evaluate(() => (window as any).held.shift()());
  await expect(page.locator("main")).toHaveAttribute("data-fit", "painted");
  await page.evaluate(appearance => (window as any).previewMessage({ data: { appearance: { ...appearance, title: "Closing" }, value: "0" } }), appearance);
  await expect.poll(() => page.evaluate(() => (window as any).held.length)).toBe(1);
  const before = await page.locator("main").getAttribute("style");
  await page.evaluate(() => dispatchEvent(new PageTransitionEvent("pagehide")));
  expect(await page.evaluate(() => (window as any).measuringImage.getAttribute("src"))).toBeNull();
  await page.evaluate(() => (window as any).held.shift()());
  await page.evaluate(async () => { await new Promise(requestAnimationFrame); await new Promise(requestAnimationFrame); });
  expect(await page.locator("main").getAttribute("style")).toBe(before);
  expect(await page.locator("canvas").count()).toBe(0);
});

test("empty and failed measurement stay finite and local", async ({ page }) => {
  const errors: string[] = []; page.on("pageerror", e => errors.push(e.message));
  await page.setContent(html);
  const appearance = { ...corpus.valid[4].appearance, enabled: true, title: "Transparent", titleIconMode: "off", textOpacity: 0, backgroundOpacity: 0 };
  await page.evaluate(appearance => (window as any).previewMessage({ data: { appearance, value: "0" } }), appearance);
  await expect(page.locator("main")).toHaveAttribute("data-fit", "empty");
  await page.evaluate(() => { HTMLCanvasElement.prototype.getContext = (() => null) as any; });
  await page.evaluate(appearance => (window as any).previewMessage({ data: { appearance: { ...appearance, textOpacity: 100 }, value: "123" } }), appearance);
  await expect(page.locator("main")).toHaveAttribute("data-fit", "fallback");
  expect(await page.locator("main").getAttribute("style")).not.toMatch(/NaN|Infinity/);
  await expect(page.locator(".overlay-heading")).toHaveText("Transparent: 123");
  expect(errors).toEqual([]);
});

test("fit waits for fonts and images and refits after resize", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 210, height: 180 });
  await page.setContent(html);
  await page.evaluate(() => {
    Object.defineProperty(document.fonts, "ready", { value: new Promise(resolve => { (window as any).releaseFonts = resolve; }) });
    const decode = HTMLImageElement.prototype.decode;
    HTMLImageElement.prototype.decode = function () {
      if (!this.src.startsWith("data:image/png")) return decode.call(this);
      (window as any).waitingImage = true;
      return new Promise<void>((resolve, reject) => { (window as any).releaseImage = () => decode.call(this).then(resolve, reject); });
    };
  });
  const appearance = { ...corpus.valid[4].appearance, enabled: true, title: "Deaths", titleIconMode: "prefixSkull", fontFamily: "Segoe UI", fontSize: 24, padding: 12, backgroundOpacity: 80 };
  await page.evaluate(appearance => (window as any).previewMessage({ data: { appearance, value: "123" } }), appearance);
  await expect(page.locator("main")).toHaveAttribute("data-fit", "measuring");
  expect(await page.evaluate(() => Boolean((window as any).waitingImage))).toBe(false);
  await page.evaluate(() => (window as any).releaseFonts(document.fonts));
  await expect.poll(() => page.evaluate(() => Boolean((window as any).waitingImage))).toBe(true);
  await expect(page.locator("main")).toHaveAttribute("data-fit", "measuring");
  await page.evaluate(() => { (window as any).releaseImage(); });
  await expect(page.locator("main")).toHaveAttribute("data-fit", "painted");
  const compact = await page.locator("main").getAttribute("style");
  await page.evaluate(() => { (window as any).waitingImage = false; });
  await page.setViewportSize({ width: 440, height: 180 });
  await expect.poll(() => page.evaluate(() => Boolean((window as any).waitingImage))).toBe(true);
  await page.evaluate(() => { (window as any).releaseImage(); });
  await expect(page.locator("main")).toHaveAttribute("data-fit", "painted");
  expect(await page.locator("main").getAttribute("style")).not.toBe(compact);
  const matrix = await page.locator("main").evaluate(node => new DOMMatrix(getComputedStyle(node).transform).a);
  expect(matrix).toBe(1);
  expect(await page.locator("section").evaluate(node => (node as HTMLElement).style.fontSize)).toBe("24px");
  await testInfo.attach("resized", { body: await page.screenshot({ omitBackground: true }), contentType: "image/png" });
});

test("ordinary preview stays native sized and centered", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 440, height: 180 });
  await page.setContent(html);
  const appearance = { ...corpus.valid[4].appearance, enabled: true, title: "Deaths", titleIconMode: "off", fontFamily: "Arial", fontSize: 24, padding: 12, backgroundOpacity: 80, outlineEnabled: false, shadowEnabled: false };
  await page.evaluate(appearance => (window as any).previewMessage({ data: { appearance, value: "123" } }), appearance);
  await expect(page.locator(".overlay-heading")).toHaveText("Deaths: 123");
  await expect(page.locator("main")).toHaveAttribute("data-fit", "painted");
  await page.evaluate(() => document.fonts.ready);
  const metrics = await page.evaluate(() => {
    const panel = document.querySelector<HTMLElement>(".souls-tracker-overlay-panel")!;
    const r = panel.getBoundingClientRect();
    return { viewport: [innerWidth, innerHeight], panel: r.toJSON(), intrinsic: [panel.offsetWidth, panel.offsetHeight], transform: getComputedStyle(document.querySelector("main")!).transform };
  });
  await testInfo.attach("metrics", { body: JSON.stringify(metrics), contentType: "application/json" });
  await testInfo.attach("render", { body: await page.screenshot(), contentType: "image/png" });
  expect(Math.abs(metrics.panel.width - metrics.intrinsic[0])).toBeLessThan(1);
  expect(Math.abs(metrics.panel.x - (440 - metrics.panel.right))).toBeLessThanOrEqual(1);
});

for (const titleIconMode of ["off", "prefixSkull", "skullOnly"]) {
  test(`preview fit contains full effects and centers: ${titleIconMode}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 210, height: 180 });
    await page.setContent(html);
    const records = [];
    for (const fontSize of [12, 24, 96]) for (const title of ["D", "W".repeat(40)]) for (const variant of [0, 1, 2, 3]) {
      const value = ["0", "123", "9223372036854775807", "123"][variant];
      const appearance = { ...corpus.valid[4].appearance, enabled: true, title, titleIconMode, fontFamily: "Arial", fontSize, textOpacity: 100,
        padding: variant % 2 ? 32 : 0, backgroundOpacity: variant % 2 ? 80 : 0, outlineEnabled: true, outlineWidth: variant < 2 ? 0 : 8,
        shadowEnabled: variant > 0, shadowBlur: variant === 1 ? 0 : 20, shadowOffsetX: variant === 2 ? -20 : 20, shadowOffsetY: variant === 3 ? -20 : 20 };
      const original = JSON.stringify(appearance);
      await page.evaluate(payload => (window as any).previewMessage({ data: payload }), { appearance, value });
      await expect(page.locator(".overlay-heading")).toHaveText(titleIconMode === "skullOnly" ? value : `${title}: ${value}`);
      await page.evaluate(async () => { await document.fonts.ready; await Promise.all(Array.from(document.images).map(image => image.decode())); await new Promise(requestAnimationFrame); });
      await expect(page.locator("main")).toHaveAttribute("data-fit", "painted");
      const geometry = await page.evaluate(() => {
        const target = document.querySelector<HTMLElement>("main")!;
        const matrix = new DOMMatrix(getComputedStyle(target).transform);
        const panel = target.querySelector<HTMLElement>("section")!;
        const p = panel.getBoundingClientRect();
        const range = document.createRange(); range.selectNodeContents(panel.firstElementChild!);
        const c = range.getBoundingClientRect();
        return { scale: matrix.a, panel: p.toJSON(), content: c.toJSON(), paddingLeft: getComputedStyle(panel).paddingLeft, paddingRight: getComputedStyle(panel).paddingRight, scrollX, scrollY, overflow: getComputedStyle(document.body).overflow, fontSize: panel.style.fontSize };
      });
      const pixels = await page.screenshot({ omitBackground: true });
      const painted = await page.evaluate(async data => {
        const image = new Image(); image.src = `data:image/png;base64,${data}`; await image.decode();
        const canvas = new OffscreenCanvas(image.width, image.height); const context = canvas.getContext("2d")!;
        context.drawImage(image, 0, 0); const rgba = context.getImageData(0, 0, image.width, image.height).data;
        let left = image.width, top = image.height, right = 0, bottom = 0;
        for (let y = 0; y < image.height; y++) for (let x = 0; x < image.width; x++) if (rgba[(y * image.width + x) * 4 + 3] > 1) {
          left = Math.min(left, x); right = Math.max(right, x + 1); top = Math.min(top, y); bottom = Math.max(bottom, y + 1);
        }
        return { left, top, right, bottom };
      }, pixels.toString("base64"));
      records.push({ fontSize, title, variant, value, geometry, painted });
      await testInfo.attach(`metrics-${fontSize}-${title.length}-${variant}`, { body: JSON.stringify(records.at(-1)), contentType: "application/json" });
      await testInfo.attach(`${fontSize}-${title.length}-${variant}`, { body: pixels, contentType: "image/png" });
      expect(geometry.scale).toBeGreaterThan(0); expect(geometry.scale).toBeLessThanOrEqual(1);
      expect(geometry.fontSize).toBe(`${fontSize}px`);
      expect(geometry.paddingLeft).toBe(geometry.paddingRight);
      expect(geometry.scrollX).toBe(0); expect(geometry.scrollY).toBe(0); expect(geometry.overflow).toBe("hidden");
      expect(painted.right).toBeGreaterThan(painted.left); expect(painted.bottom).toBeGreaterThan(painted.top);
      expect(painted.left).toBeGreaterThanOrEqual(6); expect(painted.top).toBeGreaterThanOrEqual(6);
      expect(painted.right).toBeLessThanOrEqual(204); expect(painted.bottom).toBeLessThanOrEqual(174);
      // One CSS pixel rasterization plus a small glyph/effect optical tolerance.
      expect(Math.abs(painted.left - (210 - painted.right))).toBeLessThanOrEqual(6);
      // A clamped design fills at least one available dimension, within the
      // same six-pixel raster tolerance, rather than using blanket shrinking.
      if (geometry.scale < 1) expect(Math.max((painted.right - painted.left) / 194, (painted.bottom - painted.top) / 164)).toBeGreaterThanOrEqual(1 - 6 / 164);
      expect(JSON.stringify(appearance)).toBe(original);
    }
    await testInfo.attach("geometry", { body: JSON.stringify(records), contentType: "application/json" });
  });
}
