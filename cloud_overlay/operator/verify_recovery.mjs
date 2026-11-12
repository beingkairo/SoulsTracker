// Offline verification only. Does not deploy, read credentials or contact a service.
import fs from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const sha = data => createHash("sha256").update(data).digest("hex");
const check = (value, message) => { if (!value) throw Error(message); };
check(process.argv.length === 3, "usage: node verify_recovery.mjs <recovery-directory>");
const target = path.resolve(root, process.argv[2]);
const baseline = "cb612a7a72ad024c3c0e209156ec6ba2da309138";
const candidate = path.join(root, "cloud_overlay/dist/operator-candidate", `${baseline}-b950f1ba731b`);
const raw = fs.readFileSync(path.join(target, "manifest.json"));
check(sha(raw) === "85d3c26916f3a1aa108371a2e113f3af66c9f168376d8844e6dcbfb290093927", "reviewed recovery manifest changed");
const manifest = JSON.parse(raw);
check(manifest.source_sha === baseline && manifest.deployment_ready === false && manifest.uncommitted_recovery_sources === true,
  "recovery provenance changed");
check(manifest.candidate_manifest_sha256 === "a9d1cdfe0bbada45c86248409a70bb3bbf798eedc3fe9a6474cf2d4be6b598b7" &&
  sha(fs.readFileSync(path.join(candidate, "manifest.json"))) === manifest.candidate_manifest_sha256, "candidate manifest changed");
const expected = new Set(["manifest.json"]);
for (const item of manifest.files) {
  check(typeof item.path === "string" && !item.path.includes("..") && !path.isAbsolute(item.path) &&
    !item.path.includes("\\") && !item.path.includes("agents/") && !expected.has(item.path), "unsafe or duplicate inventory path");
  expected.add(item.path);
  const data = fs.readFileSync(path.join(target, item.path));
  check(data.length === item.bytes && sha(data) === item.sha256, `inventory mismatch: ${item.path}`);
  if (item.path.startsWith("source/")) check(data.equals(fs.readFileSync(path.join(root, item.path.slice(7)))), `source drift: ${item.path}`);
  if (item.path.startsWith("assets/") || item.path === "config/target.json")
    check(data.equals(fs.readFileSync(path.join(candidate, item.path))), `candidate mismatch: ${item.path}`);
}
function walk(directory, prefix = "") {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const relative = prefix + entry.name;
    check(!entry.isSymbolicLink(), "symlink in inventory");
    if (entry.isDirectory()) walk(path.join(directory, entry.name), relative + "/");
    else check(entry.isFile() && expected.delete(relative), `unexpected inventory entry: ${relative}`);
  }
}
walk(target); check(expected.size === 0, "missing inventory files");
const built = await build({ absWorkingDir: root, entryPoints: ["cloud_overlay/recovery/index.ts"],
  bundle: true, write: false, format: "esm", platform: "neutral", target: "es2022", external: ["cloudflare:workers", "node:*"] });
const worker = fs.readFileSync(path.join(target, "worker/index.js"));
check(worker.equals(Buffer.from(built.outputFiles[0].contents)), "recovery bundle does not reproduce");
const original = JSON.parse(fs.readFileSync(path.join(candidate, "manifest.json")));
check(original.files.filter(item => item.path.startsWith("assets/")).length ===
  manifest.files.filter(item => item.path.startsWith("assets/")).length, "asset inventory changed");
console.log(JSON.stringify({ verified: true, reproduced_worker: true, files: manifest.files.length,
  manifest_sha256: sha(raw), worker_sha256: sha(worker), deployment_ready: false }));
