import { test, expect } from "@playwright/test";
import { readFileSync } from "node:fs";
import { HostedClient, parseHostedLocation } from "../src/hosted-client.js";
const corpus = JSON.parse(readFileSync(new URL("../../tests/fixtures/hosted-overlay/contracts.json", import.meta.url), "utf8"));

class Socket {
  onopen: (() => void) | null = null;
  onmessage: ((event: { data: unknown }) => void) | null = null;
  onclose: ((event: { code: number }) => void) | null = null;
  onerror: (() => void) | null = null;
  sent: string[] = [];
  closed = false;
  send(value: string) { this.sent.push(value); }
  close() { this.closed = true; }
  message(value: unknown) { this.onmessage?.({ data: typeof value === "string" ? value : JSON.stringify(value) }); }
}
function harness(random = 0.5) {
  let now = 0, next = 0;
  const timers = new Map<number, { due: number; callback: () => void }>();
  const sockets: Socket[] = [], rendered: unknown[] = [], urls: string[] = [];
  const client = new HostedClient({ id: "1".repeat(32), read: "2".repeat(64), origin: "https://overlay.test" },
    (death, appearance) => { rendered.push({ death, appearance }); }, {
      socket: url => { urls.push(url); const socket = new Socket(); sockets.push(socket); return socket; },
      now: () => now, random: () => random,
      setTimeout: (callback, delay) => { timers.set(++next, { due: now + delay, callback }); return next; },
      clearTimeout: id => { timers.delete(id); }
    });
  const advance = (milliseconds: number) => {
    const end = now + milliseconds;
    for (;;) {
      const entry = [...timers].sort((a, b) => a[1].due - b[1].due)[0];
      if (!entry || entry[1].due > end) break;
      now = entry[1].due; timers.delete(entry[0]); entry[1].callback();
    }
    now = end;
  };
  client.start();
  return { client, sockets, rendered, urls, timers, advance };
}

test("strict fragment-only identity rejects duplicates, styles, queries and malformed encodings", () => {
  const base = `https://overlay.test/overlay/#id=${"1".repeat(32)}&read=${"2".repeat(64)}`;
  expect(parseHostedLocation(base)?.id).toBe("1".repeat(32));
  for (const url of [base + "&read=x", base + "&title=x", base.replace("/overlay/#", "/overlay/?id=x#"),
    base.replace("https:", "http:"), base.replace("#id=", "#%69d="), base.replace("&read=", "&write="), base + "&", base.replace("#id=", "#id=%")])
    expect(parseHostedLocation(url)).toBeNull();
});
test("authenticates only in the first message and applies combined snapshots atomically without precision loss", () => {
  const h = harness(); const socket = h.sockets[0];
  expect(h.rendered).toEqual([]); socket.onopen?.();
  expect(h.urls).toEqual([`wss://overlay.test/api/v1/overlays/${"1".repeat(32)}/live`]);
  expect(JSON.parse(socket.sent[0])).toEqual({ v: 1, type: "auth", readCapability: "2".repeat(64) });
  socket.message({ ...corpus.valid[4], death: { revision: "9007199254740993", value: "9223372036854775807", availability: "available" } });
  expect(h.rendered).toHaveLength(1);
  expect(h.rendered[0]).toMatchObject({ death: { value: "9223372036854775807" } });
  h.client.stop(); expect(h.timers.size).toBe(0);
});

test("ignores equal and older revisions independently and renders combined newer channels once", () => {
  const h = harness(), socket = h.sockets[0];
  const initial = { ...corpus.valid[4], death: { revision: "9007199254740993", value: "2", availability: "available" } };
  socket.message(initial);
  socket.message({ v: 1, type: "update", death: { ...initial.death, revision: "9007199254740992", value: "9" } });
  socket.message({ v: 1, type: "update", death: { ...initial.death, value: "8" }, appearance: initial.appearance });
  expect(h.rendered).toHaveLength(1);
  socket.message({ v: 1, type: "update", death: { ...initial.death, revision: "9007199254740994", value: "0" },
    appearance: { ...initial.appearance, revision: "1", title: "Changed" } });
  expect(h.rendered).toHaveLength(2);
  expect(h.rendered[1]).toMatchObject({ death: { value: "0" }, appearance: { title: "Changed" } });
  h.client.stop();
});

test("retains last state on malformed packets and ignores callbacks from obsolete sockets", () => {
  const h = harness(), old = h.sockets[0]; old.onopen?.(); old.message(corpus.valid[4]);
  old.message('{"v":1,"v":1}'); expect(old.closed).toBe(true);
  expect(h.rendered).toHaveLength(1); expect(h.timers.size).toBe(1);
  old.onclose?.({ code: 4401 }); old.onerror?.(); old.message(corpus.valid[6]);
  h.advance(500); expect(h.sockets).toHaveLength(2);
  const current = h.sockets[1]; current.onopen?.(); current.message(corpus.valid[6]);
  expect(h.rendered).toHaveLength(2);
  old.message(corpus.valid[4]); expect(h.rendered).toHaveLength(2);
  h.client.stop(); current.onclose?.({ code: 1006 }); h.advance(100000); expect(h.sockets).toHaveLength(2);
});

test("backoff uses bounded full jitter and resets only after valid hydration", () => {
  const h = harness(0.5);
  for (const delay of [500, 1000, 2000, 4000, 8000, 15000, 15000]) {
    const count = h.sockets.length, socket = h.sockets[count - 1];
    socket.onopen?.(); socket.onclose?.({ code: 4429 });
    h.advance(delay - 1); expect(h.sockets).toHaveLength(count);
    h.advance(1); expect(h.sockets).toHaveLength(count + 1);
  }
  const socket = h.sockets.at(-1)!; socket.onopen?.(); socket.message(corpus.valid[4]); socket.onclose?.({ code: 1006 });
  const count = h.sockets.length; h.advance(499); expect(h.sockets).toHaveLength(count);
  h.advance(1); expect(h.sockets).toHaveLength(count + 1); h.client.stop();
  for (const random of [0, 0.999]) {
    const jitter = harness(random); jitter.sockets[0].onclose?.({ code: 1006 });
    jitter.advance(random * 1000); expect(jitter.sockets).toHaveLength(2); jitter.client.stop();
  }
});

test("one heartbeat owner detects blackholes and pending pongs cannot fake hydration", () => {
  for (const hydrated of [false, true]) {
    const h = harness(), socket = h.sockets[0]; h.client.start(); expect(h.sockets).toHaveLength(1);
    socket.onopen?.(); if (hydrated) socket.message(corpus.valid[4]);
    h.advance(30000); expect(socket.sent.at(-1)).toBe("ping");
    if (!hydrated) socket.message("pong");
    h.advance(30000); if (!hydrated) socket.message("pong");
    expect(socket.closed).toBe(false); h.advance(30000); expect(socket.closed).toBe(true);
    expect(h.timers.size).toBe(1); h.advance(500); expect(h.sockets).toHaveLength(2); h.client.stop();
  }
});

test("valid pong extends liveness and explicit credential revocation stops retry", () => {
  const h = harness(), socket = h.sockets[0]; socket.onopen?.(); socket.message(corpus.valid[4]);
  h.advance(60000); socket.message("pong"); h.advance(60000); expect(socket.closed).toBe(false);
  socket.onclose?.({ code: 4401 }); expect(h.timers.size).toBe(0);
  h.client.stop(); h.client.start();
  h.advance(100000); expect(h.sockets).toHaveLength(1); expect(h.rendered).toHaveLength(1);
});

test("requires a complete snapshot on each connection and never clears newer state on empty hydration", () => {
  const h = harness(); h.sockets[0].message({ v: 1, type: "update", death: { revision: "1", value: "0", availability: "available" } });
  expect(h.rendered).toEqual([]); expect(h.sockets[0].closed).toBe(true); h.advance(500);
  h.sockets[1].message(corpus.valid[6]); const previous = h.rendered.at(-1);
  h.sockets[1].onclose?.({ code: 1006 }); h.advance(500); h.sockets[2].message(corpus.valid[4]);
  expect(h.rendered.at(-1)).toEqual(previous); h.client.stop();
});

test("rejects a malformed combined packet before either channel advances", () => {
  const h = harness(), first = h.sockets[0]; first.message(corpus.valid[6]);
  const original = h.rendered.at(-1);
  first.message({ v: 1, type: "update", death: { revision: "99", value: "99", availability: "available" },
    appearance: { ...corpus.valid[4].appearance, revision: "99", fontFamily: "url(remote)" } });
  expect(h.rendered.at(-1)).toEqual(original); expect(first.closed).toBe(true);
  h.advance(500);
  h.sockets[1].message({ ...corpus.valid[4], death: { revision: "50", value: "50", availability: "available" } });
  expect(h.rendered.at(-1)).toMatchObject({ death: { value: "50" } }); h.client.stop();
});
