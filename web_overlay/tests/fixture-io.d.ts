// The existing test toolchain has no @types/node dependency. This is the only
// Node API used by the shared fixture tests.
declare module "node:fs" {
  export function readFileSync(path: URL, encoding: "utf8"): string;
}
