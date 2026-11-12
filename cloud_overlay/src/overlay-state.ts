import { DurableObject } from "cloudflare:workers";
import { maximumHostedBytes, validateHostedJsonTokens } from "../../web_overlay/src/hosted-contracts";
import type { HostedAppearance, HostedDeath, HostedEnvelope } from "../../web_overlay/src/hosted-contracts";
import type { Env } from "./index";
import { acquisition, authorize, capability, channelStatus, clientProtocol, defaultAppearance, digest, equalVerifier, failure, increment, readBody, reject, response, rotation, route, shape, stateWrite, verifier, verifierV2, type Provisioning } from "./protocol";

interface Status {
  v: 1; epoch: string; generation: string;
  death: ReturnType<typeof channelStatus>; appearance: ReturnType<typeof channelStatus>;
}
interface StateAck extends Status { sessionRequestId: string; sequence: string; changed: string[] }
interface RotationAck { v: 1; rotationId: string; epoch: string; generation: string; readGeneration: string }
interface CommitResult { ack: Status | StateAck | RotationAck | null; changed: HostedEnvelope | null; legacy?: boolean; renewed?: boolean }

interface Control {
  readVerifier: string;
  writeVerifier: string;
  epoch: string;
  generation: string;
  readGeneration: string;
  session: { expectedEpoch: string; sessionRequestId: string; ack: Status & { sessionRequestId: string } } | null;
  last: { sequence: string; digest: string; ack: StateAck } | null;
  rotation: { rotationId: string; digest: string; ack: RotationAck } | null;
  authVersion?: 2;
  requestId?: string;
  leaseExpiresAt?: number;
  lastAcceptedAt?: number;
  presenceGeneration?: number;
  legacyContactExpiresAt?: number;
}

type ReaderAttachment = { phase: "pending"; id: string; deadline: number } |
  { phase: "authenticated"; generation: string; protocolVersion: 1 | 2 };
const leaseMs = 180000;

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
      const auth = shape(parsed, ["v", "type", "readCapability", "protocolVersion"], ["v", "type", "readCapability"]);
      if (auth.v !== 1 || auth.type !== "auth") throw new Error();
      const protocolVersion = clientProtocol(auth.protocolVersion);
      if (protocolVersion !== undefined && protocolVersion !== 2) throw new Error();
      const token = capability(auth.readCapability);
      const attachment = socket.deserializeAttachment() as ReaderAttachment;
      if (attachment.phase !== "pending") throw new Error();
      const control = this.load<Control>("control");
      if (!control || !equalVerifier(control.readVerifier, this.roleVerifier(control, attachment.id, "read", token))) {
        socket.close(4401, "Invalid read access"); return;
      }
      const count = this.ctx.getWebSockets().filter(s => s.readyState === WebSocket.OPEN &&
        (s.deserializeAttachment() as ReaderAttachment).phase === "authenticated").length;
      if (count >= 8) { socket.close(4429, "Socket capacity"); return; }
      socket.serializeAttachment({ phase: "authenticated", generation: control.readGeneration,
        protocolVersion: protocolVersion === 2 ? 2 : 1 } satisfies ReaderAttachment);
      socket.send(JSON.stringify({ v: protocolVersion === 2 ? 2 : 1, type: "snapshot", death: this.load<HostedDeath>("death") ?? null,
        appearance: this.load<HostedAppearance>("appearance") ?? defaultAppearance,
        ...(protocolVersion === 2 ? { presence: this.presence(control) } : {}) }));
    } catch { socket.close(4400, "Invalid protocol"); }
    finally { this.cleanReaders(); }
  }

  webSocketClose(socket: WebSocket): void { socket.close(1000); this.cleanReaders(); }
  webSocketError(socket: WebSocket): void { socket.close(1011, "Connection failure"); this.cleanReaders(); }

  private presence(control: Control): { generation: number; serverTime: number; leaseExpiresAt: number | null; legacyContactExpiresAt: number | null; confirmed: boolean } {
    return { generation: control.presenceGeneration ?? 0, serverTime: Date.now(),
      leaseExpiresAt: control.leaseExpiresAt ?? null, legacyContactExpiresAt: control.legacyContactExpiresAt ?? null,
      confirmed: control.lastAcceptedAt !== undefined };
  }

  private broadcast(envelope: HostedEnvelope | null, presence = false): void {
    this.cleanReaders();
    const control = this.load<Control>("control");
    const generation = control?.readGeneration;
    for (const socket of this.ctx.getWebSockets()) {
      const attachment = socket.deserializeAttachment() as ReaderAttachment;
      if (socket.readyState === WebSocket.OPEN && attachment.phase === "authenticated" && attachment.generation === generation) {
        try {
          if (attachment.protocolVersion === 2 && control && (envelope || presence))
            socket.send(JSON.stringify({ v: 2, type: "update", ...(envelope?.death ? { death: envelope.death } : {}),
              ...(envelope?.appearance ? { appearance: envelope.appearance } : {}), presence: this.presence(control) }));
          else if (envelope) socket.send(JSON.stringify(envelope));
        } catch { socket.close(1011, "Connection failure"); }
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

  private roleVerifier(control: Control, id: string, role: "read" | "write", capabilityValue: string): string {
    return control.authVersion === 2 && typeof control.requestId === "string"
      ? verifierV2(control.requestId, role, capabilityValue)
      : verifier(id, role, capabilityValue);
  }

  private exactProvisionReplay(rows: { key: string; value: string }[], claim: Provisioning): boolean {
    if (rows.length !== 3 || rows.map(row => row.key).sort().join(",") !== "appearance,control,death") return false;
    try {
      const values = Object.fromEntries(rows.map(row => [row.key, JSON.parse(row.value)])) as Record<string, unknown>;
      const control = values.control;
      if (control === null || typeof control !== "object" || Array.isArray(control)) return false;
      const stored = control as Record<string, unknown>;
      const keys = ["readVerifier", "writeVerifier", "epoch", "generation", "readGeneration", "session", "last", "rotation", "authVersion", "requestId"];
      return Object.keys(stored).length === keys.length && keys.every(key => Object.hasOwn(stored, key)) &&
        stored.authVersion === 2 && stored.requestId === claim.requestId &&
        stored.readVerifier === claim.readVerifier && stored.writeVerifier === claim.writeVerifier &&
        stored.epoch === "0" && stored.generation === "0" && stored.readGeneration === "0" &&
        stored.session === null && stored.last === null && stored.rotation === null &&
        values.death === null && digest(values.appearance) === digest(defaultAppearance);
    } catch { return false; }
  }

  async provisionV2(expectedId: string, claim: Provisioning): Promise<boolean> {
    try {
      if (!/^[0-9a-f]{64}$/.test(expectedId) || !this.ctx.id.equals(this.env.OVERLAYS.idFromString(expectedId))) return false;
      return this.ctx.storage.transactionSync(() => {
        const rows = this.ctx.storage.sql.exec<{ key: string; value: string }>("SELECT key,value FROM records").toArray();
        if (rows.length) return this.exactProvisionReplay(rows, claim);
        const control: Control = { readVerifier: claim.readVerifier, writeVerifier: claim.writeVerifier,
          epoch: "0", generation: "0", readGeneration: "0", session: null, last: null, rotation: null,
          authVersion: 2, requestId: claim.requestId };
        this.ctx.storage.sql.exec("INSERT INTO records (key,value) VALUES ('control',?),('death','null'),('appearance',?)",
          JSON.stringify(control), JSON.stringify(defaultAppearance));
        return true;
      });
    } catch { return false; }
  }

  private initialControl(id: string, token: string, persist: boolean): Control {
    // Installed only through authenticated deployment; ignored once control exists.
    try {
      const config = shape(this.env.BOOTSTRAP, ["v", "overlayId", "readVerifier", "writeVerifier"]);
      if (this.ctx.storage.sql.exec("SELECT key FROM records LIMIT 1").toArray().length) return reject(403, "forbidden");
      if (config.v !== 1 || config.overlayId !== id ||
        !this.ctx.id.equals(this.env.OVERLAYS.idFromName(id)) ||
        typeof config.readVerifier !== "string" || typeof config.writeVerifier !== "string" ||
        !/^[0-9a-f]{64}$/.test(config.readVerifier) || !/^[0-9a-f]{64}$/.test(config.writeVerifier) ||
        config.readVerifier === config.writeVerifier ||
        !equalVerifier(config.writeVerifier, verifier(id, "write", token)) ||
        equalVerifier(config.readVerifier, verifier(id, "read", token))) return reject(403, "forbidden");
      const control: Control = { readVerifier: config.readVerifier, writeVerifier: config.writeVerifier,
        epoch: "0", generation: "0", readGeneration: "0", session: null, last: null, rotation: null };
      if (persist) this.ctx.storage.sql.exec("INSERT INTO records (key,value) VALUES ('control',?)", JSON.stringify(control));
      return control;
    } catch { return reject(403, "forbidden"); }
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
      const header = request.headers.get("X-SoulsTracker-Protocol");
      if (header !== null && !/^(?:[1-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-5])$/.test(header))
        return reject(400, "invalid_body");
      const bodyProtocol = (session ?? write ?? rotate)?.protocolVersion;
      if (header !== null && bodyProtocol !== undefined && Number(header) !== bodyProtocol) return reject(400, "invalid_body");
      const protocolVersion = action === "publisher" ? clientProtocol(header === null ? undefined : Number(header)) : bodyProtocol;
      const committed = this.ctx.storage.transactionSync((): CommitResult => {
        const control = this.load<Control>("control") ??
          (action === "publisher" ? this.initialControl(id, token, protocolVersion === 2) : reject(403, "forbidden"));
        if (!control || !equalVerifier(control.writeVerifier, this.roleVerifier(control, id, "write", token))) return reject(403, "forbidden");
        if (protocolVersion !== 2) {
          if (action !== "publisher") {
            control.legacyContactExpiresAt = Date.now() + leaseMs;
            this.save("control", control);
            return { ack: null, changed: null, legacy: true };
          }
          return reject(409, "client_update_required");
        }
        let death = this.load<HostedDeath | null>("death") ?? null;
        let appearance = this.load<HostedAppearance>("appearance") ?? defaultAppearance;
        if (this.load("death") === undefined) this.save("death", death);
        if (this.load("appearance") === undefined) this.save("appearance", appearance);
        const status = (): Status => ({ v: 1, epoch: control.epoch, generation: control.generation,
          death: channelStatus(death), appearance: channelStatus(appearance) });
        if (rotate) {
          const readVerifier = rotate.readCapability ? this.roleVerifier(control, id, "read", rotate.readCapability) : undefined;
          const writeVerifier = rotate.writeCapability ? this.roleVerifier(control, id, "write", rotate.writeCapability) : undefined;
          const requestDigest = digest({ v: 1, rotationId: rotate.rotationId, expectedGeneration: rotate.expectedGeneration, readVerifier, writeVerifier });
          if (control.rotation?.rotationId === rotate.rotationId) {
            if (control.rotation.digest !== requestDigest) return reject(409, "rotation_conflict");
            return { ack: control.rotation.ack, changed: null };
          }
          if (rotate.expectedGeneration !== control.generation) return reject(409, "rotation_conflict");
          // Keep capabilities distinct across roles and require actual replacements.
          if ((readVerifier && equalVerifier(readVerifier, control.readVerifier)) ||
            (writeVerifier && equalVerifier(writeVerifier, control.writeVerifier)) ||
            (rotate.readCapability && equalVerifier(this.roleVerifier(control, id, "write", rotate.readCapability), control.writeVerifier)) ||
            (rotate.writeCapability && equalVerifier(this.roleVerifier(control, id, "read", rotate.writeCapability), control.readVerifier)) ||
            (rotate.readCapability && rotate.readCapability === rotate.writeCapability)) return reject(400, "invalid_rotation");
          control.epoch = increment(control.epoch);
          control.generation = increment(control.generation);
          if (readVerifier) { control.readVerifier = readVerifier; control.readGeneration = increment(control.readGeneration); }
          if (writeVerifier) control.writeVerifier = writeVerifier;
          control.session = null;
          control.last = null;
          control.leaseExpiresAt = undefined;
          control.lastAcceptedAt = undefined;
          control.presenceGeneration = (control.presenceGeneration ?? 0) + 1;
          const ack: RotationAck = { v: 1, rotationId: rotate.rotationId, epoch: control.epoch,
            generation: control.generation, readGeneration: control.readGeneration };
          control.rotation = { rotationId: rotate.rotationId, digest: requestDigest, ack };
          this.save("control", control);
          return { ack, changed: null, renewed: true };
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
          control.leaseExpiresAt = undefined;
          control.lastAcceptedAt = undefined;
          control.presenceGeneration = (control.presenceGeneration ?? 0) + 1;
          this.save("control", control);
          return { ack, changed: null, renewed: true };
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
          const acceptedAt = Date.now();
          if (write.death) control.lastAcceptedAt = acceptedAt;
          control.leaseExpiresAt = acceptedAt + leaseMs;
          control.presenceGeneration = (control.presenceGeneration ?? 0) + 1;
          const channels = [ ...(changed.death ? ["death"] : []), ...(changed.appearance ? ["appearance"] : []) ];
          const ack = { ...status(), sessionRequestId: write.sessionRequestId, sequence: write.sequence, changed: channels };
          control.last = { sequence: write.sequence, digest: requestDigest, ack };
          this.save("control", control);
          return { ack, changed: channels.length ? changed : null, renewed: true };
        }
        if (action !== "publisher") return reject(404, "not_found");
        return { ack: status(), changed: null };
      });
      this.cleanReaders();
      if (committed.legacy) {
        this.broadcast(null, true);
        return response(409, { error: "client_update_required" });
      }
      if (committed.changed || committed.renewed) this.broadcast(committed.changed, true);
      const result = response(200, committed.ack);
      const presence = this.presence(this.load<Control>("control")!);
      result.headers.set("X-SoulsTracker-Server-Time", String(presence.serverTime));
      result.headers.set("X-SoulsTracker-Lease-Expires", String(presence.leaseExpiresAt ?? 0));
      return result;
    } catch (error) { return failure(error); }
  }
}
