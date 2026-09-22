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
function fit(): void {
  const panel = target.querySelector<HTMLElement>(".souls-tracker-overlay-panel");
  if (!panel) return;
  // Fit only the preview viewport, leaving the renderer's values untouched.
  const inset = 48;
  const scale = Math.min(1, Math.max(1, innerWidth - inset * 2) / panel.offsetWidth, Math.max(1, innerHeight - inset * 2) / panel.offsetHeight);
  target.style.transform = `translate(${inset}px, ${inset}px) scale(${scale})`;
}
window.chrome.webview.addEventListener("message", event => {
  pending = event.data;
  if (scheduled) return;
  scheduled = true;
  requestAnimationFrame(() => {
    scheduled = false;
    if (!pending) return;
    renderHosted(target, { revision: "0", value: pending.value, availability: "available" }, pending.appearance, "__PREVIEW_SKULL__");
    fit();
    target.querySelector("img")?.addEventListener("load", fit, { once: true });
    window.chrome.webview.postMessage("rendered");
  });
});
addEventListener("resize", fit);
window.chrome.webview.postMessage("ready");
