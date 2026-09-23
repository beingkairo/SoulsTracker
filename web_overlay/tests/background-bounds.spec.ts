import { test, expect } from "@playwright/test";
import { readFileSync, mkdtempSync, rmSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import process from "node:process";

const renderer = readFileSync(new URL("../dist/src/hosted-renderer.js", import.meta.url), "utf8").replace("export function renderHosted", "function renderHosted");
const css = readFileSync(new URL("../src/overlay.css", import.meta.url), "utf8");
const skull = `data:image/png;base64,${readFileSync(new URL("../../assets/branding/souls-tracker-skull.png", import.meta.url), "base64")}`;
const corpus = JSON.parse(readFileSync(new URL("../../tests/fixtures/hosted-overlay/contracts.json", import.meta.url), "utf8"));
const temporary = mkdtempSync(join(tmpdir(), "souls-background-bounds-"));
const documentPath = join(temporary, "preview.html");
execFileSync(process.execPath, [fileURLToPath(new URL("../scripts/build-preview.mjs", import.meta.url)), documentPath, "--no-compile"]);
const previewHtml = readFileSync(documentPath, "utf8").replace("<script>", `<script>window.version=0;window.chrome={webview:{addEventListener:(name,fn)=>window.previewMessage=fn,postMessage:message=>{if(message==='rendered')window.version++}}};</script><script>`);
test.afterAll(() => rmSync(temporary, { recursive: true, force: true }));

for (const surface of ["hosted", "preview180", "preview96", "preview72", "preview48"]) for (const mode of ["off", "prefixSkull", "skullOnly", "blank"]) {
  test(`shared background follows painted content edges: ${mode}${surface === "hosted" ? "" : ` ${surface}`}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 10000, height: 800 });
    await page.setContent(surface === "hosted" ? `<style>${css}</style><main id="souls-tracker-overlay"></main><script>${renderer}</script>` : previewHtml);
    await page.locator("body").evaluate(node => (node as HTMLElement).style.background = "transparent");
    for (const fontSize of [12, 24, 96]) for (const fontFamily of ["Arial", "Segoe UI"]) for (const value of ["0", "1234567890"]) for (const padding of [0, 12, 32]) {
      const title = padding === 32 ? "W".repeat(40) : padding === 0 ? "  W  gg  " : "Deaths";
      const appearance = { ...corpus.valid[4].appearance, enabled: true, title: mode === "blank" ? "" : title, titleIconMode: mode === "blank" ? "off" : mode,
        fontSize, fontFamily, textColor: "#FFFFFF", textOpacity: fontFamily === "Arial" ? 100 : 72, iconColor: "#FFFFFF", backgroundColor: "#FF0000", backgroundOpacity: fontFamily === "Arial" ? 100 : 70,
        padding, cornerRadius: 0, outlineEnabled: fontFamily !== "Arial", outlineWidth: 8, outlineColor: "#0000FF", shadowEnabled: fontFamily !== "Arial", shadowColor: "#0000FF", shadowBlur: 20, shadowOffsetX: value === "0" ? -20 : 20, shadowOffsetY: 20 };
      const viewportWidth = fontFamily === "Arial" ? 440 : 1200;
      if (surface === "hosted") {
        await page.evaluate(async ({ appearance, value, skull, viewportWidth }) => {
          await (window as any).renderHosted(document.querySelector("main"), { value }, appearance, skull);
          document.querySelector<HTMLElement>("main")!.style.width = `${viewportWidth}px`;
          document.querySelector<HTMLElement>("main")!.style.transform = "translate(128px, 128px)";
        }, { appearance, value, skull, viewportWidth });
      } else {
        await page.setViewportSize({ width: viewportWidth, height: Number(surface.slice("preview".length)) });
        const version = await page.evaluate(() => (window as any).version);
        await page.evaluate(data => (window as any).previewMessage({ data }), { appearance, value });
        await expect.poll(() => page.evaluate(() => (window as any).version)).toBeGreaterThan(version);
        // A resize can finish an older draft first. Fence the painted state to
        // all requested geometry, not just any rendered notification.
        await expect.poll(() => page.evaluate(({ appearance, value }) => {
          const panel = document.querySelector<HTMLElement>("section");
          const text = appearance.titleIconMode === "skullOnly" || appearance.title === "" ? value : `${appearance.title}: ${value}`;
          return document.querySelector<HTMLElement>("main")?.dataset.fit === "painted" && panel?.style.fontSize === `${appearance.fontSize}px` &&
            panel.style.fontFamily.includes(appearance.fontFamily) && panel.style.padding === `${appearance.padding}px` && panel.textContent === text;
        }, { appearance, value })).toBe(true);
      }
      const scale = await page.locator("main").evaluate(node => new DOMMatrix(getComputedStyle(node).transform).a);
      const bounds = await page.locator("section").boundingBox();
      const capture = { ...(surface === "hosted" ? { clip: { x: 0, y: 0, width: Math.ceil(bounds!.width + 256), height: Math.ceil(bounds!.height + 256) } } : {}), omitBackground: true };
      const screenshot = await page.screenshot(capture);
      // Expose background pixels hidden beneath effects without changing layout.
      await page.locator("section > h1, section > p").evaluate(node => (node as HTMLElement).style.visibility = "hidden");
      const background = await page.screenshot(capture);
      await page.locator("section > h1, section > p").evaluate(node => (node as HTMLElement).style.visibility = "");
      const paint = await page.evaluate(async ({ data, background }) => {
        const image = new Image(); image.src = `data:image/png;base64,${data}`; await image.decode();
        const canvas = new OffscreenCanvas(image.width, image.height); const context = canvas.getContext("2d")!; context.drawImage(image, 0, 0);
        const rgba = context.getImageData(0, 0, image.width, image.height).data;
        image.src = `data:image/png;base64,${background}`; await image.decode(); context.clearRect(0, 0, image.width, image.height); context.drawImage(image, 0, 0);
        const bg = context.getImageData(0, 0, image.width, image.height).data;
        let left = image.width, right = 0, backgroundLeft = image.width, backgroundRight = 0;
        for (let y = 0; y < image.height; y++) for (let x = 0; x < image.width; x++) {
          const i = (y * image.width + x) * 4;
          if (rgba[i + 3] >= 2 && rgba[i + 1] > 1) { left = Math.min(left, x); right = Math.max(right, x + 1); }
          if (bg[i + 3] >= 2) { backgroundLeft = Math.min(backgroundLeft, x); backgroundRight = Math.max(backgroundRight, x + 1); }
        }
        return { left: left - backgroundLeft, right: backgroundRight - right };
      }, { data: screenshot.toString("base64"), background: background.toString("base64") });
      await testInfo.attach(`${fontFamily}-${fontSize}-${value}-${padding}`, { body: JSON.stringify({ paint, bounds, scale, padding, surface }), contentType: "application/json" });
      await testInfo.attach(`paint-${fontFamily}-${fontSize}-${value}-${padding}`, { body: screenshot, contentType: "image/png" });
      // Green identifies glyph/icon ink independently of the blue directional
      // effects. Padding surrounds ink; effect overflow has separate full-fit proof.
      // One antialiased edge pixel per side, independent of preview fit tolerance.
      expect(Math.abs(paint.left - paint.right)).toBeLessThanOrEqual(2);
      expect(paint.left).toBeGreaterThanOrEqual(padding * scale - 2); expect(paint.right).toBeGreaterThanOrEqual(padding * scale - 2);
    }
  });
}
