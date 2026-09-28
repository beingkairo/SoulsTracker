import { cloudflareTest } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [cloudflareTest({
    wrangler: { configPath: "./wrangler.jsonc" },
    miniflare: {
      ratelimits: {
        PUBLISHER_RATE_LIMITER: { namespace_id: "950501", simple: { limit: 60, period: 60 } },
        LIVE_RATE_LIMITER: { namespace_id: "950502", simple: { limit: 120, period: 60 } },
        PROVISIONING_RATE_LIMITER: { namespace_id: "950503", simple: { limit: 10, period: 60 } }
      },
      bindings: { BROWSER_ORIGIN: "https://overlay.test", PROVISIONED_IDS: ["11111111111111111111111111111111", "22222222222222222222222222222222"],
        PROVISIONING_SLOTS: [{ v: 1, overlayId: "11111111111111111111111111111111", setupVerifier: "29abcc214743c1c7c5d372bf9ab577d850b815e5a42c4d433d5da86c8f89b53d" }] }
    }
  })],
  test: { include: ["test/**/*.test.ts"] }
});
