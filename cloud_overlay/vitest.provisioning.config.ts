import { cloudflareTest } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";

// A separate local runtime exercises the public router and real DO bindings with
// policy opt-in. No production config, resources or credentials are used.
export default defineConfig({
  plugins: [cloudflareTest({
    wrangler: { configPath: "./test/worker.wrangler.jsonc" },
    miniflare: {
      ratelimits: Object.fromEntries(["PUBLISHER", "LIVE", "CREATE_CLIENT", "CREATE_SERVICE"].map((name, i) =>
        [`${name}_RATE_LIMITER`, { namespace_id: String(960501 + i), simple: { limit: 1000, period: 60 } }])),
      bindings: {
        BROWSER_ORIGIN: "https://overlay.test", PROVISIONED_IDS: [],
        PROVISIONING_CONFIGURATION_VERSION: "3", PROVISIONING_CEILING: "100", PROVISIONING_HARD_MAXIMUM: "100",
        PROVISIONING_ALLOCATION_POLICY: { v: 1, revision: 1, enabled: true, windowSeconds: 86400, maxNewAllocations: 2 },
        PROVISIONING_POLICY_READ_KEY: "e".repeat(64), PROVISIONING_POLICY_APPLY_KEY: "d".repeat(64)
      }
    }
  })],
  test: { include: ["test/public-provisioning.integration.ts"], testTimeout: 15_000 }
});
