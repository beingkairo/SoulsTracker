import { cloudflareTest } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [cloudflareTest({
    wrangler: { configPath: "./wrangler.jsonc" },
    miniflare: {
      ratelimits: {
        PUBLISHER_RATE_LIMITER: { namespace_id: "950501", simple: { limit: 60, period: 60 } },
        LIVE_RATE_LIMITER: { namespace_id: "950502", simple: { limit: 120, period: 60 } },
        CREATE_CLIENT_RATE_LIMITER: { namespace_id: "950503", simple: { limit: 1000, period: 60 } },
        CREATE_SERVICE_RATE_LIMITER: { namespace_id: "950504", simple: { limit: 1000, period: 60 } }
      },
      bindings: { BROWSER_ORIGIN: "https://overlay.test", PROVISIONING_CEILING: "100",
        PROVISIONED_IDS: ["11111111111111111111111111111111", "22222222222222222222222222222222"] }
    }
  })],
  test: { include: ["test/**/*.test.ts"] }
});
