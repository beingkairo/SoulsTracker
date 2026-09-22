// The existing test toolchain has no @types/node dependency. This is the only
// Node API used by the shared fixture tests.
declare module "node:fs" {
  export function readFileSync(path: URL | string, encoding: "utf8"): string;
  export function readFileSync(path: URL, encoding: "base64"): string;
  export function mkdtempSync(prefix: string): string;
  export function rmSync(path: string, options: { recursive: boolean; force: boolean }): void;
}
declare module "node:child_process" { export function execFileSync(file: string, args: string[]): unknown; }
declare module "node:os" { export function tmpdir(): string; }
declare module "node:path" { export function join(...paths: string[]): string; }
declare module "node:url" { export function fileURLToPath(url: URL): string; }
declare module "node:process" { const process: { execPath: string }; export default process; }
