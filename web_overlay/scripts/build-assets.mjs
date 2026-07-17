import { copyFile, mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");
const output = resolve(root, "dist", "assets");

await mkdir(output, { recursive: true });
await copyFile(resolve(root, "dist", "src", "foundation.js"), resolve(output, "overlay-bootstrap.js"));
await copyFile(resolve(root, "src", "overlay.css"), resolve(output, "overlay-bootstrap.css"));
