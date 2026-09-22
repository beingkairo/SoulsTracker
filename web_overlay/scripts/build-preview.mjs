import { readFile, writeFile, mkdir } from "node:fs/promises";
import { resolve, dirname } from "node:path";
import { execFileSync } from "node:child_process";

const root = resolve(import.meta.dirname, "..");
const output = resolve(process.argv[2]);
execFileSync(process.execPath, [resolve(root, "node_modules/typescript/bin/tsc"), "--project", resolve(root, "tsconfig.json")], { stdio: "inherit" });
const compile = name => readFile(resolve(root, `dist/src/${name}.js`), "utf8");
const renderer = (await compile("hosted-renderer")).replace("export function renderHosted", "function renderHosted");
const entry = (await compile("preview-entry")).replace(/^import .*;\r?\n/gm, "").replace(/^export \{\};?\r?\n/gm, "");
const css = await readFile(resolve(root, "src/overlay.css"), "utf8");
const skull = (await readFile(resolve(root, "../assets/branding/souls-tracker-skull.png"))).toString("base64");
const html = `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; connect-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; base-uri 'none'; form-action 'none'"><style>${css}\nhtml,body{overflow:hidden}#souls-tracker-overlay{transform-origin:top left}#souls-tracker-overlay.souls-tracker-total-deaths-canvas{width:max-content;height:max-content;white-space:nowrap}</style></head><body><main id="souls-tracker-overlay"></main><script>${renderer}\n${entry.replace("__PREVIEW_SKULL__", `data:image/png;base64,${skull}`)}</script></body></html>`;
await mkdir(dirname(output), { recursive: true });
await writeFile(output, html);
