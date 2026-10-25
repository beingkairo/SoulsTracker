import { beforeEach, inject } from "vitest";

// Keep diagnostics outside application-console assertions in later tests, including late work.
const output = console.log.bind(console);

declare module "vitest" {
  export interface ProvidedContext { workerDiagnostics: boolean; }
}

type Scenario = `authority-${"publisher" | "session" | "state" | "credentials"}` |
  "live-hydration" | "live-broadcast" | "malformed-create" | "create-replay" | "v2-auth";
type Phase = "callback" | "reset" | "seed" | "seed-sql" | "other-seed" | "other-seed-sql" |
  "acquire" | "publish" | "stored" | "request" | "create" | "body" | "replay" | "restart" |
  "malformed" | "upgrade" | "snapshot" | "authenticate" | "update" | "barrier" | "socket" | "closed" | "fixture-sql";
type Event = "start" | "end" | "rejected" | "aborted" | "cleanup-enter" | "write" | "observed" | "message" | "close" | "error" | "observer-unavailable";

class Trace {
  private scenario?: Scenario;
  private events: object[] = [];
  private dropped = 0;
  private count = 0;
  private resetDone = false;
  private flushed = false;
  private aborted = false;
  private readonly started = performance.now();
  constructor(private file: string, private ordinal: number, private enabled: boolean, signal: AbortSignal, private timeoutMs?: number) {
    // Only plain data crosses into runInDurableObject callbacks. Signals own an I/O context.
    this.aborted = signal.aborted;
    if (enabled) signal.addEventListener("abort", () => {
      this.aborted = true;
      this.mark("callback", "aborted"); this.flush();
    }, { once: true });
  }
  select(scenario: Scenario): this {
    this.scenario = scenario;
    this.mark("callback", "start");
    return this;
  }
  mark(phase: Phase, event: Event, code?: number): void {
    try {
      if (!this.enabled || !this.scenario) return;
      if (this.count++ >= 96) { this.dropped++; return; }
      this.events.push({ phase, event, ms: performance.now() - this.started, utcMs: Date.now(),
        aborted: this.aborted, afterReset: this.resetDone, ...(code === undefined ? {} : { code }) });
      if (this.flushed) this.flush();
    } catch { /* Observation must not interrupt the operation being measured. */ }
  }
  phase<T>(label: Phase, operation: () => Promise<T>): Promise<T> {
    if (!this.enabled || !this.scenario) return operation();
    this.mark(label, "start");
    try {
      return operation().then(value => { this.mark(label, "end"); return value; },
        error => { this.mark(label, "rejected"); throw error; });
    } catch (error) { this.mark(label, "rejected"); throw error; }
  }
  watch(socket: WebSocket): void {
    if (!this.enabled || !this.scenario) return;
    try {
      this.mark("socket", "observed", socket.readyState);
      const message = () => this.mark("socket", "message");
      const error = () => this.mark("socket", "error");
      socket.addEventListener("message", message);
      socket.addEventListener("error", error);
      socket.addEventListener("close", event => {
        this.mark("socket", "close", event.code);
        try {
          socket.removeEventListener("message", message);
          socket.removeEventListener("error", error);
        } catch { /* Best effort only. */ }
      }, { once: true });
    } catch { this.mark("socket", "observer-unavailable"); }
  }
  cleanup(reset: () => Promise<void>): Promise<void> {
    if (!this.enabled || !this.scenario) return reset();
    // A timed-out body may still be running; this marks the hook, not body completion.
    this.mark("callback", "cleanup-enter");
    // Observe the original reset promise without changing its result or deadline.
    this.mark("reset", "start");
    return reset().then(() => {
      this.resetDone = true;
      this.mark("reset", "end");
      this.flush();
    }, error => {
      this.mark("reset", "rejected"); this.flush(); throw error;
    });
  }
  private flush(): void {
    if (!this.events.length) return;
    try {
      output("WORKER_PHASE " + JSON.stringify({ file: this.file, ordinal: this.ordinal,
        scenario: this.scenario, timeoutMs: this.timeoutMs, events: this.events, dropped: this.dropped }));
    } catch { /* Diagnostics must not replace the test's result. */ }
    this.events = [];
    this.flushed = true;
  }
}

export function workerDiagnostics(file: "authority" | "live" | "provisioning") {
  const enabled = inject("workerDiagnostics");
  let current: Trace;
  let ordinal = 0;
  if (enabled) {
    try { console.log("WORKER_PHASE " + JSON.stringify({ file, event: "file-loaded", utcMs: Date.now() })); }
    catch { /* Best effort only. */ }
  }
  beforeEach(({ signal, task }) => { current = new Trace(file, ++ordinal, enabled, signal, task.timeout); });
  return {
    capture: (scenario?: Scenario) => scenario ? current.select(scenario) : current,
    cleanup: (reset: () => Promise<void>) => current.cleanup(reset)
  };
}
