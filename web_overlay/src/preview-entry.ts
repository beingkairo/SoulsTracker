import { renderHosted } from "./hosted-renderer.js";
import type { HostedAppearance } from "./hosted-contracts.js";

declare global {
  interface Window {
    chrome: { webview: { addEventListener(name: string, listener: (event: MessageEvent) => void): void; postMessage(value: unknown): void } };
  }
}
const target = document.getElementById("souls-tracker-overlay")!;
let pending: { appearance: HostedAppearance; value: string } | null = null;
let scheduled = false;
let generation = 0;
let measuring = false;
let disposed = false;
let releaseMeasurement: (() => void) | null = null;
function conservativeFit(): void {
  const panel = target.querySelector<HTMLElement>(".souls-tracker-overlay-panel");
  if (!panel || !pending) return;
  target.style.transform = "none";
  const a = pending.appearance;
  const boxes: { left: number; top: number; right: number; bottom: number }[] = [];
  if (a.backgroundOpacity > 0) boxes.push(panel.getBoundingClientRect());
  const outline = a.outlineEnabled ? a.outlineWidth : 0;
  // Chromium's blur support is three standard deviations (CSS blur / 2).
  const blur = a.shadowEnabled ? a.shadowBlur * 1.5 : 0;
  const x = a.shadowEnabled ? a.shadowOffsetX : 0;
  const y = a.shadowEnabled ? a.shadowOffsetY : 0;
  const expand = (r: DOMRect, filteredImage: boolean): void => {
    const horizontalLimit = filteredImage ? r.width / 2 : Infinity;
    const verticalLimit = filteredImage ? r.height / 2 : Infinity;
    boxes.push({
      left: r.left - Math.min(horizontalLimit, Math.max(outline, blur - x, 0)),
      top: r.top - Math.min(verticalLimit, Math.max(outline, blur - y, 0)),
      right: r.right + Math.min(horizontalLimit, Math.max(outline, blur + x, 0)),
      bottom: r.bottom + Math.min(verticalLimit, Math.max(outline, blur + y, 0))
    });
  };
  const content = panel.firstElementChild!;
  for (const node of content.childNodes) {
    if (node instanceof HTMLImageElement) expand(node.getBoundingClientRect(), true);
    else if (node.nodeType === Node.TEXT_NODE && node.textContent) {
      const range = document.createRange(); range.selectNodeContents(node);
      expand(range.getBoundingClientRect(), false);
    }
  }
  if (boxes.length === 0) return;
  const left = Math.min(...boxes.map(r => r.left)), top = Math.min(...boxes.map(r => r.top));
  const right = Math.max(...boxes.map(r => r.right)), bottom = Math.max(...boxes.map(r => r.bottom));
  // Equal small safety margins; never enlarge or change the draft's sizes.
  const scale = Math.min(1, Math.max(1, innerWidth - 16) / (right - left), Math.max(1, innerHeight - 16) / (bottom - top));
  target.style.transform = `translate(${(innerWidth - (right - left) * scale) / 2 - left * scale}px, ${(innerHeight - (bottom - top) * scale) / 2 - top * scale}px) scale(${scale})`;
}
// Serialize the actual renderer and its bundled styles, rather than duplicating
// text, image or effect drawing rules. The scratch image is never displayed.
async function measurePaint(version: number): Promise<void> {
  if (measuring || disposed) return;
  measuring = true;
  const canvas = document.createElement("canvas");
  const image = new Image();
  releaseMeasurement = () => { canvas.width = canvas.height = 0; image.removeAttribute("src"); };
  try {
    await document.fonts.ready;
    await Promise.all(Array.from(target.querySelectorAll("img"), item => item.decode()));
    if (version !== generation || disposed) return;
    const panel = target.querySelector<HTMLElement>("section");
    if (!panel) return;
    const inset = 128;
    const width = Math.ceil(panel.offsetWidth + inset * 2);
    const height = Math.ceil(panel.offsetHeight + inset * 2);
    const ratio = Math.min(2, devicePixelRatio || 1);
    const pixelWidth = Math.ceil(width * ratio), pixelHeight = Math.ceil(height * ratio);
    if (pixelWidth > 16384 || pixelHeight > 16384 || pixelWidth * pixelHeight > 8388608) throw new Error("Preview measurement limit");
    const clone = target.cloneNode(true) as HTMLElement;
    clone.style.transform = `translate(${inset}px, ${inset}px)`;
    const wrapper = document.createElement("div");
    wrapper.style.fontFamily = getComputedStyle(document.body).fontFamily;
    wrapper.style.color = getComputedStyle(document.body).color;
    const style = document.createElement("style");
    style.textContent = document.querySelector("style")!.textContent;
    wrapper.append(style, clone);
    const serialized = new XMLSerializer().serializeToString(wrapper);
    image.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" width="${pixelWidth}" height="${pixelHeight}" viewBox="0 0 ${width} ${height}"><foreignObject width="100%" height="100%">${serialized}</foreignObject></svg>`);
    await image.decode();
    if (version !== generation || disposed) return;
    canvas.width = pixelWidth; canvas.height = pixelHeight;
    const context = canvas.getContext("2d", { willReadFrequently: true });
    if (!context) throw new Error("Preview measurement unavailable");
    context.drawImage(image, 0, 0);
    const pixels = context.getImageData(0, 0, pixelWidth, pixelHeight).data;
    let left = pixelWidth, top = pixelHeight, right = 0, bottom = 0;
    // Include every pixel with alpha >= 2/255, including faint blur tails.
    for (let y = 0; y < pixelHeight; y++) for (let x = 0; x < pixelWidth; x++) {
      if (pixels[(y * pixelWidth + x) * 4 + 3] <= 1) continue;
      left = Math.min(left, x); right = Math.max(right, x + 1);
      top = Math.min(top, y); bottom = Math.max(bottom, y + 1);
    }
    if (right <= left || bottom <= top) { target.dataset.fit = "empty"; return; }
    const paintWidth = (right - left) / ratio, paintHeight = (bottom - top) / ratio;
    const scale = Math.min(1, Math.max(1, innerWidth - 16) / paintWidth, Math.max(1, innerHeight - 16) / paintHeight);
    target.style.transform = `translate(${(innerWidth - paintWidth * scale) / 2 - (left / ratio - inset) * scale}px, ${(innerHeight - paintHeight * scale) / 2 - (top / ratio - inset) * scale}px) scale(${scale})`;
    target.dataset.fit = "painted";
  } catch {
    if (version === generation && !disposed) target.dataset.fit = "fallback";
  } finally {
    releaseMeasurement?.();
    releaseMeasurement = null;
    measuring = false;
    if (version !== generation && !disposed) schedule();
  }
}
function schedule(): void {
  if (scheduled || disposed) return;
  scheduled = true;
  requestAnimationFrame(() => {
    scheduled = false;
    if (!pending || disposed || measuring) return;
    renderHosted(target, { revision: "0", value: pending.value, availability: "available" }, pending.appearance, "__PREVIEW_SKULL__");
    conservativeFit();
    target.dataset.fit = "measuring";
    void measurePaint(generation);
    window.chrome.webview.postMessage("rendered");
  });
}
function invalidate(): void { generation++; schedule(); }
window.chrome.webview.addEventListener("message", event => {
  if (disposed) return;
  pending = event.data;
  invalidate();
});
addEventListener("resize", invalidate);
document.fonts.addEventListener("loadingdone", invalidate);
addEventListener("pagehide", () => {
  disposed = true; generation++; pending = null;
  releaseMeasurement?.();
  removeEventListener("resize", invalidate);
  document.fonts.removeEventListener("loadingdone", invalidate);
}, { once: true });
addEventListener("wheel", event => {
  if (disposed || !event.isTrusted || event.ctrlKey || !Number.isFinite(event.deltaY)) return;
  event.preventDefault();
  const units = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 16 : event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? innerHeight : 1;
  const delta = Math.max(-600, Math.min(600, event.deltaY * units));
  if (delta !== 0) window.chrome.webview.postMessage(`wheel:${delta}`);
}, { passive: false });
window.chrome.webview.postMessage("ready");
