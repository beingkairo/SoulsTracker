import { defineConfig } from "@playwright/test";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

const localState = mkdtempSync(path.join(tmpdir(), "souls-overlay-playwright-"));

export default defineConfig({
  testDir: "./tests",
  fullyParallel: true,
  reporter: "list",
  webServer: {
    command: `node ../cloud_overlay/node_modules/wrangler/bin/wrangler.js dev --local --config ../cloud_overlay/test/browser.wrangler.json --persist-to "${localState}" --port 8799 --ip localhost --local-protocol https --inspector-port 0`,
    url: "https://localhost:8799/soulstracker/",
    ignoreHTTPSErrors: true,
    reuseExistingServer: false
  },
  use: {
    browserName: "chromium"
  }
});
