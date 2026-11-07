import { test, expect, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";

// Exercise the production renderer and styles without provisioning or transport.
const renderer = readFileSync(new URL("../dist/src/hosted-renderer.js", import.meta.url), "utf8")
  .replace("export function renderHosted", "function renderHosted");
const css = readFileSync(new URL("../src/overlay.css", import.meta.url), "utf8");
const skull = `data:image/png;base64,${readFileSync(new URL("../../assets/branding/souls-tracker-skull.png", import.meta.url), "base64")}`;
const corpus = JSON.parse(readFileSync(new URL("../../tests/fixtures/hosted-overlay/contracts.json", import.meta.url), "utf8"));
const appearance = { ...corpus.valid[4].appearance, enabled: true, title: "Total Deaths", fontFamily: "Arial", fontSize: 24,
  padding: 12, backgroundOpacity: 0, titleIconMode: "off", outlineEnabled: true, outlineWidth: 2,
  shadowEnabled: true, shadowOffsetX: -3, shadowOffsetY: 4, shadowBlur: 6 };

async function boot(page: Page) {
  await page.setViewportSize({ width: 640, height: 360 });
  await page.setContent(`<html class="hosted-overlay"><head><style>${css}</style></head><body><main id="souls-tracker-overlay"></main><script>${renderer}</script></body></html>`);
}
async function render(page: Page, a: object, value = "9", status = "live") {
  await page.evaluate(async ({ a, value, status, skull }) => {
    await (window as any).renderHosted(document.querySelector("main"), { value }, a, skull, status);
  }, { a, value, status, skull });
}

for (const mode of ["off", "prefixSkull", "skullOnly", "blank"]) {
  test(`every visible frame retains final placement through repeated and digit-count updates: ${mode}`, async ({ page }) => {
    await boot(page);
    const a = { ...appearance, title: mode === "blank" ? "" : appearance.title, titleIconMode: mode === "blank" ? "off" : mode };
    await render(page, a);
    await page.evaluate(() => {
      // Delay readiness across actual paint opportunities. A replacement must
      // never expose its unmeasured layout, even when readiness takes frames.
      Object.defineProperty(document.fonts, "ready", { get: () => new Promise(resolve => {
        let frames = 4;
        const next = () => --frames ? requestAnimationFrame(next) : resolve(document.fonts);
        requestAnimationFrame(next);
      }) });
      const decode = HTMLImageElement.prototype.decode;
      HTMLImageElement.prototype.decode = function () {
        return decode.call(this).then(() => new Promise<void>(resolve => {
          requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
        }));
      };
    });
    for (const value of ["8", "9", "10", "11", "99", "100", "101", "9", "99", "100"]) {
      const frames = await page.evaluate(async ({ a, value, skull }) => {
        const target = document.querySelector("main")!;
        const sample = () => {
          const panel = target.querySelector<HTMLElement>("section")!;
          const content = panel.querySelector<HTMLElement>("h1, p")!;
          const range = document.createRange(); range.selectNodeContents(content.lastChild!);
          const r = range.getBoundingClientRect();
          return { text: panel.textContent, x: r.x, y: r.y, width: r.width, height: r.height,
            transform: panel.style.transform, aligned: content.style.marginLeft };
        };
        const ready = (window as any).renderHosted(target, { value }, a, skull);
        const frames = [];
        for (let i = 0; i < 8; i++) { await new Promise(requestAnimationFrame); frames.push(sample()); }
        await ready;
        return { frames, final: sample() };
      }, { a, value, skull });
      // Center anchoring allows width to grow with another digit. Every frame
      // of that value must already match its final text location and dimensions.
      for (const frame of frames.frames) {
        expect(frame.text).toBe(frames.final.text);
        expect(frame.transform).not.toBe("");
        expect(frame.aligned).not.toBe("");
        for (const key of ["x", "y", "width", "height"] as const)
          expect(frame[key]).toBeCloseTo(frames.final[key], 3);
      }
    }
  });
}

for (const fontFamily of ["Arial", "Segoe UI"]) for (const fontSize of [12, 24, 48, 96]) {
  test(`timeout notice inherits typography and fits OBS viewports: ${fontFamily} ${fontSize}`, async ({ page }, testInfo) => {
    await boot(page);
    const a = { ...appearance, fontFamily, fontSize, padding: 64 };
    await render(page, a, "42");
    await render(page, a, "42", "open");
    const notice = page.getByRole("status");
    await expect(notice).toHaveText("Please open SoulsTracker.");
    await expect(notice).toHaveCSS("font-size", `${fontSize}px`);
    await expect(notice).toHaveCSS("font-family", await page.getByTestId("total-deaths-overlay").evaluate(node => getComputedStyle(node).fontFamily));
    for (const [width, height] of [[640, 360], [800, 600], [1920, 1080], [320, 180], [256, 100]]) {
      await page.setViewportSize({ width, height });
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const geometry = await notice.evaluate(node => {
        const panel = node.parentElement!;
        const bounds = panel.getBoundingClientRect();
        const range = document.createRange(); range.selectNodeContents(node);
        return { x: bounds.x, y: bounds.y, right: bounds.right, bottom: bounds.bottom,
          scale: new DOMMatrix(getComputedStyle(panel).transform).a,
          lines: Array.from(range.getClientRects()).map(r => ({ x: r.x, y: r.y, right: r.right, bottom: r.bottom })) };
      });
      expect(geometry.scale).toBeGreaterThan(0); expect(geometry.scale).toBeLessThanOrEqual(1);
      for (const r of [geometry, ...geometry.lines]) {
        expect(r.x).toBeGreaterThanOrEqual(-1); expect(r.y).toBeGreaterThanOrEqual(-1);
        expect(r.right).toBeLessThanOrEqual(width + 1); expect(r.bottom).toBeLessThanOrEqual(height + 1);
      }
      expect(Math.abs(geometry.x + geometry.right - width)).toBeLessThanOrEqual(1);
      expect(Math.abs(geometry.y + geometry.bottom - height)).toBeLessThanOrEqual(1);
    }
    await testInfo.attach("constrained-notice", { body: await page.screenshot({ omitBackground: true }), contentType: "image/png" });
    await render(page, a, "42");
    await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 42");
    await render(page, { ...a, enabled: false }, "42");
    await expect(page.locator("main")).toBeEmpty();
    await render(page, a, "43");
    await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 43");
  });
}

for (const superseding of ["open", "disabled", "live", "clear"]) {
  test(`an uncached skull keeps the previous panel and cannot overwrite ${superseding}`, async ({ page }) => {
    await boot(page);
    await render(page, appearance);
    await page.evaluate(({ a, skull }) => {
      Object.defineProperty(HTMLImageElement.prototype, "complete", { configurable: true, get: () => false });
      HTMLImageElement.prototype.decode = () => new Promise<void>(resolve => { (window as any).releaseImage = resolve; });
      (window as any).pendingRender = (window as any).renderHosted(document.querySelector("main"), { value: "10" },
        { ...a, titleIconMode: "prefixSkull" }, skull);
    }, { a: appearance, skull });
    await page.evaluate(() => new Promise(requestAnimationFrame));
    await expect(page.getByTestId("total-deaths-overlay")).toHaveText("Total Deaths: 9");
    if (superseding === "clear") await page.locator("main").evaluate(node => node.replaceChildren());
    else await render(page, { ...appearance, enabled: superseding !== "disabled" }, "100", superseding === "open" ? "open" : "live");
    await page.evaluate(async () => { (window as any).releaseImage(); await (window as any).pendingRender; });
    if (superseding === "disabled" || superseding === "clear") await expect(page.locator("main")).toBeEmpty();
    else await expect(page.getByTestId("total-deaths-overlay")).toHaveText(superseding === "open" ? "Please open SoulsTracker." : "Total Deaths: 100");
  });
}
