import { test, expect } from "@playwright/test";
import { readFileSync } from "node:fs";
import { parseHostedOverlay, serializeHostedOverlay, diffHostedOverlay } from "../src/hosted-contracts.js";

const corpus = JSON.parse(readFileSync(new URL("../../tests/fixtures/hosted-overlay/contracts.json", import.meta.url), "utf8"));

for (const field of ["title", "fontFamily"] as const) {
  test(`hosted rejects malformed Unicode ${field} on parse and serialize`, () => {
    for (const sample of corpus.invalidUnicode) {
      const changed = structuredClone(corpus.valid[4]);
      changed.appearance[field] = "UNICODE";
      const raw = JSON.stringify(changed).replace('"UNICODE"', sample.json);
      expect(() => parseHostedOverlay(raw), sample.name).toThrow();
      changed.appearance[field] = String.fromCharCode(...sample.codeUnits);
      expect(JSON.parse(sample.json)).toBe(changed.appearance[field]);
      expect(() => serializeHostedOverlay(changed), sample.name).toThrow();
    }
  });
  test(`hosted preserves supplementary Unicode and UTF-16 bounds ${field}`, () => {
    const limit = field === "title" ? 40 : 128;
    for (const sample of corpus.validUnicode) {
      for (const repeat of [1, limit / sample.codeUnits.length]) {
        const expected = String.fromCharCode(...sample.codeUnits).repeat(repeat);
        const changed = structuredClone(corpus.valid[4]);
        changed.appearance[field] = "UNICODE";
        const token = sample.json.slice(1, -1).repeat(repeat);
        const raw = JSON.stringify(changed).replace('"UNICODE"', `"${token}"`);
        const parsed = parseHostedOverlay(raw);
        expect(parsed.appearance![field]).toBe(expected);
        expect(parseHostedOverlay(serializeHostedOverlay(parsed))).toEqual(parsed);
        changed.appearance[field] = expected;
        expect(parseHostedOverlay(serializeHostedOverlay(changed))).toEqual(parsed);
        if (field === "title") {
          changed.appearance.title = `\u0085 ${expected} \u0085`;
          expect(parseHostedOverlay(JSON.stringify(changed)).appearance!.title).toBe(expected);
          changed.appearance.title = expected;
        }
        if (expected.length === limit) {
          expect(() => parseHostedOverlay(raw.replace(`"${token}"`, `"${token}A"`))).toThrow();
          changed.appearance[field] += "A";
          expect(() => serializeHostedOverlay(changed)).toThrow();
        }
      }
    }
  });
}

test("hosted exact byte, Unicode, title and font bounds", () => {
  const json = JSON.stringify(corpus.valid[0]);
  const exact = json + " ".repeat(8192 - new TextEncoder().encode(json).length);
  expect(() => parseHostedOverlay(exact)).not.toThrow();
  expect(() => parseHostedOverlay(exact + " ")).toThrow();
  const style = structuredClone(corpus.valid[4]);
  style.appearance.fontFamily = "F".repeat(128);
  style.appearance.title = "界".repeat(40);
  expect(() => parseHostedOverlay(JSON.stringify(style))).not.toThrow();
  style.appearance.fontFamily += "F";
  expect(() => parseHostedOverlay(JSON.stringify(style))).toThrow();
  style.appearance.fontFamily = "Arial";
  style.appearance.title = "\u0085 界 \u0085";
  expect(parseHostedOverlay(JSON.stringify(style)).appearance!.title).toBe("界");
  style.appearance.fontFamily = "\u0085 界 \u0085";
  expect(() => parseHostedOverlay(JSON.stringify(style))).toThrow();
});

test("hosted complete allowlist rejects missing, unknown and typed death values", () => {
  for (const channel of ["death", "appearance"]) {
    for (const field of Object.keys(corpus.valid[6][channel])) {
      const changed = structuredClone(corpus.valid[6]);
      delete changed[channel][field];
      expect(() => parseHostedOverlay(JSON.stringify(changed))).toThrow();
    }
    for (const field of corpus.forbiddenFields) {
      const changed = structuredClone(corpus.valid[6]);
      changed[channel][field] = "forbidden";
      expect(() => parseHostedOverlay(JSON.stringify(changed))).toThrow();
      delete changed[channel][field];
      changed[field] = "forbidden";
      expect(() => parseHostedOverlay(JSON.stringify(changed))).toThrow();
    }
  }
  for (const [field, values] of Object.entries(corpus.invalidDeath)) {
    for (const value of values as unknown[]) {
      const changed = structuredClone(corpus.valid[0]);
      changed.death[field] = value;
      expect(() => parseHostedOverlay(JSON.stringify(changed))).toThrow();
    }
  }
  const serialized = JSON.parse(serializeHostedOverlay(parseHostedOverlay(JSON.stringify(corpus.valid[6]))));
  expect(Object.keys(serialized)).toEqual(["v", "type", "death", "appearance"]);
  expect(Object.keys(serialized.death)).toEqual(["revision", "value", "availability"]);
  expect(Object.keys(serialized.appearance)).toEqual(Object.keys(corpus.valid[4].appearance));
  const normalized = parseHostedOverlay(JSON.stringify(corpus.valid[5])).appearance!;
  expect(normalized.title).toBe("");
  expect(normalized.textColor).toBe("#ABCDEF");
});

test("hosted semantic diff excludes revisions and omitted channels", () => {
  const before = parseHostedOverlay(JSON.stringify(corpus.valid[6]));
  const same = structuredClone(corpus.valid[6]);
  same.death.revision = "50";
  same.appearance.revision = "60";
  same.appearance.title = " Deaths ";
  expect(diffHostedOverlay(before, parseHostedOverlay(JSON.stringify(same)))).toBeNull();
  same.death.value = "0";
  expect(diffHostedOverlay(before, parseHostedOverlay(JSON.stringify(same)))).toEqual({ v: 1, type: "update", death: same.death });
  same.death.value = corpus.valid[6].death.value;
  same.appearance.enabled = false;
  const styleOnly = diffHostedOverlay(before, parseHostedOverlay(JSON.stringify(same)));
  expect(styleOnly).not.toHaveProperty("death");
  expect(styleOnly).toHaveProperty("appearance.enabled", false);
  expect(diffHostedOverlay(before, { v: 1, type: "update", appearance: before.appearance! })).toBeNull();
  same.death.value = "0";
  const combined = diffHostedOverlay(before, parseHostedOverlay(JSON.stringify(same)));
  expect(combined).toHaveProperty("death.value", "0");
  expect(combined).toHaveProperty("appearance.enabled", false);
  expect(parseHostedOverlay(serializeHostedOverlay(combined!))).toEqual(combined);
  same.death = { revision: "51", value: null, availability: "unavailable" };
  expect(diffHostedOverlay(before, parseHostedOverlay(JSON.stringify(same)))).toHaveProperty("death.value", null);
});

for (const [index, value] of corpus.valid.entries()) {
  test(`hosted valid golden ${index}`, () => {
    const parsed = parseHostedOverlay(JSON.stringify(value));
    expect(parseHostedOverlay(serializeHostedOverlay(parsed))).toEqual(parsed);
  });
}
for (const [index, value] of corpus.invalid.entries()) {
  test(`hosted invalid golden ${index}`, () => expect(() => parseHostedOverlay(value)).toThrow());
}
for (const [field, values] of Object.entries(corpus.invalidAppearance)) {
  test(`hosted rejects invalid appearance ${field}`, () => {
    for (const value of values as unknown[]) {
      const changed = structuredClone(corpus.valid[4]);
      changed.appearance[field] = value;
      expect(() => parseHostedOverlay(JSON.stringify(changed))).toThrow();
    }
  });
}
for (const field of ["revision", "value"]) {
  test(`hosted rejects malformed decimal ${field}`, () => {
    for (const value of corpus.invalidDecimals) {
      const changed = structuredClone(corpus.valid[0]);
      changed.death[field] = value;
      expect(() => parseHostedOverlay(JSON.stringify(changed))).toThrow();
    }
  });
}
