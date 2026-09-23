import { test, expect } from "@playwright/test";
import { readFileSync, mkdtempSync, rmSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import process from "node:process";

const temporary = mkdtempSync(join(tmpdir(), "souls-preview-equivalence-"));
const previewDocument = join(temporary, "preview.html");
execFileSync(process.execPath, [fileURLToPath(new URL("../scripts/build-preview.mjs", import.meta.url)), previewDocument, "--no-compile"]);
const html = readFileSync(previewDocument, "utf8");
test.afterAll(() => rmSync(temporary, { recursive: true, force: true }));
const renderer = readFileSync(new URL("../dist/src/hosted-renderer.js", import.meta.url), "utf8").replace("export function renderHosted", "function renderHosted");
const css = readFileSync(new URL("../src/overlay.css", import.meta.url), "utf8");
const skull = `data:image/png;base64,${readFileSync(new URL("../../assets/branding/souls-tracker-skull.png", import.meta.url), "base64")}`;
const corpus = JSON.parse(readFileSync(new URL("../../tests/fixtures/hosted-overlay/contracts.json", import.meta.url), "utf8"));

for (const titleIconMode of ["off", "prefixSkull", "skullOnly"]) {
  test(`offline preview equals hosted pixels: ${titleIconMode}`, async ({ browser }, testInfo) => {
    const context = await browser.newContext({ viewport: { width: 1200, height: 300 } });
    const requests: string[] = [];
    await context.route("**/*", route => { requests.push(route.request().url()); return route.abort(); });
    const preview = await context.newPage();
    const hosted = await context.newPage();
    await preview.setContent(html.replace("<script>", `<script>window.chrome={webview:{addEventListener:(name,fn)=>window.previewMessage=fn,postMessage:()=>{}}};</script><script>`));
    // Shell transparency indicator is intentionally absent from hosted output.
    await preview.locator("body").evaluate(node => (node as HTMLElement).style.background = "transparent");
    await hosted.setContent(`<style>${css}</style><main id="souls-tracker-overlay"></main><script>${renderer}</script>`);
    for (const outlineWidth of [0, 3]) {
      const appearance = { ...corpus.valid[4].appearance, enabled: true, title: "Preview test", titleIconMode, fontFamily: "Arial", fontSize: 32, textColor: "#AABBCC", textOpacity: 72, iconColor: "#CC8844", backgroundColor: "#223344", backgroundOpacity: 70, padding: 12, cornerRadius: 7, outlineEnabled: true, outlineWidth, outlineColor: "#112233", shadowEnabled: true, shadowColor: "#000000", shadowBlur: 5, shadowOffsetX: -2, shadowOffsetY: 3 };
      for (const value of ["0", "123"]) {
        await preview.evaluate(payload => (window as any).previewMessage({ data: payload }), { appearance, value });
        await hosted.evaluate(({ appearance, value, skull }) => (window as any).renderHosted(document.querySelector("main"), { value, availability: "available", revision: "0" }, appearance, skull), { appearance, value, skull });
        const actual = preview.getByTestId("total-deaths-overlay");
        const expected = hosted.getByTestId("total-deaths-overlay");
        await expect(actual).toHaveText(titleIconMode === "skullOnly" ? value : `Preview test: ${value}`);
        await expect(preview.locator("main")).toHaveAttribute("data-fit", "painted");
        for (const page of [preview, hosted]) await page.evaluate(async () => { await document.fonts.ready; await Promise.all(Array.from(document.images).map(image => image.decode())); });
        // Isolate renderer equivalence from the separately tested local fit.
        // Normalize both origins to whole pixels to avoid subpixel rasterization.
        const transform = await preview.locator("main").evaluate(node => (node as HTMLElement).style.transform);
        await testInfo.attach(`placement-${outlineWidth}-${value}`, { body: transform, contentType: "text/plain" });
        for (const page of [preview, hosted]) await page.locator("main").evaluate(node => { (node as HTMLElement).style.transformOrigin = "top left"; (node as HTMLElement).style.transform = "translate(48px, 48px)"; });
        const pixels = await actual.screenshot();
        expect(pixels.equals(await expected.screenshot())).toBe(true);
        await testInfo.attach(`preview-${outlineWidth}-${value}`, { body: pixels, contentType: "image/png" });
      }
    }
    expect(requests).toEqual([]);
    await context.close();
  });
}
