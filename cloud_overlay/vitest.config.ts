import { cloudflareTest } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";
import { monitorEventLoopDelay } from "node:perf_hooks";
import { availableParallelism, freemem } from "node:os";

export default defineConfig({
  plugins: [workerNodeDiagnostics(), cloudflareTest({
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
  test: { include: ["test/**/*.test.ts"], provide: { workerDiagnostics: process.env.WORKER_TEST_DIAGNOSTICS === "1" } }
});

function workerNodeDiagnostics() {
  let stop = () => {};
  return {
    name: "worker-test-node-diagnostics",
    configResolved() {
      if (process.env.WORKER_TEST_DIAGNOSTICS !== "1") return;
      try {
        const delay = monitorEventLoopDelay({ resolution: 20 });
        delay.enable();
        let cpu = process.cpuUsage(), time = performance.now(), samples = 0, stopped = false;
        const emit = (event: string) => {
          try {
            const now = performance.now(), elapsed = now - time;
            const usage = process.cpuUsage(cpu);
            cpu = process.cpuUsage(); time = now;
            console.log("WORKER_HOST " + JSON.stringify({ event, utcMs: Date.now(), elapsedMs: elapsed,
              logicalCpus: availableParallelism(), cpuPercentOfOneCore: elapsed > 0 ? (usage.user + usage.system) / (elapsed * 10) : 0,
              rssMiB: process.memoryUsage().rss / 1048576, freeMiB: freemem() / 1048576,
              eventLoopMaxMs: delay.max / 1e6, eventLoopP99Ms: delay.percentile(99) / 1e6 }));
            delay.reset();
          } catch { /* Sampling must not fail tests. */ }
        };
        emit("config-ready");
        const timer = setInterval(() => {
          emit("sample");
          if (++samples >= 180) stop();
        }, 2000);
        timer.unref();
        stop = () => {
          if (stopped) return;
          stopped = true; clearInterval(timer); emit("stopped"); delay.disable();
        };
      } catch { /* Unsupported telemetry must not prevent test startup. */ }
    },
    closeBundle() { try { stop(); } catch { /* Best effort only. */ } }
  };
}
