type OverlayRoute = "total-deaths" | "boss-list";

type TotalDeathsSource = "Unavailable" | "ManualBloodborne" | "GameLifetimeReader";

interface OverlayGameMetadata {
  DisplayName: string;
}

interface TotalDeathsDisplayValue {
  Source: TotalDeathsSource;
  Value: number | null;
}

interface OverlayBossEntry {
  DisplayName: string;
  DlcLabel: string | null;
  IsDefeated: boolean;
}

type BossListVisibilityMode = "All" | "Remaining" | "Defeated";

interface OverlayPresentationConfiguration {
  IsTotalDeathsEnabled: boolean;
  ShowGameName: boolean;
  IsBossListEnabled: boolean;
  BossListVisibilityMode: BossListVisibilityMode;
  TotalDeathsCompactTitle: boolean;
  TotalDeathsTitleIconMode: "Off" | "PrefixSkull" | "SkullOnly";
  TotalDeathsAppearance: OverlayAppearance;
  BossListAppearance: OverlayAppearance;
  BossListDefeatedColor: string;
  BossListDefeatedTreatment: "Nothing" | "Dimmed" | "Strikethrough" | "Both";
  BossListShowCheckmark: boolean;
  BossListCheckmarkAccent: string;
  BossListMaximumVisibleCount: number;
  BossListShowDefeatedSkull: boolean;
  BossListCenterMarkerAlignment?: "Left" | "Right";
}
interface OverlayAppearance { Title: string; FontFamily: string; FontSize: number; TextColor: string; AccentColor: string; BackgroundColor: string; BackgroundOpacity: number; Padding: number; CornerRadius: number; Alignment: "Left" | "Center" | "Right"; OutlineEnabled: boolean; OutlineColor: string; OutlineWidth: number; ShadowEnabled: boolean; ShadowColor: string; ShadowOffsetX: number; ShadowOffsetY: number; ShadowBlur: number; TextOpacity?: number; IconColor?: string; }

interface OverlaySnapshot {
  SchemaVersion: number;
  SequenceNumber: number;
  SelectedGame: OverlayGameMetadata | null;
  TotalDeaths: TotalDeathsDisplayValue;
  Bosses: OverlayBossEntry[];
  Presentation: OverlayPresentationConfiguration;
}

class OverlayClient {
  private acceptedSequence = -1;
  private reconnectDelayMilliseconds = 250;
  private reconnectTimer: number | undefined;

  public constructor(
    private readonly target: HTMLElement,
    private readonly route: OverlayRoute,
  ) {}

  public start(): void {
    this.renderConnectionState("Connecting to SoulsTracker…");
    this.connect();
  }

  private connect(): void {
    let socket: WebSocket;
    try {
      socket = new WebSocket(webSocketUrl());
    } catch {
      this.scheduleReconnect();
      return;
    }

    socket.onopen = () => {
      this.reconnectDelayMilliseconds = 250;
    };
    socket.onmessage = (event) => this.acceptMessage(event.data);
    socket.onerror = () => socket.close();
    socket.onclose = () => {
      this.renderConnectionState("SoulsTracker connection unavailable");
      this.scheduleReconnect();
    };
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer !== undefined) {
      return;
    }

    const delay = this.reconnectDelayMilliseconds;
    this.reconnectDelayMilliseconds = Math.min(this.reconnectDelayMilliseconds * 2, 5_000);
    this.reconnectTimer = window.setTimeout(() => {
      this.reconnectTimer = undefined;
      this.connect();
    }, delay);
  }

  private acceptMessage(data: unknown): void {
    if (typeof data !== "string") {
      return;
    }

    let candidate: unknown;
    try {
      candidate = JSON.parse(data);
    } catch {
      return;
    }

    if (!isOverlaySnapshot(candidate) || candidate.SequenceNumber <= this.acceptedSequence) {
      return;
    }

    candidate.Presentation = applySceneStyle(normalizePresentation(candidate.Presentation), this.route);

    this.acceptedSequence = candidate.SequenceNumber;
    if (this.route === "total-deaths") {
      this.renderTotalDeaths(candidate);
    } else {
      this.renderBossList(candidate);
    }
  }

  private renderConnectionState(message: string): void {
    const state = document.createElement("p");
    state.className = "overlay-connection-state";
    state.dataset.testid = "connection-state";
    state.textContent = message;
    replaceContent(this.target, state);
  }

  private renderTotalDeaths(snapshot: OverlaySnapshot): void {
    if (!snapshot.Presentation.IsTotalDeathsEnabled) {
      replaceContent(this.target);
      return;
    }

    const panel = panelFor("total-deaths-overlay", snapshot.Presentation.TotalDeathsAppearance);
    const displayValue = snapshot.TotalDeaths.Source === "Unavailable" || snapshot.TotalDeaths.Value === null ? "Unavailable" : String(snapshot.TotalDeaths.Value);
    const inlineTitle = true;
    const titleIcon = snapshot.Presentation.TotalDeathsTitleIconMode;
    const hasTitle = snapshot.Presentation.TotalDeathsAppearance.Title.trim().length > 0;
    // A blank title intentionally means number-only, regardless of icon mode.
    // Skull-only is also intentionally separator-free: skull followed by count.
    if (hasTitle) {
      const titleText = inlineTitle
        ? titleIcon === "SkullOnly" ? displayValue : `${snapshot.Presentation.TotalDeathsAppearance.Title}: ${displayValue}`
        : snapshot.Presentation.TotalDeathsAppearance.Title;
      panel.append(heading(titleText, titleIcon, snapshot.Presentation.TotalDeathsAppearance.IconColor ?? snapshot.Presentation.TotalDeathsAppearance.TextColor, snapshot.Presentation.TotalDeathsAppearance));
    }

    const value = document.createElement("p");
    value.className = "overlay-total-deaths";
    value.dataset.testid = "total-deaths-value";
    value.textContent = displayValue;
    if (!inlineTitle || !hasTitle) panel.append(value);
    replaceContent(this.target, panel);
  }

  private renderBossList(snapshot: OverlaySnapshot): void {
    if (!snapshot.Presentation.IsBossListEnabled) {
      replaceContent(this.target);
      return;
    }

    const panel = panelFor("boss-list-overlay", snapshot.Presentation.BossListAppearance);
    if (snapshot.Presentation.BossListAppearance.Title.trim().length > 0) panel.append(heading(snapshot.Presentation.BossListAppearance.Title));

    if (snapshot.SelectedGame === null) {
      panel.append(message("Select a game in SoulsTracker to show its boss list."));
    } else if (visibleBosses(snapshot.Bosses, snapshot.Presentation.BossListVisibilityMode).length === 0) {
      panel.append(message("No bosses are available for this game."));
    } else {
      const list = document.createElement("ul");
      list.className = `overlay-boss-list overlay-boss-list-${snapshot.Presentation.BossListAppearance.Alignment.toLowerCase()}`;
      list.dataset.testid = "boss-list";
      for (const boss of visibleBosses(snapshot.Bosses, snapshot.Presentation.BossListVisibilityMode).slice(0, snapshot.Presentation.BossListMaximumVisibleCount)) {
        const item = document.createElement("li");
        item.className = boss.IsDefeated ? "is-defeated" : "is-remaining";
        item.dataset.defeatedTreatment = snapshot.Presentation.BossListDefeatedTreatment;
        item.style.setProperty("--defeated-color", snapshot.Presentation.BossListDefeatedColor);
        item.dataset.testid = "boss-entry";

        const copy = document.createElement("span");
        copy.className = "overlay-boss-copy";
        const row = document.createElement("span");
        row.className = "overlay-boss-row";
        const name = document.createElement("span");
        name.className = "overlay-boss-name";
        name.textContent = boss.DisplayName;
        // Treatments control color/decoration only.  Explicitly set the applied
        // effects on boss text so a treatment cannot accidentally sever inheritance.
        name.style.textShadow = textEffectsFor(snapshot.Presentation.BossListAppearance);
        const markersAllowed = snapshot.Presentation.BossListAppearance.Alignment !== "Center";
        const markerAfterName = snapshot.Presentation.BossListAppearance.Alignment === "Right" ||
          (snapshot.Presentation.BossListAppearance.Alignment === "Center" && snapshot.Presentation.BossListCenterMarkerAlignment === "Right");
        const appendMarker = () => { if (markersAllowed && boss.IsDefeated && snapshot.Presentation.BossListShowDefeatedSkull) {
          item.dataset.marker = "skull";
          // Markers are deliberately color-tinted only.  Text effects belong to
          // text; applying a silhouette filter to this raster would erase its
          // eye and nose details when an outline is disabled.
          const skull = skullMark("Defeated boss", snapshot.Presentation.BossListAppearance.IconColor ?? snapshot.Presentation.BossListCheckmarkAccent);
          skull.className = "overlay-defeated-skull";
          row.append(skull);
        } else if (markersAllowed && snapshot.Presentation.BossListShowCheckmark) {
          item.dataset.marker = "checkmark";
          const check = document.createElement("span"); check.className = "overlay-checkmark"; check.style.color = safeColor(snapshot.Presentation.BossListAppearance.IconColor ?? snapshot.Presentation.BossListCheckmarkAccent, snapshot.Presentation.BossListCheckmarkAccent); check.textContent = boss.IsDefeated ? "✓" : "○"; row.append(check);
        }};
        if (!markerAfterName) appendMarker();
        row.append(name);
        copy.append(row);
        item.append(copy);
        if (markerAfterName) appendMarker();

        if (boss.DlcLabel !== null && boss.DlcLabel.length > 0) {
          const dlc = document.createElement("span");
          dlc.className = "overlay-boss-dlc";
          dlc.textContent = boss.DlcLabel;
          copy.append(dlc);
        }

        list.append(item);
      }
      list.style.rowGap = `${snapshot.Presentation.BossListAppearance.Padding}px`;
      panel.append(list);
    }

    replaceContent(this.target, panel);
  }
}

function routeForPath(pathname: string): OverlayRoute {
  return pathname === "/overlay/boss_list" || pathname === "/overlay/boss-progress"
    ? "boss-list"
    : "total-deaths";
}

function webSocketUrl(): string {
  const url = new URL("/overlay/ws", window.location.href);
  url.protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
  url.search = window.location.search;
  return url.toString();
}

function panelFor(testId: string, appearance: OverlayAppearance): HTMLElement {
  const panel = document.createElement("section");
  panel.className = "souls-tracker-overlay-panel";
  panel.dataset.testid = testId;
  panel.style.fontFamily = safeFontFamily(appearance.FontFamily);
  panel.style.fontSize = `${appearance.FontSize}px`;
  panel.style.color = appearance.TextColor;
  panel.style.setProperty("--text-opacity", String((appearance.TextOpacity ?? 100) / 100));
  panel.style.backgroundColor = hexWithOpacity(appearance.BackgroundColor, appearance.BackgroundOpacity);
  panel.style.padding = `${appearance.Padding}px`;
  panel.style.borderRadius = `${appearance.CornerRadius}px`;
  panel.style.textAlign = appearance.Alignment.toLowerCase();
  panel.style.textShadow = textEffectsFor(appearance);
  // Text shadows do not paint bitmap pixels.  Supply a separately composed,
  // bounded drop-shadow filter only for the title skull so skull-only titles
  // remain legible without applying effects to boss marker assets.
  return panel;
}

function heading(text: string, iconMode: "Off" | "PrefixSkull" | "SkullOnly" = "Off", iconColor = "#FFFFFF", iconAppearance?: OverlayAppearance): HTMLHeadingElement {
  const title = document.createElement("h1");
  title.className = "overlay-heading";
  if (iconMode === "PrefixSkull") {
    const skull = skullMark("SoulsTracker skull", iconColor, iconAppearance); skull.className = "overlay-title-skull"; title.append(skull, document.createTextNode(` ${text}`));
  } else if (iconMode === "SkullOnly") {
    const skull = skullMark("SoulsTracker skull", iconColor, iconAppearance); skull.className = "overlay-title-skull"; title.append(skull, document.createTextNode(text));
  } else title.textContent = text;
  return title;
}

/** Uses the bundled raster directly so its black eye/nose detail survives tinting. */
function skullMark(label: string, color: string, appearance?: OverlayAppearance): HTMLImageElement {
  const mark = document.createElement("img");
  mark.alt = label;
  const asset = `/overlay/assets/souls-tracker-skull.png?token=${encodeURIComponent(new URLSearchParams(window.location.search).get("token") ?? "")}`;
  mark.src = asset;
  mark.style.filter = `url(#${ensureSkullFilter(safeColor(color, "#FFFFFF"), appearance)})`;
  return mark;
}

/**
 * Creates a small, bounded SVG filter for the bundled raster.  A color matrix
 * maps white skull pixels to the selected safe color while leaving black facial
 * pixels black.  The optional dilated alpha is composited *outside* SourceAlpha,
 * so an outline can never fill eye or nose holes.
 */
function ensureSkullFilter(color: string, appearance?: OverlayAppearance): string {
  const outline = appearance?.OutlineEnabled === true && appearance.OutlineWidth > 0 ? appearance.OutlineWidth : 0;
  const outlineColor = safeColor(appearance?.OutlineColor ?? "#000000", "#000000");
  const shadow = appearance?.ShadowEnabled === true;
  const shadowColor = safeColor(appearance?.ShadowColor ?? "#000000", "#000000");
  const shadowX = shadow ? appearance!.ShadowOffsetX : 0;
  const shadowY = shadow ? appearance!.ShadowOffsetY : 0;
  const shadowBlur = shadow ? appearance!.ShadowBlur : 0;
  const id = `skull-${color.slice(1)}-${outline}-${outlineColor.slice(1)}-${shadow ? 1 : 0}-${shadowColor.slice(1)}-${shadowX}-${shadowY}-${shadowBlur}`.replace(/-/g, "n");
  if (document.getElementById(id) !== null) return id;

  const namespace = "http://www.w3.org/2000/svg";
  let definitions = document.getElementById("souls-tracker-overlay-filters") as SVGSVGElement | null;
  if (definitions === null) {
    definitions = document.createElementNS(namespace, "svg");
    definitions.id = "souls-tracker-overlay-filters";
    definitions.setAttribute("aria-hidden", "true");
    definitions.setAttribute("width", "0");
    definitions.setAttribute("height", "0");
    definitions.style.position = "absolute";
    definitions.style.width = "0";
    definitions.style.height = "0";
    definitions.style.overflow = "hidden";
    document.body.append(definitions);
  }

  const filter = document.createElementNS(namespace, "filter");
  filter.id = id;
  filter.setAttribute("x", "-50%"); filter.setAttribute("y", "-50%");
  filter.setAttribute("width", "200%"); filter.setAttribute("height", "200%");
  filter.setAttribute("color-interpolation-filters", "sRGB");

  const [red, green, blue] = [color.slice(1, 3), color.slice(3, 5), color.slice(5, 7)].map((part) => Number.parseInt(part, 16) / 255);
  const tint = document.createElementNS(namespace, "feColorMatrix");
  tint.setAttribute("in", "SourceGraphic"); tint.setAttribute("result", "tinted");
  tint.setAttribute("type", "matrix");
  tint.setAttribute("values", `${red} 0 0 0 0  0 ${green} 0 0 0  0 0 ${blue} 0 0  0 0 0 1 0`);
  filter.append(tint);

  let outlineResult = "";
  if (outline > 0) {
    const expand = document.createElementNS(namespace, "feMorphology");
    expand.setAttribute("in", "SourceAlpha"); expand.setAttribute("operator", "dilate"); expand.setAttribute("radius", String(outline)); expand.setAttribute("result", "expanded");
    const flood = document.createElementNS(namespace, "feFlood"); flood.setAttribute("flood-color", outlineColor); flood.setAttribute("result", "outline-color");
    const paint = document.createElementNS(namespace, "feComposite"); paint.setAttribute("in", "outline-color"); paint.setAttribute("in2", "expanded"); paint.setAttribute("operator", "in"); paint.setAttribute("result", "expanded-outline");
    const outside = document.createElementNS(namespace, "feComposite"); outside.setAttribute("in", "expanded-outline"); outside.setAttribute("in2", "SourceAlpha"); outside.setAttribute("operator", "out"); outside.setAttribute("result", "outer-outline");
    filter.append(expand, flood, paint, outside);
    outlineResult = "outer-outline";
  }

  let shadowResult = "";
  if (shadow) {
    const dropShadow = document.createElementNS(namespace, "feDropShadow");
    dropShadow.setAttribute("in", "SourceAlpha"); dropShadow.setAttribute("dx", String(shadowX)); dropShadow.setAttribute("dy", String(shadowY)); dropShadow.setAttribute("stdDeviation", String(shadowBlur / 2)); dropShadow.setAttribute("flood-color", shadowColor); dropShadow.setAttribute("result", "shadow");
    filter.append(dropShadow);
    shadowResult = "shadow";
  }
  if (outlineResult || shadowResult) {
    const merge = document.createElementNS(namespace, "feMerge");
    for (const result of [shadowResult, outlineResult, "tinted"].filter(Boolean)) {
      const node = document.createElementNS(namespace, "feMergeNode"); node.setAttribute("in", result); merge.append(node);
    }
    filter.append(merge);
  }
  definitions.append(filter);
  return id;
}

function safeColor(value: string, fallback: string): string { return /^#[0-9a-f]{6}$/i.test(value) ? value : fallback; }

function message(text: string): HTMLParagraphElement {
  const paragraph = document.createElement("p");
  paragraph.className = "overlay-empty-state";
  paragraph.dataset.testid = "empty-state";
  paragraph.textContent = text;
  return paragraph;
}

function isOverlaySnapshot(value: unknown): value is OverlaySnapshot {
  if (!isRecord(value) || value.SchemaVersion !== 1 || !isNonNegativeInteger(value.SequenceNumber)) {
    return false;
  }

  if (!isRecord(value.TotalDeaths) || !isTotalDeathsSource(value.TotalDeaths.Source) ||
      !(typeof value.TotalDeaths.Value === "number" || value.TotalDeaths.Value === null) ||
      (typeof value.TotalDeaths.Value === "number" && (!Number.isSafeInteger(value.TotalDeaths.Value) || value.TotalDeaths.Value < 0))) {
    return false;
  }

  if (!(value.SelectedGame === null || (isRecord(value.SelectedGame) && typeof value.SelectedGame.DisplayName === "string")) ||
      !Array.isArray(value.Bosses) || !isPresentationConfiguration(value.Presentation)) {
    return false;
  }

  return value.Bosses.every((boss) => isRecord(boss) && typeof boss.DisplayName === "string" &&
    (typeof boss.DlcLabel === "string" || boss.DlcLabel === null) && typeof boss.IsDefeated === "boolean");
}

function visibleBosses(bosses: OverlayBossEntry[], visibilityMode: BossListVisibilityMode): OverlayBossEntry[] {
  switch (visibilityMode) {
    case "All": return bosses;
    case "Remaining": return bosses.filter((boss) => !boss.IsDefeated);
    case "Defeated": return bosses.filter((boss) => boss.IsDefeated);
  }
}

function isPresentationConfiguration(value: unknown): value is OverlayPresentationConfiguration {
  return isRecord(value) && typeof value.IsTotalDeathsEnabled === "boolean" &&
    typeof value.ShowGameName === "boolean" && typeof value.IsBossListEnabled === "boolean" &&
    (value.BossListVisibilityMode === "All" || value.BossListVisibilityMode === "Remaining" || value.BossListVisibilityMode === "Defeated");
}
function normalizePresentation(value: OverlayPresentationConfiguration): OverlayPresentationConfiguration {
  const candidate = value as unknown as Record<string, unknown>;
  const defaultAppearance: OverlayAppearance = { Title: "TOTAL DEATHS", FontFamily: "Segoe UI", FontSize: 42, TextColor: "#FFFFFF", AccentColor: "#A78BFA", BackgroundColor: "#15171B", BackgroundOpacity: 88, Padding: 16, CornerRadius: 8, Alignment: "Left", OutlineEnabled: false, OutlineColor: "#000000", OutlineWidth: 0, ShadowEnabled: false, ShadowColor: "#000000", ShadowOffsetX: 2, ShadowOffsetY: 2, ShadowBlur: 4, TextOpacity: 100, IconColor: "#FFFFFF" };
  return { ...value,
    TotalDeathsCompactTitle: true,
    TotalDeathsTitleIconMode: candidate.TotalDeathsTitleIconMode === "PrefixSkull" || candidate.TotalDeathsTitleIconMode === "SkullOnly" ? candidate.TotalDeathsTitleIconMode : "Off",
    TotalDeathsAppearance: isAppearance(candidate.TotalDeathsAppearance) ? { ...normalizeAppearance(candidate.TotalDeathsAppearance, defaultAppearance), Alignment: "Left" } : defaultAppearance,
    BossListAppearance: isAppearance(candidate.BossListAppearance) ? normalizeAppearance(candidate.BossListAppearance, { ...defaultAppearance, Title: "BOSS LIST" }) : { ...defaultAppearance, Title: "BOSS LIST" },
    BossListDefeatedColor: typeof candidate.BossListDefeatedColor === "string" ? candidate.BossListDefeatedColor : "#8C8C96",
    BossListDefeatedTreatment: candidate.BossListDefeatedTreatment === "Nothing" || candidate.BossListDefeatedTreatment === "Dimmed" || candidate.BossListDefeatedTreatment === "Strikethrough" || candidate.BossListDefeatedTreatment === "Both" ? candidate.BossListDefeatedTreatment : "Nothing",
    BossListShowCheckmark: candidate.BossListShowCheckmark !== false,
    BossListCheckmarkAccent: typeof candidate.BossListCheckmarkAccent === "string" ? candidate.BossListCheckmarkAccent : "#A78BFA",
    BossListMaximumVisibleCount: isNonNegativeInteger(candidate.BossListMaximumVisibleCount) && candidate.BossListMaximumVisibleCount > 0 ? candidate.BossListMaximumVisibleCount : 25,
    BossListShowDefeatedSkull: candidate.BossListShowDefeatedSkull === true,
    BossListCenterMarkerAlignment: candidate.BossListCenterMarkerAlignment === "Right" ? "Right" : "Left",
  };
}
/** Valid URL query values override saved defaults only in that browser source. */
function applySceneStyle(presentation: OverlayPresentationConfiguration, route: OverlayRoute): OverlayPresentationConfiguration {
  const query = new URLSearchParams(window.location.search);
  if (query.get("styleVersion") === "1") return applyStyleFields(presentation, route, query);

  // Retain existing OBS fragment URLs as a migration path; generated URLs use query fields.
  const prefix = "#style=v1.";
  if (!window.location.hash.startsWith(prefix)) return presentation;
  try {
    const encoded = window.location.hash.slice(prefix.length).replace(/-/g, "+").replace(/_/g, "/");
    const text = atob(encoded + "=".repeat((4 - encoded.length % 4) % 4));
    if (text.length > 1024) return presentation;
    const fields = new URLSearchParams();
    for (const part of text.split(";")) { const index = part.indexOf("="); if (index > 0) fields.set(part.slice(0, index), decodeURIComponent(part.slice(index + 1))); }
    fields.set("styleVersion", "1");
    if (fields.has("text")) fields.set("textColor", fields.get("text")!);
    if (fields.has("background")) fields.set("backgroundColor", fields.get("background")!);
    if (fields.has("opacity")) fields.set("backgroundOpacity", fields.get("opacity")!);
    if (fields.has("game")) fields.set("showGameName", fields.get("game") === "1" ? "true" : "false");
    if (fields.has("inline")) fields.set("inline", fields.get("inline") === "1" ? "true" : "false");
    return applyStyleFields(presentation, route, fields);
  } catch { return presentation; }
}
function applyStyleFields(presentation: OverlayPresentationConfiguration, route: OverlayRoute, fields: URLSearchParams): OverlayPresentationConfiguration {
  const raw = route === "total-deaths" ? presentation.TotalDeathsAppearance : presentation.BossListAppearance;
  const bounded = (name: string, fallback: number, min: number, max: number): number => { const value = Number(fields.get(name)); return Number.isInteger(value) && value >= min && value <= max ? value : fallback; };
  const color = (name: string, fallback: string): string => { const value = fields.get(name); return value !== null && /^#[0-9a-fA-F]{6}$/.test(value) ? value : fallback; };
  const boolean = (name: string, fallback: boolean): boolean => fields.get(name) === "true" ? true : fields.get(name) === "false" ? false : fallback;
  const title = fields.get("title"); const font = fields.get("font");
  const alignment = fields.get("alignment");
  const appearance: OverlayAppearance = { ...raw,
    Title: title !== null && title.length <= 40 ? title.trim() : raw.Title,
    FontFamily: font !== null && isSafeFontFamily(font) ? font : raw.FontFamily,
    FontSize: bounded("size", raw.FontSize, 12, 96), TextColor: color("textColor", raw.TextColor),
    BackgroundColor: color("backgroundColor", raw.BackgroundColor), BackgroundOpacity: bounded("backgroundOpacity", raw.BackgroundOpacity, 0, 100),
    Padding: route === "boss-list" ? bounded("bossRowSpacing", raw.Padding, 0, 48) : raw.Padding,
    Alignment: route === "boss-list" && (alignment === "Left" || alignment === "Center" || alignment === "Right") ? alignment : raw.Alignment,
    OutlineEnabled: boolean("outline", raw.OutlineEnabled), OutlineColor: color("outlineColor", raw.OutlineColor), OutlineWidth: bounded("outlineWidth", raw.OutlineWidth, 0, 8),
    ShadowEnabled: boolean("shadow", raw.ShadowEnabled), ShadowColor: color("shadowColor", raw.ShadowColor), ShadowOffsetX: bounded("shadowX", raw.ShadowOffsetX, -20, 20), ShadowOffsetY: bounded("shadowY", raw.ShadowOffsetY, -20, 20), ShadowBlur: bounded("shadowBlur", raw.ShadowBlur, 0, 20), TextOpacity: bounded("textOpacity", raw.TextOpacity ?? 100, 0, 100), IconColor: color("iconColor", raw.IconColor ?? raw.TextColor),
  };
  if (route === "total-deaths") return { ...presentation, TotalDeathsAppearance: appearance,
    TotalDeathsCompactTitle: true, ShowGameName: false,
    TotalDeathsTitleIconMode: fields.get("titleIcon") === "PrefixSkull" || fields.get("titleIcon") === "SkullOnly" ? fields.get("titleIcon")! as "PrefixSkull" | "SkullOnly" : fields.get("titleIcon") === "Off" ? "Off" : presentation.TotalDeathsTitleIconMode };
  const marker = fields.get("marker"); const treatment = fields.get("treatment"); const mode = fields.get("mode");
  const centered = appearance.Alignment === "Center";
  return { ...presentation, BossListAppearance: appearance,
    BossListVisibilityMode: mode === "All" || mode === "Remaining" || mode === "Defeated" ? mode : presentation.BossListVisibilityMode,
    BossListDefeatedColor: color("defeatedColor", presentation.BossListDefeatedColor),
    BossListDefeatedTreatment: treatment === "Nothing" || treatment === "Dimmed" || treatment === "Strikethrough" || treatment === "Both" ? treatment : presentation.BossListDefeatedTreatment,
    BossListMaximumVisibleCount: bounded("maximumVisible", presentation.BossListMaximumVisibleCount, 1, 100),
    // A centered list has no markers by product decision, including when a
    // streamer edits an older scene URL by hand.
    BossListShowCheckmark: !centered && (marker === "Checkmark" ? true : marker === "Skull" || marker === "None" ? false : presentation.BossListShowCheckmark),
    BossListShowDefeatedSkull: !centered && (marker === "Skull" ? true : marker === "Checkmark" || marker === "None" ? false : presentation.BossListShowDefeatedSkull),
    BossListCenterMarkerAlignment: fields.get("centerMarkerAlignment") === "Right" ? "Right" : fields.get("centerMarkerAlignment") === "Left" ? "Left" : presentation.BossListCenterMarkerAlignment };
}
function isAppearance(value: unknown): value is OverlayAppearance { return isRecord(value) && typeof value.Title === "string" && typeof value.FontFamily === "string" && isSafeFontFamily(value.FontFamily) && isNonNegativeInteger(value.FontSize) && typeof value.TextColor === "string" && typeof value.AccentColor === "string" && typeof value.BackgroundColor === "string" && isNonNegativeInteger(value.BackgroundOpacity) && isNonNegativeInteger(value.Padding) && isNonNegativeInteger(value.CornerRadius) && (value.Alignment === "Left" || value.Alignment === "Center" || value.Alignment === "Right"); }
function normalizeAppearance(value: OverlayAppearance, fallback: OverlayAppearance): OverlayAppearance {
  const candidate = value as unknown as Record<string, unknown>;
  const bounded = (name: string, fallbackValue: number, min: number, max: number): number => isNonNegativeInteger(candidate[name]) && Number(candidate[name]) >= min && Number(candidate[name]) <= max ? Number(candidate[name]) : fallbackValue;
  const offset = (name: string, fallbackValue: number): number => typeof candidate[name] === "number" && Number.isInteger(candidate[name]) && candidate[name] >= -20 && candidate[name] <= 20 ? candidate[name] : fallbackValue;
  const color = (name: string, fallbackValue: string): string => typeof candidate[name] === "string" && /^#[0-9a-fA-F]{6}$/.test(candidate[name]) ? candidate[name] : fallbackValue;
  return { ...fallback, ...value, OutlineEnabled: candidate.OutlineEnabled === true, OutlineColor: color("OutlineColor", fallback.OutlineColor), OutlineWidth: bounded("OutlineWidth", fallback.OutlineWidth, 0, 8), ShadowEnabled: candidate.ShadowEnabled === true, ShadowColor: color("ShadowColor", fallback.ShadowColor), ShadowOffsetX: offset("ShadowOffsetX", fallback.ShadowOffsetX), ShadowOffsetY: offset("ShadowOffsetY", fallback.ShadowOffsetY), ShadowBlur: bounded("ShadowBlur", fallback.ShadowBlur, 0, 20), TextOpacity: bounded("TextOpacity", fallback.TextOpacity ?? 100, 0, 100), IconColor: color("IconColor", fallback.IconColor ?? fallback.TextColor) };
}
function textEffectsFor(appearance: OverlayAppearance): string {
  const effects: string[] = [];
  if (appearance.OutlineEnabled && appearance.OutlineWidth > 0) {
    const width = appearance.OutlineWidth;
    for (const [x, y] of [[-width, -width], [0, -width], [width, -width], [-width, 0], [width, 0], [-width, width], [0, width], [width, width]]) effects.push(`${x}px ${y}px 0 ${appearance.OutlineColor}`);
  }
  if (appearance.ShadowEnabled) effects.push(`${appearance.ShadowOffsetX}px ${appearance.ShadowOffsetY}px ${appearance.ShadowBlur}px ${appearance.ShadowColor}`);
  return effects.join(", ");
}
function titleIconEffectsFor(appearance: OverlayAppearance): string {
  const effects: string[] = [];
  if (appearance.OutlineEnabled && appearance.OutlineWidth > 0) {
    const width = appearance.OutlineWidth;
    for (const [x, y] of [[-width, -width], [0, -width], [width, -width], [-width, 0], [width, 0], [-width, width], [0, width], [width, width]]) effects.push(`drop-shadow(${x}px ${y}px 0 ${appearance.OutlineColor})`);
  }
  if (appearance.ShadowEnabled) effects.push(`drop-shadow(${appearance.ShadowOffsetX}px ${appearance.ShadowOffsetY}px ${appearance.ShadowBlur}px ${appearance.ShadowColor})`);
  return effects.length === 0 ? "none" : effects.join(" ");
}
function isSafeFontFamily(value: string): boolean { return value.length > 0 && value.length <= 128 && !/[;{}<>"'\\]/.test(value); }
function safeFontFamily(value: string): string { return isSafeFontFamily(value) ? value : "Segoe UI"; }
function hexWithOpacity(hex: string, opacity: number): string { const r = Number.parseInt(hex.slice(1, 3), 16); const g = Number.parseInt(hex.slice(3, 5), 16); const b = Number.parseInt(hex.slice(5, 7), 16); return `rgb(${r} ${g} ${b} / ${opacity}%)`; }

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

/** Uses only DOM APIs supported by the WPF WebBrowser engine as well as OBS Chromium. */
function replaceContent(target: HTMLElement, ...children: Node[]): void {
  while (target.firstChild !== null) target.removeChild(target.firstChild);
  for (const child of children) target.appendChild(child);
}

function isNonNegativeInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

function isTotalDeathsSource(value: unknown): value is TotalDeathsSource {
  return value === "Unavailable" || value === "ManualBloodborne" || value === "GameLifetimeReader";
}

const root = document.getElementById("souls-tracker-overlay");

if (root !== null) {
  new OverlayClient(root, routeForPath(window.location.pathname)).start();
}
