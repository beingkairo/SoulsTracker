import { DurableObject } from "cloudflare:workers";
import { maximumHostedBytes, validateHostedJsonTokens } from "../../web_overlay/src/hosted-contracts";
import type { HostedAppearance, HostedDeath, HostedEnvelope } from "../../web_overlay/src/hosted-contracts";
import type { Env } from "./index";
import { acquisition, authorize, capability, channelStatus, defaultAppearance, digest, equalVerifier, failure, increment, readBody, reject, response, rotation, route, shape, stateWrite, verifier } from "./protocol";

interface Status {
  v: 1; epoch: string; generation: string;
  death: ReturnType<typeof channelStatus>; appearance: ReturnType<typeof channelStatus>;
}
interface StateAck extends Status { sessionRequestId: string; sequence: string; changed: string[] }
interface RotationAck { v: 1; rotationId: string; epoch: string; generation: string; readGeneration: string }
interface CommitResult { ack: Status | StateAck | RotationAck; changed: HostedEnvelope | null }

interface Control {
  readVerifier: string;
  writeVerifier: string;
  epoch: string;
  generation: string;
  readGeneration: string;
  session: { expectedEpoch: string; sessionRequestId: string; ack: Status & { sessionRequestId: string } } | null;
  last: { sequence: string; digest: string; ack: StateAck } | null;
  rotation: { rotationId: string; digest: string; ack: RotationAck } | null;
}

type ReaderAttachment = { phase: "pending"; id: string; deadline: number } |
  { phase: "authenticated"; generation: string };

export class OverlayState extends DurableObject<Env> {
  private pendingTimer: ReturnType<typeof setTimeout> | undefined;
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.storage.sql.exec("CREATE TABLE IF NOT EXISTS records (key TEXT PRIMARY KEY CHECK (key IN ('control','death','appearance')), value TEXT NOT NULL)");
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
    this.cleanReaders();
  }

  // Attachments and storage survive wake. Only pending authentication owns a
  // bounded timer; fixed automatic ping responses cannot extend its deadline.
  private cleanReaders(): void {
    if (this.pendingTimer !== undefined) clearTimeout(this.pendingTimer);
    this.pendingTimer = undefined;
    const control = this.load<Control>("control");
    let deadline = Infinity;
    for (const socket of this.ctx.getWebSockets()) {
      if (socket.readyState !== WebSocket.OPEN) continue;
      const attachment = socket.deserializeAttachment() as ReaderAttachment | null;
      if (!attachment) { socket.close(4400, "Invalid protocol"); continue; }
      if (attachment.phase === "pending") {
        if (attachment.deadline <= Date.now()) socket.close(4401, "Authentication expired");
        else deadline = Math.min(deadline, attachment.deadline);
      } else if (!control || attachment.generation !== control.readGeneration) socket.close(4401, "Read access revoked");
    }
    if (deadline !== Infinity) this.pendingTimer = setTimeout(() => this.cleanReaders(), Math.max(1, deadline - Date.now()));
  }

  private live(id: string): Response {
    this.cleanReaders();
    if (!this.load<Control>("control")) return reject(403, "forbidden");
    const pair = new WebSocketPair();
    const socket = pair[1];
    const pending = this.ctx.getWebSockets().filter(s => s.readyState === WebSocket.OPEN &&
      (s.deserializeAttachment() as ReaderAttachment).phase === "pending").length;
    this.ctx.acceptWebSocket(socket);
    socket.serializeAttachment({ phase: "pending", id, deadline: Date.now() + 5000 } satisfies ReaderAttachment);
    if (pending >= 2) socket.close(4429, "Socket capacity");
    this.cleanReaders();
    return new Response(null, { status: 101, webSocket: pair[0], headers: {
      "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer"
    } });
  }

  webSocketMessage(socket: WebSocket, message: string | ArrayBuffer): void {
    this.cleanReaders();
    if (socket.readyState !== WebSocket.OPEN) return;
    try {
      if (typeof message !== "string" || new TextEncoder().encode(message).length > maximumHostedBytes) throw new Error();
      const parsed: unknown = JSON.parse(message);
      validateHostedJsonTokens(message);
      const auth = shape(parsed, ["v", "type", "readCapability"]);
      if (auth.v !== 1 || auth.type !== "auth") throw new Error();
      const token = capability(auth.readCapability);
      const attachment = socket.deserializeAttachment() as ReaderAttachment;
      if (attachment.phase !== "pending") throw new Error();
      const control = this.load<Control>("control");
      if (!control || !equalVerifier(control.readVerifier, verifier(attachment.id, "read", token))) {
        socket.close(4401, "Invalid read access"); return;
      }
      const count = this.ctx.getWebSockets().filter(s => s.readyState === WebSocket.OPEN &&
        (s.deserializeAttachment() as ReaderAttachment).phase === "authenticated").length;
      if (count >= 8) { socket.close(4429, "Socket capacity"); return; }
      socket.serializeAttachment({ phase: "authenticated", generation: control.readGeneration } satisfies ReaderAttachment);
      socket.send(JSON.stringify({ v: 1, type: "snapshot", death: this.load<HostedDeath>("death") ?? null,
        appearance: this.load<HostedAppearance>("appearance") ?? defaultAppearance }));
    } catch { socket.close(4400, "Invalid protocol"); }
    finally { this.cleanReaders(); }
  }

  webSocketClose(socket: WebSocket): void { socket.close(1000); this.cleanReaders(); }
  webSocketError(socket: WebSocket): void { socket.close(1011, "Connection failure"); this.cleanReaders(); }

  private broadcast(envelope: HostedEnvelope): void {
    this.cleanReaders();
    const generation = this.load<Control>("control")?.readGeneration;
    for (const socket of this.ctx.getWebSockets()) {
      const attachment = socket.deserializeAttachment() as ReaderAttachment;
      if (socket.readyState === WebSocket.OPEN && attachment.phase === "authenticated" && attachment.generation === generation) {
        try { socket.send(JSON.stringify(envelope)); } catch { socket.close(1011, "Connection failure"); }
      }
    }
  }

  private load<T>(key: string): T | undefined {
    const row = this.ctx.storage.sql.exec<{ value: string }>("SELECT value FROM records WHERE key = ?", key).toArray()[0];
    return row ? JSON.parse(row.value) as T : undefined;
  }

  private save(key: string, value: unknown): void {
    this.ctx.storage.sql.exec("INSERT INTO records (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, JSON.stringify(value));
  }

  async fetch(request: Request): Promise<Response> {
    try {
      const { id, action } = route(request);
      if (action === "live") return this.live(id);
      const token = authorize(request);
      const body = action === "publisher" ? undefined : await readBody(request);
      const session = action === "session" ? acquisition(body) : undefined;
      const write = action === "state" ? stateWrite(body) : undefined;
      const rotate = action === "credentials" ? rotation(body) : undefined;
      const committed = this.ctx.storage.transactionSync((): CommitResult => {
        const control = this.load<Control>("control");
        if (!control || !equalVerifier(control.writeVerifier, verifier(id, "write", token))) return reject(403, "forbidden");
        let death = this.load<HostedDeath | null>("death") ?? null;
        let appearance = this.load<HostedAppearance>("appearance") ?? defaultAppearance;
        if (this.load("death") === undefined) this.save("death", death);
        if (this.load("appearance") === undefined) this.save("appearance", appearance);
        const status = (): Status => ({ v: 1, epoch: control.epoch, generation: control.generation,
          death: channelStatus(death), appearance: channelStatus(appearance) });
        if (rotate) {
          const readVerifier = rotate.readCapability ? verifier(id, "read", rotate.readCapability) : undefined;
          const writeVerifier = rotate.writeCapability ? verifier(id, "write", rotate.writeCapability) : undefined;
          const requestDigest = digest({ v: 1, rotationId: rotate.rotationId, expectedGeneration: rotate.expectedGeneration, readVerifier, writeVerifier });
          if (control.rotation?.rotationId === rotate.rotationId) {
            if (control.rotation.digest !== requestDigest) return reject(409, "rotation_conflict");
            return { ack: control.rotation.ack, changed: null };
          }
          if (rotate.expectedGeneration !== control.generation) return reject(409, "rotation_conflict");
          // Keep capabilities distinct across roles and require actual replacements.
          if ((readVerifier && equalVerifier(readVerifier, control.readVerifier)) ||
            (writeVerifier && equalVerifier(writeVerifier, control.writeVerifier)) ||
            (rotate.readCapability && equalVerifier(verifier(id, "write", rotate.readCapability), control.writeVerifier)) ||
            (rotate.writeCapability && equalVerifier(verifier(id, "read", rotate.writeCapability), control.readVerifier)) ||
            (rotate.readCapability && rotate.readCapability === rotate.writeCapability)) return reject(400, "invalid_rotation");
          control.epoch = increment(control.epoch);
          control.generation = increment(control.generation);
          if (readVerifier) { control.readVerifier = readVerifier; control.readGeneration = increment(control.readGeneration); }
          if (writeVerifier) control.writeVerifier = writeVerifier;
          control.session = null;
          control.last = null;
          const ack: RotationAck = { v: 1, rotationId: rotate.rotationId, epoch: control.epoch,
            generation: control.generation, readGeneration: control.readGeneration };
          control.rotation = { rotationId: rotate.rotationId, digest: requestDigest, ack };
          this.save("control", control);
          return { ack, changed: null };
        }
        if (session) {
          if (control.session?.sessionRequestId === session.sessionRequestId) {
            if (control.session.expectedEpoch !== session.expectedEpoch) return reject(409, "publisher_conflict");
            return { ack: control.session.ack, changed: null };
          }
          if (session.expectedEpoch !== control.epoch) return reject(409, "publisher_conflict");
          control.epoch = increment(control.epoch);
          const ack = { ...status(), sessionRequestId: session.sessionRequestId };
          control.session = { ...session, ack };
          control.last = null;
          this.save("control", control);
          return { ack, changed: null };
        }
        if (write) {
          if (write.epoch !== control.epoch || write.sessionRequestId !== control.session?.sessionRequestId)
            return reject(409, "publisher_conflict");
          const requestDigest = digest(write);
          if (control.last && BigInt(write.sequence) <= BigInt(control.last.sequence)) {
            if (write.sequence !== control.last.sequence || requestDigest !== control.last.digest)
              return reject(409, "sequence_conflict");
            return { ack: control.last.ack, changed: null };
          }
          const changed: HostedEnvelope = { v: 1, type: "update",
            ...(write.death && channelStatus(write.death).digest !== channelStatus(death).digest
              ? { death: { ...write.death, revision: increment(death?.revision ?? "0") } } : {}),
            ...(write.appearance && channelStatus(write.appearance).digest !== channelStatus(appearance).digest
              ? { appearance: { ...write.appearance, revision: increment(appearance.revision) } } : {}) };
          if (changed.death) { death = changed.death; this.save("death", death); }
          if (changed.appearance) { appearance = changed.appearance; this.save("appearance", appearance); }
          const channels = [ ...(changed.death ? ["death"] : []), ...(changed.appearance ? ["appearance"] : []) ];
          const ack = { ...status(), sessionRequestId: write.sessionRequestId, sequence: write.sequence, changed: channels };
          control.last = { sequence: write.sequence, digest: requestDigest, ack };
          this.save("control", control);
          return { ack, changed: channels.length ? changed : null };
        }
        if (action !== "publisher") return reject(404, "not_found");
        return { ack: status(), changed: null };
      });
      this.cleanReaders();
      if (committed.changed) this.broadcast(committed.changed);
      return response(200, committed.ack);
    } catch (error) { return failure(error); }
  }
}
