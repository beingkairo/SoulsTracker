import { parseHostedOverlay, type HostedAppearance, type HostedDeath } from "./hosted-contracts.js";

export interface HostedLocation { id: string; read: string; origin: string }
export function parseHostedLocation(address: string): HostedLocation | null {
  try {
    const url = new URL(address);
    const match = /^#id=([0-9a-f]{32})&read=([0-9a-f]{64})$/.exec(url.hash);
    if (url.protocol !== "https:" || url.search || url.username || url.password || !match) return null;
    return { id: match[1], read: match[2], origin: url.origin };
  } catch { return null; }
}
interface Socket {
  onopen: ((event: Event) => void) | null;
  onmessage: ((event: MessageEvent) => void) | null;
  onclose: ((event: CloseEvent) => void) | null;
  onerror: ((event: Event) => void) | null;
  send(value: string): void;
  close(): void;
}
interface Runtime {
  socket(url: string): Socket;
  now(): number;
  random(): number;
  setTimeout(callback: () => void, delay: number): number;
  clearTimeout(id: number): void;
}
const browserRuntime: Runtime = {
  socket: url => new WebSocket(url), now: () => Date.now(), random: () => Math.random(),
  setTimeout: (callback, delay) => window.setTimeout(callback, delay), clearTimeout: id => window.clearTimeout(id)
};

export class HostedClient {
  private socket?: Socket;
  private timer?: number;
  private generation = 0;
  private running = false;
  private revoked = false;
  private delay = 1000;
  private death: HostedDeath | null = null;
  private appearance?: HostedAppearance;

  constructor(private readonly location: HostedLocation,
    private readonly render: (death: HostedDeath | null, appearance: HostedAppearance) => void,
    private readonly runtime: Runtime = browserRuntime) {}

  start(): void {
    if (this.running || this.revoked) return;
    this.running = true;
    this.connect();
  }
  stop(): void {
    this.running = false;
    this.generation++;
    this.cancelTimer();
    this.socket?.close();
    this.socket = undefined;
  }
  private cancelTimer(): void {
    if (this.timer !== undefined) this.runtime.clearTimeout(this.timer);
    this.timer = undefined;
  }
  private connect(): void {
    const generation = ++this.generation;
    let socket: Socket;
    let hydrated = false;
    let lastResponse = this.runtime.now();
    let opened = false;
    const current = () => this.running && generation === this.generation;
    const disconnect = (code = 1006) => {
      if (!current()) return;
      this.generation++;
      this.cancelTimer();
      socket?.close();
      this.socket = undefined;
      if (code === 4401) { this.running = false; this.revoked = true; return; }
      const wait = this.runtime.random() * this.delay;
      this.delay = Math.min(this.delay * 2, 30000);
      this.timer = this.runtime.setTimeout(() => { this.timer = undefined; if (this.running) this.connect(); }, wait);
    };
    const heartbeat = () => {
      if (!current()) return;
      if (this.runtime.now() - lastResponse >= 90000) { disconnect(); return; }
      try { if (opened) socket.send("ping"); } catch { disconnect(); return; }
      this.timer = this.runtime.setTimeout(heartbeat, 30000);
    };
    try {
      socket = this.runtime.socket(`${this.location.origin.replace(/^https:/, "wss:")}/api/v1/overlays/${this.location.id}/live`);
      this.socket = socket;
      socket.onopen = () => {
        if (!current()) return;
        opened = true;
        try { socket.send(JSON.stringify({ v: 1, type: "auth", readCapability: this.location.read })); }
        catch { disconnect(); }
      };
      socket.onmessage = event => {
        if (!current()) return;
        try {
          if (event.data === "pong") { if (hydrated) lastResponse = this.runtime.now(); return; }
          if (typeof event.data !== "string") throw new Error();
          const envelope = parseHostedOverlay(event.data);
          if (!hydrated && envelope.type !== "snapshot") throw new Error();
          let changed = !this.appearance;
          if (envelope.death && (!this.death || BigInt(envelope.death.revision) > BigInt(this.death.revision))) {
            this.death = envelope.death; changed = true;
          }
          if (envelope.appearance && (!this.appearance || BigInt(envelope.appearance.revision) > BigInt(this.appearance.revision))) {
            this.appearance = envelope.appearance; changed = true;
          }
          if (!hydrated) { hydrated = true; this.delay = 1000; }
          lastResponse = this.runtime.now();
          if (changed) this.render(this.death, this.appearance!);
        } catch { disconnect(4400); }
      };
      socket.onerror = () => disconnect();
      socket.onclose = event => disconnect(event.code);
      this.timer = this.runtime.setTimeout(heartbeat, 30000);
    } catch { disconnect(); }
  }
}
