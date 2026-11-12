import { cloudflareTest } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";

// Local acceptance only. Reuse the public acceptance suites against recovery.
export default defineConfig({
  plugins: [cloudflareTest({
    main: "./recovery/index.ts",
    wrangler: { configPath: "./test/worker.wrangler.jsonc" },
    miniflare: {
      ratelimits: {
        PUBLISHER_RATE_LIMITER: { namespace_id: "950501", simple: { limit: 60, period: 60 } },
        LIVE_RATE_LIMITER: { namespace_id: "950502", simple: { limit: 120, period: 60 } },
        CREATE_CLIENT_RATE_LIMITER: { namespace_id: "950503", simple: { limit: 1000, period: 60 } },
        CREATE_SERVICE_RATE_LIMITER: { namespace_id: "950504", simple: { limit: 1000, period: 60 } }
      },
      bindings: { BROWSER_ORIGIN: "https://overlay.test", PROVISIONING_CONFIGURATION_VERSION: "1",
        PROVISIONING_CEILING: "100", PROVISIONING_HARD_MAXIMUM: "1000",
        PROVISIONED_IDS: ["11111111111111111111111111111111", "22222222222222222222222222222222"] }
    }
  })],
  test: { include: ["test/authority.test.ts", "test/live.test.ts", "test/provisioning.test.ts",
    "test/admission*.test.ts", "test/bootstrap.test.ts", "recovery/recovery.test.ts"] }
});
