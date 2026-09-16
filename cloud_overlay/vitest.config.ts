import { cloudflareTest } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [cloudflareTest({
    wrangler: { configPath: "./wrangler.jsonc" },
    miniflare: { bindings: { PROVISIONED_IDS: ["11111111111111111111111111111111", "22222222222222222222222222222222"] } }
  })],
  test: { include: ["test/**/*.test.ts"] }
});
