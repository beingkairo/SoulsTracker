import type { HostedAppearance, HostedDeath } from "./hosted-contracts.js";

// The retained compact renderer layout, with wire-validated appearance only.
export function renderHosted(target: HTMLElement, death: HostedDeath | null, appearance: HostedAppearance, skullAsset: string): Promise<void> {
  if (!appearance.enabled) { target.replaceChildren(); return Promise.resolve(); }
  const panel = document.createElement("section");
  panel.className = "souls-tracker-overlay-panel";
  panel.dataset.testid = "total-deaths-overlay";
  panel.style.fontFamily = `"${appearance.fontFamily}", sans-serif`;
  panel.style.fontSize = `${appearance.fontSize}px`;
  panel.style.color = appearance.textColor;
  panel.style.setProperty("--text-opacity", String(appearance.textOpacity / 100));
  const rgb = (hex: string) => [1, 3, 5].map(i => Number.parseInt(hex.slice(i, i + 2), 16));
  panel.style.backgroundColor = `rgb(${rgb(appearance.backgroundColor).join(" ")} / ${appearance.backgroundOpacity}%)`;
  panel.style.padding = `${appearance.padding}px`;
  panel.style.borderRadius = `${appearance.cornerRadius}px`;
  panel.style.textAlign = "left";
  const effects: string[] = [];
  const width = appearance.outlineEnabled ? appearance.outlineWidth : 0;
  if (width > 0) for (const [x, y] of [[-width, -width], [0, -width], [width, -width], [-width, 0], [width, 0], [-width, width], [0, width], [width, width]])
    effects.push(`${x}px ${y}px 0 ${appearance.outlineColor}`);
  if (appearance.shadowEnabled) effects.push(`${appearance.shadowOffsetX}px ${appearance.shadowOffsetY}px ${appearance.shadowBlur}px ${appearance.shadowColor}`);
  panel.style.textShadow = effects.join(", ");
  const value = death?.value ?? "0";
  const hasHeading = appearance.title.trim().length > 0 || appearance.titleIconMode === "skullOnly";
  if (hasHeading) {
    const title = document.createElement("h1"); title.className = "overlay-heading";
    const text = appearance.titleIconMode === "skullOnly" ? value : `${appearance.title}: ${value}`;
    if (appearance.titleIconMode !== "off") {
      const skull = document.createElement("img"); skull.alt = "SoulsTracker skull"; skull.className = "overlay-title-skull";
      skull.src = skullAsset;
      skull.style.filter = "url(#hosted-skull-filter)";
      title.append(skull, document.createTextNode(text));
    } else title.textContent = text;
    panel.append(title);
  } else {
    const number = document.createElement("p"); number.className = "overlay-total-deaths";
    number.dataset.testid = "total-deaths-value"; number.textContent = value; panel.append(number);
  }
  target.className = "souls-tracker-total-deaths-canvas";
  target.dataset.alignment = document.documentElement.classList.contains("hosted-overlay") ? "center" : "left";
  // One replacement keeps both channels visually atomic and bounds SVG state.
  target.replaceChildren(skullFilter(appearance), panel);
  return alignContentEdges(panel).then(() => {
    if (target.dataset.alignment === "center" && panel.isConnected) centerHostedPaint(panel, appearance);
  });
}

// The hosted source never fits or scales. Measure ink/effects on an offscreen
// canvas without loading a serialized document or relaxing the page's CSP.
// A panel-relative translation stays centered through viewport-only resizes.
function centerHostedPaint(panel: HTMLElement, appearance: HostedAppearance): void {
  const canvas = document.createElement("canvas");
  try {
    const inset = 128;
    const bounds = panel.getBoundingClientRect();
    canvas.width = Math.ceil(bounds.width + inset * 2);
    canvas.height = Math.ceil(bounds.height + inset * 2);
    if (canvas.width > 16384 || canvas.height > 16384 || canvas.width * canvas.height > 8388608) return;
    const context = canvas.getContext("2d", { willReadFrequently: true });
    if (!context) return;
    const content = panel.firstElementChild as HTMLElement;
    const style = getComputedStyle(content);
    context.font = `${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
    const node = content.lastChild!;
    const range = document.createRange(); range.selectNodeContents(node);
    const textBounds = range.getBoundingClientRect();
    const text = (node.textContent ?? "").replace(/[ \t\r\n\f]+/g, " ").trim();
    const metrics = context.measureText(text);
    const x = textBounds.left - bounds.left + inset;
    const y = textBounds.top - bounds.top + metrics.fontBoundingBoxAscent + inset;
    context.fillStyle = style.color;
    // CSS text-shadow paints the outline and shadow behind the same text ink.
    if (appearance.shadowEnabled) {
      context.shadowColor = appearance.shadowColor;
      context.shadowBlur = appearance.shadowBlur;
      context.shadowOffsetX = appearance.shadowOffsetX;
      context.shadowOffsetY = appearance.shadowOffsetY;
      context.fillText(text, x, y);
      context.shadowColor = "transparent";
    }
    if (appearance.outlineEnabled && appearance.outlineWidth > 0) {
      const w = appearance.outlineWidth;
      context.fillStyle = appearance.outlineColor;
      for (const [dx, dy] of [[-w, -w], [0, -w], [w, -w], [-w, 0], [w, 0], [-w, w], [0, w], [w, w]]) context.fillText(text, x + dx, y + dy);
    }
    context.fillStyle = style.color; context.fillText(text, x, y);
    const image = content.querySelector("img");
    if (image) {
      const r = image.getBoundingClientRect();
      context.filter = "url(#hosted-skull-filter)";
      context.drawImage(image, r.left - bounds.left + inset, r.top - bounds.top + inset, r.width, r.height);
      context.filter = "none";
    }
    const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data;
    let left = Infinity, top = Infinity, right = -Infinity, bottom = -Infinity;
    if (appearance.backgroundOpacity > 0) { left = 0; top = 0; right = bounds.width; bottom = bounds.height; }
    const opacity = appearance.textOpacity / 100;
    for (let y = 0; y < canvas.height; y++) for (let x = 0; x < canvas.width; x++) {
      if (pixels[(y * canvas.width + x) * 4 + 3] * opacity < 2) continue;
      left = Math.min(left, x - inset); top = Math.min(top, y - inset);
      right = Math.max(right, x + 1 - inset); bottom = Math.max(bottom, y + 1 - inset);
    }
    if (right > left && bottom > top)
      panel.style.transform = `translate(${(bounds.width - left - right) / 2}px, ${(bounds.height - top - bottom) / 2}px)`;
  } catch {
    // Keep the intrinsic panel centered if the browser cannot measure paint.
  } finally { canvas.width = canvas.height = 0; }
}

// Padding starts at the visible content, not a font's advance box or the
// bundled image's transparent border. Internal image/text spacing stays intact.
let imageEdges: { source: string; left: number } | null = null;
async function alignContentEdges(panel: HTMLElement): Promise<void> {
  try {
    await document.fonts.ready;
    const image = panel.querySelector("img");
    if (image) await image.decode();
    if (!panel.isConnected) return;
    const content = panel.firstElementChild as HTMLElement;
    const style = getComputedStyle(content);
    const canvas = document.createElement("canvas");
    try {
      const context = canvas.getContext("2d", { willReadFrequently: true });
      if (!context) return;
      context.font = `${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
      // Match CSS nowrap's collapsible whitespace without altering the title.
      const text = (content.textContent ?? "").replace(/[ \t\r\n\f]+/g, " ").replace(/^ | $/g, "");
      const metrics = context.measureText(text);
      let left = metrics.actualBoundingBoxLeft;
      if (image) {
        if (imageEdges?.source !== image.src) {
          if (image.naturalWidth * image.naturalHeight > 4194304) return;
          canvas.width = image.naturalWidth; canvas.height = image.naturalHeight;
          context.drawImage(image, 0, 0);
          const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data;
          let first = canvas.width;
          for (let y = 0; y < canvas.height; y++) for (let x = 0; x < first; x++)
            if (pixels[(y * canvas.width + x) * 4 + 3] >= 2) first = x;
          imageEdges = { source: image.src, left: first < canvas.width ? first / canvas.width : 0 };
        }
        left = -imageEdges.left * Number.parseFloat(getComputedStyle(image).width);
      }
      content.style.marginLeft = `${left}px`;
      content.style.marginRight = `${metrics.actualBoundingBoxRight - metrics.width}px`;
    } finally { canvas.width = canvas.height = 0; }
  } catch {
    // Unavailable local fonts/images retain conservative intrinsic layout.
  }
}

// Same raster color matrix and outside-alpha outline as the retained renderer.
// Rebuild one bounded definition instead of accumulating filters per revision.
function skullFilter(a: HostedAppearance): SVGSVGElement {
  const namespace = "http://www.w3.org/2000/svg";
  const element = (name: string, attributes: Record<string, string>) => {
    const node = document.createElementNS(namespace, name);
    for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
    return node;
  };
  const svg = element("svg", { "aria-hidden": "true", width: "0", height: "0" }) as SVGSVGElement;
  svg.style.position = "absolute"; svg.style.overflow = "hidden";
  const filter = element("filter", { id: "hosted-skull-filter", x: "-50%", y: "-50%", width: "200%", height: "200%", "color-interpolation-filters": "sRGB" });
  const [r, g, b] = [1, 3, 5].map(i => Number.parseInt(a.iconColor.slice(i, i + 2), 16) / 255);
  filter.append(element("feColorMatrix", { in: "SourceGraphic", result: "tinted", type: "matrix", values: `${r} 0 0 0 0  0 ${g} 0 0 0  0 0 ${b} 0 0  0 0 0 1 0` }));
  const outline = a.outlineEnabled && a.outlineWidth > 0;
  if (outline) {
    filter.append(
      element("feMorphology", { in: "SourceAlpha", operator: "dilate", radius: String(a.outlineWidth), result: "expanded" }),
      element("feFlood", { "flood-color": a.outlineColor, result: "outline-color" }),
      element("feComposite", { in: "outline-color", in2: "expanded", operator: "in", result: "expanded-outline" }),
      element("feComposite", { in: "expanded-outline", in2: "SourceAlpha", operator: "out", result: "outer-outline" })
    );
  }
  if (a.shadowEnabled) filter.append(element("feDropShadow", { in: "SourceAlpha", dx: String(a.shadowOffsetX), dy: String(a.shadowOffsetY),
    stdDeviation: String(a.shadowBlur / 2), "flood-color": a.shadowColor, result: "shadow" }));
  if (outline || a.shadowEnabled) {
    const merge = element("feMerge", {});
    for (const result of [...(a.shadowEnabled ? ["shadow"] : []), ...(outline ? ["outer-outline"] : []), "tinted"])
      merge.append(element("feMergeNode", { in: result }));
    filter.append(merge);
  }
  svg.append(filter); return svg;
}
