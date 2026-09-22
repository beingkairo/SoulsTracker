import type { HostedAppearance, HostedDeath } from "./hosted-contracts.js";

// The retained compact renderer layout, with wire-validated appearance only.
export function renderHosted(target: HTMLElement, death: HostedDeath | null, appearance: HostedAppearance, skullAsset: string): void {
  if (!appearance.enabled) { target.replaceChildren(); return; }
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
  target.dataset.alignment = "left";
  // One replacement keeps both channels visually atomic and bounds SVG state.
  target.replaceChildren(skullFilter(appearance), panel);
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
