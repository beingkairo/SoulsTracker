import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  fullyParallel: true,
  reporter: "list",
  webServer: {
    command: "node ../cloud_overlay/node_modules/wrangler/bin/wrangler.js dev --local --config ../cloud_overlay/test/browser.wrangler.json --port 8799 --ip localhost --local-protocol https --inspector-port 0",
    url: "https://localhost:8799/overlay/",
    ignoreHTTPSErrors: true,
    reuseExistingServer: false
  },
  use: {
    browserName: "chromium"
  }
});
