// Local build/freeze only. No Wrangler, credentials or network access.
import fs from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const baseline = "cb612a7a72ad024c3c0e209156ec6ba2da309138";
const historical = "a9ce3693077c8adf9fd12d8cd1234fee0c8d2fbf";
const candidateName = `${baseline}-b950f1ba731b`;
const candidate = path.join(root, "cloud_overlay/dist/operator-candidate", candidateName);
const digest = data => createHash("sha256").update(data).digest("hex");
const require = (ok, message) => { if (!ok) throw Error(message); };
const git = (...args) => execFileSync("git", args, { cwd: root, encoding: "utf8", windowsHide: true }).trim();
require(git("rev-parse", "HEAD") === baseline, "recovery base changed");
const manifestBytes = fs.readFileSync(path.join(candidate, "manifest.json"));
require(digest(manifestBytes) === "a9d1cdfe0bbada45c86248409a70bb3bbf798eedc3fe9a6474cf2d4be6b598b7", "candidate manifest changed");
const original = JSON.parse(manifestBytes);
for (const item of original.files) {
  const data = fs.readFileSync(path.join(candidate, item.path));
  require(data.length === item.bytes && digest(data) === item.sha256, "candidate file changed");
}
const result = await build({ absWorkingDir: root, entryPoints: ["cloud_overlay/recovery/index.ts"],
  bundle: true, write: false, format: "esm", platform: "neutral", target: "es2022",
  external: ["cloudflare:workers", "node:*"], metafile: true });
const worker = result.outputFiles[0].contents;
require(digest(worker) !== digest(fs.readFileSync(path.join(candidate, "worker/index.js"))), "recovery code is identical");
for (const marker of ["client_update_required", "leaseExpiresAt", "applyConfiguredPolicy", "policyStatus"])
  require(Buffer.from(worker).includes(marker), "required protection absent");
for (const marker of ["reserveNewAllocation", "fixtureSeed", "fixtureClaim", "readAuthorityAggregate"])
  require(!Buffer.from(worker).includes(marker), "unexpected allocation/diagnostic/test implementation");
const config = fs.readFileSync(path.join(candidate, "config/target.json"));
const settings = JSON.parse(config);
require(settings.vars.PROVISIONING_CONFIGURATION_VERSION === "3" && settings.vars.PROVISIONING_CEILING === "100" &&
  settings.vars.PROVISIONING_HARD_MAXIMUM === "100", "authority configuration changed");
require(Object.keys(settings.vars).sort().join(",") === ["BROWSER_ORIGIN", "PROVISIONED_IDS", "PROVISIONING_CEILING",
  "PROVISIONING_CONFIGURATION_VERSION", "PROVISIONING_HARD_MAXIMUM"].sort().join(","), "unexpected variables");
require(!settings.services?.length && settings.workers_dev === false && settings.preview_urls === false, "deployment boundary changed");
const inputs = new Map([["worker/index.js", worker], ["config/target.json", config]]);
for (const item of original.files.filter(item => item.path.startsWith("assets/")))
  inputs.set(item.path, fs.readFileSync(path.join(candidate, item.path)));
const sources = Object.keys(result.metafile.inputs).sort();
for (const name of sources) {
  require(!name.includes("..") && !name.startsWith("agents/") && !name.includes("node_modules"), "unexpected source input");
  const data = fs.readFileSync(path.join(root, name));
  if (!name.startsWith("cloud_overlay/recovery/"))
    require(data.toString().replaceAll("\r\n", "\n").trimEnd() === git("show", `${baseline}:${name}`), "shared baseline source changed");
  inputs.set(`source/${name}`, data);
}
inputs.set("source/cloud_overlay/operator/package_recovery.mjs", fs.readFileSync(fileURLToPath(import.meta.url)));
const files = [...inputs].sort(([a], [b]) => a.localeCompare(b)).map(([name, data]) =>
  ({ path: name, bytes: data.length, sha256: digest(data) }));
const manifest = { manifest_schema_version: 1, source_sha: baseline, historical_authority_sha: historical,
  uncommitted_recovery_sources: true, deployment_ready: false, candidate_manifest_sha256: digest(manifestBytes),
  purpose: "Pre-activation public recovery; disabled gateway and authentication profile stay untouched",
  strategy: "Historical lifetime admission; explicit authenticated policy controls retained; configured or stored policy blocks new allocations",
  limitations: ["Shared publisher/protocol/browser security code is not an independent fallback for defects in those shared components",
    "No scalable allocation admission; never delete policy state or downgrade to pre-protocol code"],
  configuration_identical_to_candidate: true, files };
const suffix = digest(JSON.stringify(files)).slice(0, 12);
const destination = path.join(root, "cloud_overlay/dist/public-recovery", `${baseline}-${suffix}`);
require(!fs.existsSync(destination), "immutable recovery destination already exists");
for (const [name, data] of inputs) {
  const output = path.join(destination, name);
  fs.mkdirSync(path.dirname(output), { recursive: true }); fs.writeFileSync(output, data, { flag: "wx" });
}
const frozen = Buffer.from(JSON.stringify(manifest, null, 2) + "\n");
fs.writeFileSync(path.join(destination, "manifest.json"), frozen, { flag: "wx" });
console.log(JSON.stringify({ path: path.relative(root, destination).replaceAll("\\", "/"),
  manifest_sha256: digest(frozen), worker_sha256: digest(worker), config_sha256: digest(config),
  files: files.length, deployment_ready: false }));
