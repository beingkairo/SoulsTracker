import { DurableObject } from "cloudflare:workers";
import type { HostedAppearance, HostedDeath, HostedEnvelope } from "../../web_overlay/src/hosted-contracts";
import type { Env } from "./index";
import { acquisition, authorize, channelStatus, defaultAppearance, digest, equalVerifier, failure, increment, readBody, reject, response, rotation, route, stateWrite, verifier } from "./protocol";

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

export class OverlayState extends DurableObject<Env> {
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.storage.sql.exec("CREATE TABLE IF NOT EXISTS records (key TEXT PRIMARY KEY CHECK (key IN ('control','death','appearance')), value TEXT NOT NULL)");
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
      // Only this committed result may feed a future socket broadcaster. Retry
      // acknowledgements retain their original body but have no changed channels.
      return response(200, committed.ack);
    } catch (error) { return failure(error); }
  }
}
