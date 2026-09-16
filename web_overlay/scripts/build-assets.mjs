import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");
const output = resolve(root, "dist", "assets");

await mkdir(output, { recursive: true });
await copyFile(resolve(root, "dist", "src", "foundation.js"), resolve(output, "overlay-bootstrap.js"));
await copyFile(resolve(root, "src", "overlay.css"), resolve(output, "overlay-bootstrap.css"));
await mkdir(resolve(root, "dist/hosted/overlay"), { recursive: true });
const hosted = resolve(root, "dist/hosted");
await mkdir(resolve(hosted, "assets"), { recursive: true });
const emit = async (name, extension, content) => {
  const hash = createHash("sha256").update(content).digest("hex");
  const filename = `${name}.${hash}.${extension}`;
  await writeFile(resolve(hosted, "assets", filename), content);
  return `/assets/${filename}`;
};
// Keep previously emitted hashes for rollout overlap; never empty this tree.
const skull = await emit("skull", "png", await readFile(resolve(root, "../assets/branding/souls-tracker-skull.png")));
const css = await emit("overlay", "css", await readFile(resolve(root, "src/overlay.css")));
const modules = new Map();
for (const name of ["hosted-contracts", "hosted-client", "hosted-renderer", "hosted-entry"]) {
  let source = await readFile(resolve(root, `dist/src/${name}.js`), "utf8");
  for (const [dependency, asset] of modules) source = source.replaceAll(`./${dependency}.js`, asset);
  source = source.replaceAll("__HOSTED_SKULL__", skull);
  modules.set(name, await emit(name, "js", source));
}
const shell = await readFile(resolve(root, "hosted/overlay/index.html"), "utf8");
await writeFile(resolve(hosted, "overlay/index.html"), shell.replace("__CSS__", css).replace("__ENTRY__", modules.get("hosted-entry")));
await copyFile(resolve(root, "hosted/_headers"), resolve(hosted, "_headers"));
