// Transport-neutral contracts. Not imported by the current browser entry point.
export interface HostedDeath {
  readonly revision: string;
  readonly value: string | null;
  readonly availability: "available" | "unavailable";
}
export interface HostedAppearance {
  readonly revision: string;
  readonly enabled: boolean;
  readonly title: string;
  readonly fontFamily: string;
  readonly fontSize: number;
  readonly textColor: string;
  readonly textOpacity: number;
  readonly backgroundColor: string;
  readonly backgroundOpacity: number;
  readonly padding: number;
  readonly cornerRadius: number;
  readonly outlineEnabled: boolean;
  readonly outlineColor: string;
  readonly outlineWidth: number;
  readonly shadowEnabled: boolean;
  readonly shadowColor: string;
  readonly shadowOffsetX: number;
  readonly shadowOffsetY: number;
  readonly shadowBlur: number;
  readonly titleIconMode: "off" | "prefixSkull" | "skullOnly";
  readonly iconColor: string;
}
export type HostedEnvelope =
  | { readonly v: 1; readonly type: "snapshot"; readonly death: HostedDeath | null; readonly appearance: HostedAppearance }
  | { readonly v: 1; readonly type: "update"; readonly death?: HostedDeath; readonly appearance?: HostedAppearance };

export const maximumHostedBytes = 8192;
const appearanceFields = ["revision", "enabled", "title", "fontFamily", "fontSize", "textColor", "textOpacity",
  "backgroundColor", "backgroundOpacity", "padding", "cornerRadius", "outlineEnabled", "outlineColor", "outlineWidth",
  "shadowEnabled", "shadowColor", "shadowOffsetX", "shadowOffsetY", "shadowBlur", "titleIconMode", "iconColor"];
function invalid(): never { throw new Error("Invalid hosted contract."); }
function object(value: unknown, allowed: string[], required = allowed): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return invalid();
  const result = value as Record<string, unknown>;
  if (Object.keys(result).some(k => !allowed.includes(k)) || required.some(k => !Object.hasOwn(result, k))) return invalid();
  return result;
}
function decimal(value: unknown): string {
  if (typeof value !== "string" || !/^(0|[1-9][0-9]*)$/.test(value) || value.length > 19 ||
    (value.length === 19 && value > "9223372036854775807")) return invalid();
  return value;
}
function death(value: unknown): HostedDeath {
  const d = object(value, ["revision", "value", "availability"]);
  const revision = decimal(d.revision);
  const total = d.value === null ? null : decimal(d.value);
  if ((d.availability !== "available" && d.availability !== "unavailable") ||
    (d.availability === "available" && total === null) ||
    (d.availability === "unavailable" && total !== null && total !== "0")) return invalid();
  return { revision, value: total, availability: d.availability };
}
// Match .NET String.Trim rather than JS trim (notably NEL and BOM differ).
function trimTitle(value: string): string {
  return value.replace(/^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+|[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$/g, "");
}
function wellFormed(value: string): boolean {
  // Iteration combines valid surrogate pairs, leaving isolated code units visible.
  for (const character of value) {
    const codePoint = character.codePointAt(0)!;
    if (codePoint >= 0xd800 && codePoint <= 0xdfff) return false;
  }
  return true;
}
function appearance(value: unknown): HostedAppearance {
  const a = object(value, appearanceFields);
  decimal(a.revision);
  for (const field of ["enabled", "outlineEnabled", "shadowEnabled"]) if (typeof a[field] !== "boolean") invalid();
  if (typeof a.title !== "string" || !wellFormed(a.title) || /[<>]/.test(a.title)) return invalid();
  const title = trimTitle(a.title);
  if (title.length > 40) return invalid();
  if (typeof a.fontFamily !== "string" || !wellFormed(a.fontFamily) || a.fontFamily.length === 0 || a.fontFamily.length > 128 ||
    /[\u0000-\u001f\u007f-\u009f;{}<>'"\\:()]/.test(a.fontFamily)) return invalid();
  const ranges: Record<string, [number, number]> = {
    fontSize: [12, 96], textOpacity: [0, 100], backgroundOpacity: [0, 100], padding: [0, 64], cornerRadius: [0, 32],
    outlineWidth: [0, 8], shadowOffsetX: [-20, 20], shadowOffsetY: [-20, 20], shadowBlur: [0, 20]
  };
  for (const [field, [min, max]] of Object.entries(ranges)) {
    const n = a[field];
    if (typeof n !== "number" || !Number.isInteger(n) || n < min || n > max) invalid();
  }
  const normalized = { ...a, title };
  for (const field of ["textColor", "backgroundColor", "outlineColor", "shadowColor", "iconColor"]) {
    const color = a[field];
    if (typeof color !== "string" || !/^#[0-9a-fA-F]{6}$/.test(color)) return invalid();
    (normalized as Record<string, unknown>)[field] = color.toUpperCase();
  }
  if (!["off", "prefixSkull", "skullOnly"].includes(a.titleIconMode as string)) return invalid();
  // Build in canonical field order independently of the incoming property order.
  return Object.fromEntries(appearanceFields.map(field => [field, (normalized as Record<string, unknown>)[field]])) as unknown as HostedAppearance;
}
function rejectDuplicateFields(json: string): void {
  // JSON.parse validates grammar first. Scan only structural tokens and quoted
  // strings so escaped keys are compared before JSON.parse could hide duplicates.
  const tokens = json.match(/"(?:\\[\s\S]|[^"\\])*"|[{}\[\]:]|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/g) ?? [];
  const stack: (Set<string> | null)[] = [];
  for (let i = 0; i < tokens.length; i++) {
    const token = tokens[i];
    // All numeric schema fields use integer JSON tokens, matching Int32 parsing.
    if (/^-?\d/.test(token) && /[.eE]/.test(token)) invalid();
    if (token === "{" || token === "[") {
      stack.push(token === "{" ? new Set() : null);
      if (stack.length > 8) invalid();
    } else if (token === "}" || token === "]") stack.pop();
    else if (token.startsWith('"') && tokens[i + 1] === ":") {
      const key: string = JSON.parse(token);
      const keys = stack[stack.length - 1];
      if (!keys || keys.has(key)) invalid();
      keys.add(key);
    }
  }
}
export function parseHostedOverlay(json: string): HostedEnvelope {
  if (new TextEncoder().encode(json).length > maximumHostedBytes) return invalid();
  const parsed: unknown = JSON.parse(json);
  rejectDuplicateFields(json);
  const root = object(parsed, ["v", "type", "death", "appearance"], ["v", "type"]);
  if (root.v !== 1 || (root.type !== "snapshot" && root.type !== "update")) return invalid();
  const hasDeath = Object.hasOwn(root, "death"), hasAppearance = Object.hasOwn(root, "appearance");
  if (root.type === "snapshot") {
    if (!hasDeath || !hasAppearance) return invalid();
    return { v: 1, type: "snapshot", death: root.death === null ? null : death(root.death), appearance: appearance(root.appearance) };
  }
  if (!hasDeath && !hasAppearance) return invalid();
  return { v: 1, type: "update", ...(hasDeath ? { death: death(root.death) } : {}),
    ...(hasAppearance ? { appearance: appearance(root.appearance) } : {}) };
}
export function serializeHostedOverlay(envelope: HostedEnvelope): string {
  return JSON.stringify(parseHostedOverlay(JSON.stringify(envelope)));
}

// Pure comparison only: no queue, revision allocation, or delivery ordering.
export function diffHostedOverlay(previous: HostedEnvelope, candidate: HostedEnvelope): HostedEnvelope | null {
  previous = parseHostedOverlay(serializeHostedOverlay(previous));
  candidate = parseHostedOverlay(serializeHostedOverlay(candidate));
  const semantic = (channel: HostedDeath | HostedAppearance | null | undefined) =>
    channel == null ? null : JSON.stringify({ ...channel, revision: "0" });
  const deathChanged = candidate.death != null && semantic(previous.death) !== semantic(candidate.death);
  const appearanceChanged = candidate.appearance != null && semantic(previous.appearance) !== semantic(candidate.appearance);
  return deathChanged || appearanceChanged
    ? { v: 1, type: "update", ...(deathChanged ? { death: candidate.death! } : {}),
      ...(appearanceChanged ? { appearance: candidate.appearance! } : {}) }
    : null;
}
