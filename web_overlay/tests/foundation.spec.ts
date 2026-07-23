import { expect, test, type Page } from "@playwright/test";

interface OverlayHostProcess {
  stdout: { on(event: "data", listener: (chunk: unknown) => void): void };
  once(event: "error", listener: (error: Error) => void): void;
  once(event: "exit", listener: (code: number | null) => void): void;
  kill(): boolean;
}

const childProcess = await import(("node:child_process") as string) as unknown as {
  spawn(command: string, args: string[], options: { cwd: string; stdio: ["ignore", "pipe", "pipe"] }): OverlayHostProcess;
};
const nodeProcess = await import(("node:process") as string) as unknown as { default: { cwd(): string } };

const token = "test-token";
const overlayDocument = `<!doctype html><html><head><link rel="stylesheet" href="/overlay/assets/overlay-bootstrap.css?token=${token}"></head><body><div id="souls-tracker-overlay"></div><script type="module" src="/overlay/assets/overlay-bootstrap.js?token=${token}"></script></body></html>`;

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    class ControlledWebSocket {
      public static readonly sockets: ControlledWebSocket[] = [];
      public onopen: ((event: Event) => void) | null = null;
      public onmessage: ((event: MessageEvent<string>) => void) | null = null;
      public onclose: ((event: CloseEvent) => void) | null = null;
      public onerror: ((event: Event) => void) | null = null;

      public constructor(public readonly url: string) {
        ControlledWebSocket.sockets.push(this);
        queueMicrotask(() => this.onopen?.(new Event("open")));
      }

      public close(): void {
        this.onclose?.(new CloseEvent("close"));
      }

      public emit(snapshot: unknown): void {
        this.onmessage?.(new MessageEvent("message", { data: JSON.stringify(snapshot) }));
      }
    }

    Object.defineProperty(window, "WebSocket", { configurable: true, value: ControlledWebSocket });
    Object.defineProperty(window, "__overlaySockets", { configurable: true, value: ControlledWebSocket.sockets });
  });

  await page.route("http://overlay.test/**", async (route) => {
    const pathname = new URL(route.request().url()).pathname;
    if (pathname.startsWith("/overlay/assets/overlay-bootstrap.js")) {
      await route.fulfill({ contentType: "text/javascript", path: "dist/src/foundation.js" });
    } else if (pathname.startsWith("/overlay/assets/overlay-bootstrap.css")) {
      await route.fulfill({ contentType: "text/css", path: "dist/assets/overlay-bootstrap.css" });
    } else if (pathname.startsWith("/overlay/")) {
      await route.fulfill({ contentType: "text/html", body: overlayDocument });
    } else {
      await route.abort();
    }
  });
});

test("canonical Total Deaths route is transparent and renders dynamic updates", async ({ page }) => {
  await openOverlay(page, "/overlay/total_deaths");
  await emit(page, snapshot(1, { selectedGame: "Bloodborne", totalDeaths: 42 }));

  await expect(page.getByTestId("total-deaths-overlay")).toBeVisible();
  await expect(page.getByTestId("game-name")).toHaveCount(0);
  await expect(page.getByRole("heading")).toContainText("42");
  await expect(page.locator("html")).toHaveCSS("background-color", "rgba(0, 0, 0, 0)");

  await emit(page, snapshot(2, { selectedGame: "Bloodborne", totalDeaths: 43 }));
  await expect(page.getByRole("heading")).toContainText("43");
});

test("legacy deaths alias selects the Total Deaths overlay", async ({ page }) => {
  await openOverlay(page, "/overlay/deaths");
  await emit(page, snapshot(1, { selectedGame: "Bloodborne", totalDeaths: 7 }));

  await expect(page.getByTestId("total-deaths-overlay")).toBeVisible();
  await expect(page.getByRole("heading")).toContainText("7");
});

test("canonical Boss List route renders progress without redundant visible status labels or HTML injection", async ({ page }) => {
  await openOverlay(page, "/overlay/boss_list");
  await emit(page, snapshot(1, {
    selectedGame: "<img src=x onerror=alert(1)>",
    bosses: [
      { DisplayName: "Cleric Beast", DlcLabel: null, IsDefeated: false },
      { DisplayName: "<script>window.pwned = true</script>", DlcLabel: "<b>DLC</b>", IsDefeated: true },
    ],
  }));

  await expect(page.getByTestId("boss-list-overlay")).toBeVisible();
  await expect(page.getByTestId("boss-entry")).toHaveCount(2);
  await expect(page.getByTestId("boss-entry").nth(0)).toHaveClass("is-remaining");
  await expect(page.getByTestId("boss-entry").nth(0)).not.toContainText("Remaining");
  await expect(page.getByTestId("boss-entry").nth(1)).toHaveClass("is-defeated");
  await expect(page.getByTestId("boss-entry").nth(1)).not.toContainText("Defeated");
  await expect(page.locator("#souls-tracker-overlay img, #souls-tracker-overlay script")).toHaveCount(0);
  await expect(page.locator("body")).toContainText("<script>window.pwned = true</script>");
  expect(await page.evaluate(() => (window as unknown as { pwned?: boolean }).pwned)).toBeUndefined();
});

test("Boss List category labels stay out of the overlay", async ({ page }) => {
  await openOverlay(page, "/overlay/boss_list");
  await emit(page, snapshot(1, {
    selectedGame: "Elden Ring",
    bosses: [
      { DisplayName: "Radahn (Promised Consort)", DlcLabel: "Shadow of the Erdtree", IsDefeated: false },
      { DisplayName: "Other DLC boss", DlcLabel: "The Old Hunters", IsDefeated: false },
    ],
  }));

  await expect(page.getByTestId("boss-list")).toContainText("Radahn (Promised Consort)");
  await expect(page.getByTestId("boss-list")).not.toContainText("Shadow of the Erdtree");
  await expect(page.getByTestId("boss-list")).not.toContainText("The Old Hunters");
});

test("legacy boss alias ignores stale snapshots and reconnects after a disconnect", async ({ page }) => {
  await openOverlay(page, "/overlay/boss-progress");
  await emit(page, snapshot(2, { selectedGame: "Bloodborne", bosses: [{ DisplayName: "Vicar Amelia", DlcLabel: null, IsDefeated: false }] }));
  await expect(page.getByTestId("boss-entry")).toContainText("Vicar Amelia");

  await emit(page, snapshot(1, { selectedGame: "Bloodborne", bosses: [{ DisplayName: "Stale Boss", DlcLabel: null, IsDefeated: true }] }));
  await expect(page.getByTestId("boss-entry")).toContainText("Vicar Amelia");

  await page.evaluate(() => {
    const sockets = (window as unknown as { __overlaySockets: Array<{ close(): void }> }).__overlaySockets;
    sockets.at(-1)?.close();
  });
  await expect(page.getByTestId("connection-state")).toHaveText("SoulsTracker connection unavailable");
  await expect.poll(() => page.evaluate(() => (window as unknown as { __overlaySockets: unknown[] }).__overlaySockets.length)).toBeGreaterThan(1);

  await emit(page, snapshot(3, { selectedGame: "Bloodborne", bosses: [{ DisplayName: "Shadows of Yharnam", DlcLabel: null, IsDefeated: true }] }));
  await expect(page.getByTestId("boss-entry")).toContainText("Shadows of Yharnam");
});

test("unavailable Total Deaths is rendered as a safe display state", async ({ page }) => {
  await openOverlay(page, "/overlay/total_deaths");
  await emit(page, snapshot(1, { selectedGame: "Dark Souls III", totalDeaths: null, source: "Unavailable" }));

  await expect(page.getByRole("heading")).toContainText("Unavailable");
});

test("validated presentation settings hide the game name and disabled Total Deaths layout", async ({ page }) => {
  await openOverlay(page, "/overlay/total_deaths");
  await emit(page, snapshot(1, {
    selectedGame: "Bloodborne",
    totalDeaths: 42,
    presentation: { IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All" },
  }));
  await expect(page.getByRole("heading")).toContainText("42");
  await expect(page.getByTestId("game-name")).toHaveCount(0);

  await emit(page, snapshot(2, {
    presentation: { IsTotalDeathsEnabled: false, ShowGameName: true, IsBossListEnabled: true, BossListVisibilityMode: "All" },
  }));
  await expect(page.getByTestId("total-deaths-overlay")).toHaveCount(0);
});

test("validated boss visibility and enablement control the boss-list layout", async ({ page }) => {
  await openOverlay(page, "/overlay/boss_list");
  const bosses = [
    { DisplayName: "Remaining Boss", DlcLabel: null, IsDefeated: false },
    { DisplayName: "Defeated Boss", DlcLabel: null, IsDefeated: true },
  ];
  await emit(page, snapshot(1, {
    bosses,
    presentation: { IsTotalDeathsEnabled: true, ShowGameName: true, IsBossListEnabled: true, BossListVisibilityMode: "Remaining" },
  }));
  await expect(page.getByTestId("boss-entry")).toHaveCount(1);
  await expect(page.getByTestId("boss-entry")).toContainText("Remaining Boss");

  await emit(page, snapshot(2, {
    bosses,
    presentation: { IsTotalDeathsEnabled: true, ShowGameName: true, IsBossListEnabled: true, BossListVisibilityMode: "Defeated" },
  }));
  await expect(page.getByTestId("boss-entry")).toHaveCount(1);
  await expect(page.getByTestId("boss-entry")).toContainText("Defeated Boss");

  await emit(page, snapshot(3, {
    presentation: { IsTotalDeathsEnabled: true, ShowGameName: true, IsBossListEnabled: false, BossListVisibilityMode: "All" },
  }));
  await expect(page.getByTestId("boss-list-overlay")).toHaveCount(0);
});

test("typed appearance matrix is reflected in rendered overlay styles and boss treatment", async ({ page }) => {
  await openOverlay(page, "/overlay/boss_list");
  await emit(page, {
    SchemaVersion: 1, SequenceNumber: 1, SelectedGame: { DisplayName: "Bloodborne" },
    TotalDeaths: { Source: "ManualBloodborne", Value: 12 },
    Bosses: [{ DisplayName: "Defeated", DlcLabel: null, IsDefeated: true }, { DisplayName: "Remaining", DlcLabel: null, IsDefeated: false }],
    Presentation: {
      IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All",
      TotalDeathsCompactTitle: true,
      TotalDeathsAppearance: { Title: "DEATHS", FontFamily: "Arial", FontSize: 31, TextColor: "#112233", AccentColor: "#445566", BackgroundColor: "#778899", BackgroundOpacity: 45, Padding: 13, CornerRadius: 7, Alignment: "Right" },
      BossListAppearance: { Title: "CUSTOM BOSSES", FontFamily: "Verdana", FontSize: 29, TextColor: "#AABBCC", AccentColor: "#DDEEFF", BackgroundColor: "#102030", BackgroundOpacity: 55, Padding: 11, CornerRadius: 6, Alignment: "Center" },
      BossListDefeatedColor: "#123456", BossListDefeatedTreatment: "Dimmed", BossListShowCheckmark: true, BossListCheckmarkAccent: "#654321", BossListMaximumVisibleCount: 1,
    },
  });
  const panel = page.getByTestId("boss-list-overlay");
  await expect(panel).toContainText("CUSTOM BOSSES");
  await expect(panel).toHaveCSS("font-family", "Verdana");
  await expect(panel).toHaveCSS("font-size", "29px");
  await expect(panel).toHaveCSS("color", "rgb(170, 187, 204)");
  await expect(panel).toHaveCSS("padding-top", "11px");
  await expect(panel).toHaveCSS("border-top-left-radius", "6px");
  await expect(panel).toHaveCSS("text-align", "center");
  await expect(page.getByTestId("boss-entry")).toHaveCount(1);
  await expect(page.getByTestId("boss-entry")).toHaveCSS("--defeated-color", "#123456");
  await expect(page.locator(".overlay-checkmark")).toHaveCount(0);
});

test("total deaths title treatment and typed appearance are reflected", async ({ page }) => {
  await openOverlay(page, "/overlay/total_deaths");
  const appearance = { Title: "CUSTOM DEATHS", FontFamily: "Arial", FontSize: 35, TextColor: "#102030", AccentColor: "#405060", BackgroundColor: "#708090", BackgroundOpacity: 60, Padding: 9, CornerRadius: 4, Alignment: "Right" };
  await emit(page, { SchemaVersion: 1, SequenceNumber: 1, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [], Presentation: { IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All", TotalDeathsCompactTitle: true, TotalDeathsAppearance: appearance, BossListAppearance: { ...appearance, Title: "BOSSES" }, BossListDefeatedColor: "#808080", BossListDefeatedTreatment: "Strikethrough", BossListShowCheckmark: true, BossListCheckmarkAccent: "#A78BFA", BossListMaximumVisibleCount: 25 } });
  const panel = page.getByTestId("total-deaths-overlay");
  await expect(panel).toContainText("CUSTOM DEATHS");
  await expect(panel.getByRole("heading")).toHaveText("CUSTOM DEATHS: 9");
  await expect(panel).toHaveCSS("font-family", "Arial");
  await expect(panel).toHaveCSS("font-size", "35px");
  await expect(panel).toHaveCSS("color", "rgb(16, 32, 48)");
  await expect(panel).toHaveCSS("text-align", "left");
  await expect(panel).toHaveCSS("padding-top", "9px");
  await expect(page.getByTestId("game-name")).toHaveCount(0);
});

test("total deaths title icon modes and selected game name render safely", async ({ page }) => {
  await openOverlay(page, "/overlay/total_deaths");
  const appearance = { Title: "DEATHS", FontFamily: "Segoe UI", FontSize: 42, TextColor: "#FFFFFF", AccentColor: "#A78BFA", BackgroundColor: "#15171B", BackgroundOpacity: 88, Padding: 16, CornerRadius: 8, Alignment: "Left" };
  const presentation = { IsTotalDeathsEnabled: true, ShowGameName: true, IsBossListEnabled: true, BossListVisibilityMode: "All" as const, TotalDeathsCompactTitle: true, TotalDeathsAppearance: appearance, BossListAppearance: { ...appearance, Title: "BOSSES" }, BossListDefeatedColor: "#808080", BossListDefeatedTreatment: "Strikethrough" as const, BossListShowCheckmark: true, BossListCheckmarkAccent: "#A78BFA", BossListMaximumVisibleCount: 25 };

  await emit(page, { SchemaVersion: 1, SequenceNumber: 1, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [], Presentation: { ...presentation, TotalDeathsTitleIconMode: "PrefixSkull" } });
  await expect(page.getByRole("heading")).toContainText("DEATHS: 9");
  await expect(page.locator(".overlay-title-skull")).toHaveAttribute("src", /souls-tracker-skull\.png/);
  await expect(page.getByTestId("game-name")).toHaveCount(0);

  await emit(page, { SchemaVersion: 1, SequenceNumber: 2, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [], Presentation: { ...presentation, TotalDeathsTitleIconMode: "SkullOnly" } });
  await expect(page.getByRole("heading")).toHaveText("9");
  await expect(page.getByTestId("total-deaths-value")).toHaveCount(0);
  await expect(page.locator(".overlay-title-skull")).toHaveAttribute("src", /souls-tracker-skull\.png/);

  await emit(page, { SchemaVersion: 1, SequenceNumber: 3, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [], Presentation: { ...presentation, TotalDeathsTitleIconMode: "unexpected" } });
  await expect(page.getByRole("heading")).toHaveText("DEATHS: 9");
});

test("skull-only Total Deaths keeps the count and inherits applied text effects without altering the skull asset", async ({ page }) => {
  await openOverlay(page, "/overlay/total_deaths?styleVersion=1&outline=true&outlineColor=%23112233&outlineWidth=2&shadow=true&shadowColor=%23445566&shadowX=3&shadowY=4&shadowBlur=5");
  const appearance = { Title: "DEATHS", FontFamily: "Segoe UI", FontSize: 42, TextColor: "#FFFFFF", AccentColor: "#A78BFA", BackgroundColor: "#15171B", BackgroundOpacity: 88, Padding: 16, CornerRadius: 8, Alignment: "Left" };
  await emit(page, { SchemaVersion: 1, SequenceNumber: 1, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [], Presentation: { IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All", TotalDeathsCompactTitle: true, TotalDeathsTitleIconMode: "SkullOnly", TotalDeathsAppearance: appearance, BossListAppearance: { ...appearance, Title: "BOSSES" }, BossListDefeatedColor: "#808080", BossListDefeatedTreatment: "Nothing", BossListShowCheckmark: false, BossListCheckmarkAccent: "#A78BFA", BossListMaximumVisibleCount: 25, BossListShowDefeatedSkull: false } });

  await expect(page.getByRole("heading")).toHaveText("9");
  await expect(page.getByTestId("total-deaths-overlay")).toHaveCSS("text-shadow", /rgb\(17, 34, 51\).*rgb\(68, 85, 102\)/);
  await expect(page.locator(".overlay-title-skull")).toHaveCSS("filter", /url\("#skull/);
  await expect(page.locator(".overlay-title-skull")).toHaveCSS("width", "96.5938px");
  await expect(page.locator(".overlay-title-skull")).toHaveAttribute("src", /souls-tracker-skull\.png/);
  const externalOutline = await page.locator(".overlay-title-skull").evaluate((skull: Element) => {
    const id = getComputedStyle(skull).filter.match(/#([^\")]+)/)?.[1];
    const filter = id === undefined ? null : document.getElementById(id);
    return filter?.querySelector('feComposite[in="expanded-outline"][in2="SourceAlpha"][operator="out"]') !== null;
  });
  expect(externalOutline).toBeTruthy();
});

test("blank overlay titles remove only the title row", async ({ page }) => {
  await openOverlay(page, "/overlay/total_deaths");
  const appearance = { Title: "", FontFamily: "Segoe UI", FontSize: 30, TextColor: "#FFFFFF", AccentColor: "#A78BFA", BackgroundColor: "#15171B", BackgroundOpacity: 88, Padding: 16, CornerRadius: 8, Alignment: "Left" };
  await emit(page, { SchemaVersion: 1, SequenceNumber: 1, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [], Presentation: { IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All", TotalDeathsCompactTitle: true, TotalDeathsTitleIconMode: "SkullOnly", TotalDeathsAppearance: appearance, BossListAppearance: { ...appearance, Title: "" }, BossListDefeatedColor: "#808080", BossListDefeatedTreatment: "Nothing", BossListShowCheckmark: false, BossListCheckmarkAccent: "#A78BFA", BossListMaximumVisibleCount: 25, BossListShowDefeatedSkull: false } });
  await expect(page.getByRole("heading")).toHaveCount(0);
  await expect(page.getByTestId("total-deaths-value")).toHaveText("9");

  await openOverlay(page, "/overlay/boss_list");
  await emit(page, { SchemaVersion: 1, SequenceNumber: 1, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [{ DisplayName: "Boss", DlcLabel: null, IsDefeated: false }], Presentation: { IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All", TotalDeathsCompactTitle: true, TotalDeathsTitleIconMode: "Off", TotalDeathsAppearance: appearance, BossListAppearance: { ...appearance, Title: "" }, BossListDefeatedColor: "#808080", BossListDefeatedTreatment: "Nothing", BossListShowCheckmark: false, BossListCheckmarkAccent: "#A78BFA", BossListMaximumVisibleCount: 25, BossListShowDefeatedSkull: false } });
  await expect(page.getByRole("heading")).toHaveCount(0);
  await expect(page.getByTestId("boss-entry")).toHaveCount(1);
});

test("defeated boss marker modes render zero or one marker", async ({ page }) => {
  await openOverlay(page, "/overlay/boss_list");
  const appearance = { Title: "BOSSES", FontFamily: "Segoe UI", FontSize: 42, TextColor: "#FFFFFF", AccentColor: "#A78BFA", BackgroundColor: "#15171B", BackgroundOpacity: 88, Padding: 16, CornerRadius: 8, Alignment: "Left" };
  const snapshot = { SchemaVersion: 1, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [{ DisplayName: "Boss One", DlcLabel: null, IsDefeated: true }], Presentation: { IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All" as const, TotalDeathsCompactTitle: false, TotalDeathsAppearance: appearance, BossListAppearance: appearance, BossListDefeatedColor: "#123456", BossListDefeatedTreatment: "Strikethrough" as const, BossListShowCheckmark: true, BossListCheckmarkAccent: "#A78BFA", BossListMaximumVisibleCount: 25, BossListShowDefeatedSkull: true } };
  await emit(page, { ...snapshot, SequenceNumber: 1 });
  await expect(page.locator(".overlay-defeated-skull")).toHaveAttribute("src", /souls-tracker-skull\.png/);
  await expect(page.locator(".overlay-checkmark")).toHaveCount(0);

  await emit(page, { ...snapshot, SequenceNumber: 2, Presentation: { ...snapshot.Presentation, BossListShowDefeatedSkull: false, BossListShowCheckmark: true } });
  await expect(page.locator(".overlay-defeated-skull")).toHaveCount(0);
  await expect(page.locator(".overlay-checkmark")).toHaveText("✓");

  await emit(page, { ...snapshot, SequenceNumber: 3, Presentation: { ...snapshot.Presentation, BossListShowDefeatedSkull: false, BossListShowCheckmark: false } });
  await expect(page.locator(".overlay-defeated-skull")).toHaveCount(0);
  await expect(page.locator(".overlay-checkmark")).toHaveCount(0);

  await emit(page, { ...snapshot, SequenceNumber: 4, Presentation: { ...snapshot.Presentation, BossListVisibilityMode: "Remaining", BossListShowCheckmark: false } });
  await expect(page.getByTestId("boss-entry")).toHaveCount(0);
  await expect(page.locator(".overlay-defeated-skull")).toHaveCount(0);
});

test("readable per-URL query settings override only the matching boss renderer", async ({ page }) => {
  await openOverlay(page, "/overlay/boss_list?styleVersion=1&title=Scene%20Bosses&font=Arial&size=24&textColor=%23112233&backgroundColor=%23010203&backgroundOpacity=40&alignment=Right&mode=Defeated&defeatedColor=%23445566&treatment=Dimmed&marker=Skull&maximumVisible=1&bossRowSpacing=9");
  await emit(page, { SchemaVersion: 1, SequenceNumber: 1, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [{ DisplayName: "Defeated", DlcLabel: null, IsDefeated: true }, { DisplayName: "Remaining", DlcLabel: null, IsDefeated: false }], Presentation: { IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All", TotalDeathsCompactTitle: true, TotalDeathsAppearance: { Title: "TOTAL DEATHS", FontFamily: "Segoe UI", FontSize: 42, TextColor: "#FFFFFF", AccentColor: "#A78BFA", BackgroundColor: "#15171B", BackgroundOpacity: 88, Padding: 16, CornerRadius: 8, Alignment: "Left" }, BossListAppearance: { Title: "BOSS LIST", FontFamily: "Segoe UI", FontSize: 42, TextColor: "#FFFFFF", AccentColor: "#A78BFA", BackgroundColor: "#15171B", BackgroundOpacity: 88, Padding: 16, CornerRadius: 8, Alignment: "Left" }, BossListDefeatedColor: "#8C8C96", BossListDefeatedTreatment: "Nothing", BossListShowCheckmark: false, BossListCheckmarkAccent: "#A78BFA", BossListMaximumVisibleCount: 25, BossListShowDefeatedSkull: false } });
  const panel = page.getByTestId("boss-list-overlay");
  await expect(panel.getByRole("heading")).toHaveText("Scene Bosses");
  await expect(panel).toHaveCSS("font-size", "24px");
  await expect(panel).toHaveCSS("text-align", "right");
  await expect(page.getByTestId("boss-entry")).toHaveCount(1);
  await expect(page.locator(".overlay-defeated-skull")).toHaveAttribute("src", /souls-tracker-skull\.png/);
  await expect(page.getByTestId("boss-list")).toHaveCSS("row-gap", "9px");
  await expect(page.getByTestId("boss-entry")).toHaveCSS("--defeated-color", "#445566");
});

test("boss treatment modes are exclusive and boss row spacing changes only row separation", async ({ page }) => {
  await openOverlay(page, "/overlay/boss_list");
  const appearance = { Title: "BOSS LIST", FontFamily: "Segoe UI", FontSize: 30, TextColor: "#FFFFFF", AccentColor: "#A78BFA", BackgroundColor: "#15171B", BackgroundOpacity: 88, Padding: 3, CornerRadius: 8, Alignment: "Left" };
  const presentation = { IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All" as const, TotalDeathsCompactTitle: true, TotalDeathsAppearance: appearance, BossListAppearance: appearance, BossListDefeatedColor: "#123456", BossListShowCheckmark: false, BossListCheckmarkAccent: "#A78BFA", BossListMaximumVisibleCount: 25, BossListShowDefeatedSkull: false };
  const snapshot = { SchemaVersion: 1, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [{ DisplayName: "Defeated", DlcLabel: null, IsDefeated: true }, { DisplayName: "Remaining", DlcLabel: null, IsDefeated: false }] };
  await emit(page, { ...snapshot, SequenceNumber: 1, Presentation: { ...presentation, BossListDefeatedTreatment: "Nothing" } });
  const name = page.getByTestId("boss-entry").first().locator(".overlay-boss-name");
  await expect(name).toHaveCSS("color", "rgb(18, 52, 86)");
  await expect(name).toHaveCSS("text-decoration-line", "none");
  await expect(name).toHaveCSS("opacity", "1");
  await emit(page, { ...snapshot, SequenceNumber: 2, Presentation: { ...presentation, BossListDefeatedTreatment: "Dimmed" } });
  await expect(name).toHaveCSS("text-decoration-line", "none");
  await expect(name).toHaveCSS("opacity", "0.62");
  await emit(page, { ...snapshot, SequenceNumber: 3, Presentation: { ...presentation, BossListDefeatedTreatment: "Strikethrough" } });
  await expect(name).toHaveCSS("text-decoration-line", "line-through");
  await expect(name).toHaveCSS("opacity", "1");
  await expect(page.getByTestId("boss-list")).toHaveCSS("row-gap", "3px");
});

test("skull and checkmark markers align to the selected boss-list layout", async ({ page }) => {
  await openOverlay(page, "/overlay/boss_list");
  const baseAppearance = { Title: "BOSS LIST", FontFamily: "Segoe UI", FontSize: 30, TextColor: "#FFFFFF", AccentColor: "#A78BFA", BackgroundColor: "#15171B", BackgroundOpacity: 88, Padding: 3, CornerRadius: 8, Alignment: "Left" };
  const base = { SchemaVersion: 1, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [{ DisplayName: "A very long defeated boss name", DlcLabel: "DLC", IsDefeated: true }], Presentation: { IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All" as const, TotalDeathsCompactTitle: true, TotalDeathsAppearance: baseAppearance, BossListDefeatedColor: "#123456", BossListDefeatedTreatment: "Nothing" as const, BossListShowCheckmark: false, BossListCheckmarkAccent: "#A78BFA", BossListMaximumVisibleCount: 25, BossListShowDefeatedSkull: true } };
  await emit(page, { ...base, SequenceNumber: 1, Presentation: { ...base.Presentation, BossListAppearance: { ...baseAppearance, Alignment: "Left" } } });
  await expect(page.getByTestId("boss-list")).toHaveClass(/overlay-boss-list-left/);
  await expect(page.getByTestId("boss-entry")).toHaveAttribute("data-marker", "skull");
  await expect(page.locator(".overlay-defeated-skull")).toHaveCSS("width", "40.5px");
  const leftMarkerBeforeName = await page.getByTestId("boss-entry").evaluate((entry: Element) => {
    const marker = entry.querySelector(".overlay-defeated-skull")!;
    const name = entry.querySelector(".overlay-boss-name")!;
    return marker.compareDocumentPosition(name) & Node.DOCUMENT_POSITION_FOLLOWING;
  });
  expect(leftMarkerBeforeName).toBeTruthy();
  const leftGeometry = await page.getByTestId("boss-entry").evaluate((entry: Element) => {
    const marker = entry.querySelector(".overlay-defeated-skull")!.getBoundingClientRect();
    const name = entry.querySelector(".overlay-boss-name")!.getBoundingClientRect();
    return { before: marker.right <= name.left, centerDistance: Math.abs((marker.top + marker.height / 2) - (name.top + name.height / 2)) };
  });
  expect(leftGeometry.before).toBeTruthy();
  expect(leftGeometry.centerDistance).toBeLessThan(1);

  await emit(page, { ...base, SequenceNumber: 2, Presentation: { ...base.Presentation, BossListAppearance: { ...baseAppearance, Alignment: "Right" } } });
  await expect(page.getByTestId("boss-list")).toHaveClass(/overlay-boss-list-right/);
  const rightMarkerAfterName = await page.getByTestId("boss-entry").evaluate((entry: Element) => {
    const marker = entry.querySelector(".overlay-defeated-skull")!;
    const name = entry.querySelector(".overlay-boss-name")!;
    return name.compareDocumentPosition(marker) & Node.DOCUMENT_POSITION_FOLLOWING;
  });
  expect(rightMarkerAfterName).toBeTruthy();
  const rightGeometry = await page.getByTestId("boss-entry").evaluate((entry: Element) => {
    const marker = entry.querySelector(".overlay-defeated-skull")!.getBoundingClientRect();
    const name = entry.querySelector(".overlay-boss-name")!.getBoundingClientRect();
    return { after: marker.left >= name.right, centerDistance: Math.abs((marker.top + marker.height / 2) - (name.top + name.height / 2)) };
  });
  expect(rightGeometry.after).toBeTruthy();
  expect(rightGeometry.centerDistance).toBeLessThan(1);

  await openOverlay(page, "/overlay/boss_list?styleVersion=1&alignment=Center&marker=Skull");
  await emit(page, { ...base, SequenceNumber: 3, Presentation: { ...base.Presentation, BossListAppearance: { ...baseAppearance, Alignment: "Center" } } });
  await expect(page.getByTestId("boss-list")).toHaveClass(/overlay-boss-list-center/);
  await expect(page.getByTestId("boss-entry")).toHaveCSS("justify-content", "center");
  await expect(page.locator(".overlay-defeated-skull")).toHaveCount(0);
  await expect(page.locator(".overlay-checkmark")).toHaveCount(0);

  await openOverlay(page, "/overlay/boss_list");
  const checkmarkPresentation = { ...base.Presentation, BossListShowDefeatedSkull: false, BossListShowCheckmark: true };
  await emit(page, { ...base, SequenceNumber: 4, Presentation: { ...checkmarkPresentation, BossListAppearance: { ...baseAppearance, Alignment: "Left" } } });
  await expect(page.locator(".overlay-checkmark")).toHaveCSS("font-size", "45px");

  await emit(page, { ...base, SequenceNumber: 5, Presentation: { ...checkmarkPresentation, BossListAppearance: { ...baseAppearance, Alignment: "Right" } } });
  await expect(page.getByTestId("boss-entry")).toHaveCSS("justify-content", "flex-end");

  await openOverlay(page, "/overlay/boss_list?styleVersion=1&alignment=Center&marker=Checkmark");
  await emit(page, { ...base, SequenceNumber: 6, Presentation: { ...checkmarkPresentation, BossListAppearance: { ...baseAppearance, Alignment: "Center" } } });
  await expect(page.getByTestId("boss-entry")).toHaveCSS("justify-content", "center");
  await expect(page.locator(".overlay-checkmark")).toHaveCount(0);
});

test("invalid query values safely retain the saved overlay values", async ({ page }) => {
  await openOverlay(page, "/overlay/total_deaths?styleVersion=1&title=%3Cscript%3E&font=Arial%3Bcolor%3Ared&size=999&textColor=red&backgroundOpacity=999&inline=bogus");
  await emit(page, snapshot(1, { selectedGame: "Bloodborne", totalDeaths: 8 }));
  await expect(page.getByRole("heading")).toHaveText("<script>: 8");
  await expect(page.getByTestId("total-deaths-overlay")).toHaveCSS("font-family", "\"Segoe UI\"");
});

test("outline and shadow scene settings compose safely without affecting boss marker images", async ({ page }) => {
  await openOverlay(page, "/overlay/boss_list?styleVersion=1&outline=true&outlineColor=%23112233&outlineWidth=2&shadow=true&shadowColor=%23445566&shadowX=-3&shadowY=4&shadowBlur=5");
  const appearance = { Title: "BOSS LIST", FontFamily: "Segoe UI", FontSize: 30, TextColor: "#FFFFFF", AccentColor: "#A78BFA", BackgroundColor: "#15171B", BackgroundOpacity: 88, Padding: 3, CornerRadius: 8, Alignment: "Left" };
  await emit(page, { SchemaVersion: 1, SequenceNumber: 1, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [{ DisplayName: "Defeated", DlcLabel: null, IsDefeated: true }], Presentation: { IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All", TotalDeathsCompactTitle: true, TotalDeathsAppearance: appearance, BossListAppearance: appearance, BossListDefeatedColor: "#8C8C96", BossListDefeatedTreatment: "Nothing", BossListShowCheckmark: false, BossListCheckmarkAccent: "#A78BFA", BossListMaximumVisibleCount: 25, BossListShowDefeatedSkull: true } });
  const panel = page.getByTestId("boss-list-overlay");
  await expect(panel).toHaveCSS("text-shadow", /rgb\(17, 34, 51\).*rgb\(68, 85, 102\)/);
  await expect(page.locator(".overlay-defeated-skull")).toHaveCSS("filter", /url\("#skull/);

  await openOverlay(page, "/overlay/total_deaths?styleVersion=1&outline=true&outlineColor=red&outlineWidth=9&shadow=true&shadowColor=red&shadowX=99&shadowY=-99&shadowBlur=99");
  await emit(page, { SchemaVersion: 1, SequenceNumber: 1, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [], Presentation: { IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All", TotalDeathsCompactTitle: true, TotalDeathsAppearance: { ...appearance, OutlineEnabled: true, OutlineColor: "#010203", OutlineWidth: 1, ShadowEnabled: false, ShadowColor: "#000000", ShadowOffsetX: 2, ShadowOffsetY: 2, ShadowBlur: 4 }, BossListAppearance: appearance, BossListDefeatedColor: "#8C8C96", BossListDefeatedTreatment: "Nothing", BossListShowCheckmark: false, BossListCheckmarkAccent: "#A78BFA", BossListMaximumVisibleCount: 25, BossListShowDefeatedSkull: false } });
  await expect(page.getByTestId("total-deaths-overlay")).toHaveCSS("text-shadow", /rgb\(1, 2, 3\)/);
});

test("icon color and text opacity affect content without fading the overlay panel", async ({ page }) => {
  await openOverlay(page, "/overlay/total_deaths");
  const appearance = { Title: "DEATHS", FontFamily: "Segoe UI", FontSize: 30, TextColor: "#FFFFFF", AccentColor: "#A78BFA", BackgroundColor: "#15171B", BackgroundOpacity: 88, Padding: 3, CornerRadius: 8, Alignment: "Left", TextOpacity: 45, IconColor: "#E880FF" };
  await emit(page, { SchemaVersion: 1, SequenceNumber: 1, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [], Presentation: { IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All", TotalDeathsCompactTitle: true, TotalDeathsTitleIconMode: "PrefixSkull", TotalDeathsAppearance: appearance, BossListAppearance: appearance, BossListDefeatedColor: "#8C8C96", BossListDefeatedTreatment: "Nothing", BossListShowCheckmark: false, BossListCheckmarkAccent: "#A78BFA", BossListMaximumVisibleCount: 25, BossListShowDefeatedSkull: false } });
  const panel = page.getByTestId("total-deaths-overlay");
  await expect(panel).toHaveCSS("opacity", "1");
  await expect(panel.getByRole("heading")).toHaveCSS("opacity", "0.45");
  await expect(page.locator(".overlay-title-skull")).toHaveCSS("filter", /E880FF/i);
});

test("unsafe font-family input is rejected before it can affect overlay CSS", async ({ page }) => {
  await openOverlay(page, "/overlay/total_deaths");
  const appearance = { Title: "DEATHS", FontFamily: "Arial; color: red", FontSize: 35, TextColor: "#102030", AccentColor: "#405060", BackgroundColor: "#708090", BackgroundOpacity: 60, Padding: 9, CornerRadius: 4, Alignment: "Right" };
  await emit(page, { SchemaVersion: 1, SequenceNumber: 1, SelectedGame: { DisplayName: "Sekiro" }, TotalDeaths: { Source: "GameLifetimeReader", Value: 9 }, Bosses: [], Presentation: { IsTotalDeathsEnabled: true, ShowGameName: false, IsBossListEnabled: true, BossListVisibilityMode: "All", TotalDeathsCompactTitle: false, TotalDeathsAppearance: appearance, BossListAppearance: { ...appearance, Title: "BOSSES" }, BossListDefeatedColor: "#808080", BossListDefeatedTreatment: "Strikethrough", BossListShowCheckmark: true, BossListCheckmarkAccent: "#A78BFA", BossListMaximumVisibleCount: 25 } });

  await expect(page.getByTestId("total-deaths-overlay")).toHaveCSS("font-family", "\"Segoe UI\"");
});

test("real loopback host loads the token-gated browser assets and applies its presentation projection", async ({ browser }) => {
  const host = await startRealOverlayHost();
  try {
    const page = await browser.newPage();
    await page.goto(host.totalDeathsUrl);
    await expect(page.getByTestId("total-deaths-overlay")).toBeVisible();
    await expect(page.getByRole("heading")).toContainText("6");
    await expect(page.getByTestId("game-name")).toHaveCount(0);

    await page.goto(host.bossListUrl);
    await expect(page.getByTestId("boss-entry")).toHaveCount(1);
    await expect(page.getByTestId("boss-entry")).toHaveClass("is-defeated");
  } finally {
    host.process.kill();
  }
});

async function openOverlay(page: Page, route: string): Promise<void> {
  await page.goto(`http://overlay.test${route}${route.includes("?") ? "&" : "?"}token=${token}`);
  await expect(page.getByTestId("connection-state")).toBeVisible();
}

async function emit(page: Page, value: unknown): Promise<void> {
  await page.evaluate((message) => {
    const sockets = (window as unknown as { __overlaySockets: Array<{ emit(value: unknown): void }> }).__overlaySockets;
    sockets.at(-1)?.emit(message);
  }, value);
}

function snapshot(
  sequence: number,
  options: {
    selectedGame?: string | null;
    totalDeaths?: number | null;
    source?: "Unavailable" | "ManualBloodborne";
    bosses?: Array<{ DisplayName: string; DlcLabel: string | null; IsDefeated: boolean }>;
    presentation?: { IsTotalDeathsEnabled: boolean; ShowGameName: boolean; IsBossListEnabled: boolean; BossListVisibilityMode: "All" | "Remaining" | "Defeated" };
  },
) {
  const selectedGame = options.selectedGame === undefined ? "Bloodborne" : options.selectedGame;
  const totalDeaths = options.totalDeaths === undefined ? 0 : options.totalDeaths;
  return {
    SchemaVersion: 1,
    SequenceNumber: sequence,
    SelectedGame: selectedGame === null ? null : { DisplayName: selectedGame },
    TotalDeaths: {
      Source: options.source ?? (totalDeaths === null ? "Unavailable" : "ManualBloodborne"),
      Value: totalDeaths,
    },
    Bosses: options.bosses ?? [],
    Presentation: options.presentation ?? { IsTotalDeathsEnabled: true, ShowGameName: true, IsBossListEnabled: true, BossListVisibilityMode: "All" },
  };
}

async function startRealOverlayHost(): Promise<{ process: OverlayHostProcess; totalDeathsUrl: string; bossListUrl: string }> {
  const process = childProcess.spawn(
    "dotnet",
    ["run", "--project", "../tests/SoulsTracker.Overlay.TestHost/SoulsTracker.Overlay.TestHost.csproj", "--configuration", "Release"],
    { cwd: nodeProcess.default.cwd(), stdio: ["ignore", "pipe", "pipe"] },
  );
  const ready = await new Promise<string>((resolve, reject) => {
    let output = "";
    let errors = "";
    const timeout = setTimeout(() => reject(new Error("The real overlay test host did not become ready.")), 30_000);
    process.stdout.on("data", (chunk: unknown) => {
      output += String(chunk);
      const line = output.split(/\r?\n/).find((value) => value.startsWith("READY "));
      if (line !== undefined) {
        clearTimeout(timeout);
        resolve(line);
      }
    });
    (process as unknown as { stderr: { on(event: "data", listener: (chunk: unknown) => void): void } }).stderr.on("data", (chunk: unknown) => {
      errors += String(chunk);
    });
    process.once("error", reject);
    process.once("exit", (code: number | null) => reject(new Error(`The real overlay test host stopped before readiness (${code}): ${output}${errors}`)));
  });
  const [, totalDeathsUrl, bossListUrl] = ready.split(" ");
  return { process, totalDeathsUrl, bossListUrl };
}
