import { expect, test } from "@playwright/test";

test.beforeEach(async ({ page }) => {
  await page.route("http://overlay.test/**", async (route) => {
    const pathname = new URL(route.request().url()).pathname;
    if (pathname.endsWith(".js")) {
      await route.fulfill({ contentType: "text/javascript", path: "dist/src/foundation.js" });
    } else if (pathname.endsWith(".css")) {
      await route.fulfill({ contentType: "text/css", path: "dist/assets/overlay-bootstrap.css" });
    } else {
      await route.fulfill({ contentType: "text/html", body: "<!doctype html><div id=\"souls-tracker-overlay\"></div><script type=\"module\" src=\"/assets/overlay-bootstrap.js\"></script>" });
    }
  });
});

test("Total Deaths route is available", async ({ page }) => {
  await page.goto("http://overlay.test/overlay/total_deaths");
  await expect(page.locator("#souls-tracker-overlay")).toHaveCount(1);
});

test("legacy deaths alias selects Total Deaths", async ({ page }) => {
  await page.goto("http://overlay.test/overlay/deaths");
  await expect(page.locator("#souls-tracker-overlay")).toHaveCount(1);
});
