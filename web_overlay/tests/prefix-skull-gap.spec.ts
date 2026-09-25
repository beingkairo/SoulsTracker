import { test, expect } from "@playwright/test";
import { readFileSync } from "node:fs";

const renderer = readFileSync(new URL("../dist/src/hosted-renderer.js", import.meta.url), "utf8").replace("export function renderHosted", "function renderHosted");
const css = readFileSync(new URL("../src/overlay.css", import.meta.url), "utf8");
const skull = `data:image/png;base64,${readFileSync(new URL("../../assets/branding/souls-tracker-skull.png", import.meta.url), "base64")}`;
const corpus = JSON.parse(readFileSync(new URL("../../tests/fixtures/hosted-overlay/contracts.json", import.meta.url), "utf8"));

for (const titleIconMode of ["off", "prefixSkull", "skullOnly"]) for (const fontSize of [24, 96]) {
  test(`painted inline gap and geometry ${titleIconMode} ${fontSize}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 2400, height: 600 });
    await page.setContent(`<style>${css}</style><main id="souls-tracker-overlay"></main><script>${renderer}</script>`);
    const appearance = { ...corpus.valid[4].appearance, enabled: true, title: "Deaths", titleIconMode, fontFamily: "Arial", fontSize,
      textColor: "#FF0000", textOpacity: 100, iconColor: "#00FF00", backgroundColor: "#0000FF", backgroundOpacity: 100,
      padding: 12, cornerRadius: 0, outlineEnabled: false, shadowEnabled: false };
    await page.evaluate(({ appearance, skull }) => (window as any).renderHosted(document.querySelector("main"), { value: "123" }, appearance, skull), { appearance, skull });
    const geometry = await page.locator("h1").evaluate(node => {
      const image = node.querySelector("img");
      const range = document.createRange(); range.selectNodeContents(node.lastChild!);
      const text = range.getBoundingClientRect();
      const r = image?.getBoundingClientRect();
      return { text: node.textContent, textWidth: text.width, skullWidth: r?.width ?? 0, skullHeight: r?.height ?? 0,
        rawGap: r ? text.left - r.right : null, imageMargin: image ? getComputedStyle(image).marginRight : null };
    });
    const screenshot = await page.screenshot();
    const ink = await page.evaluate(async data => {
      const image = new Image(); image.src = `data:image/png;base64,${data}`; await image.decode();
      const canvas = new OffscreenCanvas(image.width, image.height); const context = canvas.getContext("2d")!; context.drawImage(image, 0, 0);
      const pixels = context.getImageData(0, 0, image.width, image.height).data;
      let skullLeft = Infinity, skullRight = -Infinity, textLeft = Infinity, textRight = -Infinity;
      for (let y = 0; y < image.height; y++) for (let x = 0; x < image.width; x++) {
        const i = (y * image.width + x) * 4;
        if (pixels[i + 1] >= 2 && pixels[i] === 0) { skullLeft = Math.min(skullLeft, x); skullRight = Math.max(skullRight, x + 1); }
        if (pixels[i] >= 2 && pixels[i + 1] === 0) { textLeft = Math.min(textLeft, x); textRight = Math.max(textRight, x + 1); }
      }
      return { skullLeft, skullRight, textLeft, textRight, gap: textLeft - skullRight };
    }, screenshot.toString("base64"));
    await testInfo.attach("paint", { body: screenshot, contentType: "image/png" });
    await testInfo.attach("geometry", { body: JSON.stringify({ titleIconMode, fontSize, geometry, ink }), contentType: "application/json" });
    expect(geometry.text).toBe(titleIconMode === "skullOnly" ? "123" : "Deaths: 123");
    if (titleIconMode !== "off") {
      expect(geometry.skullWidth).toBeCloseTo(fontSize * 1.15 * 2, 1);
      expect(geometry.skullHeight).toBeCloseTo(fontSize * 1.15 * 2, 1);
      if (titleIconMode === "prefixSkull") expect(Math.abs(ink.gap - 4)).toBeLessThanOrEqual(2);
      else expect(geometry.imageMargin).toBe("0px");
    }
  });
}
