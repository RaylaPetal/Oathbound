import { DurableObject } from "cloudflare:workers";
import type { Env } from "../env";
import {
  DEVICE_BURST_FRAMES,
  DEVICE_BURST_WINDOW_SECONDS,
  DEVICE_HOURLY_FRAMES,
  HELD_FRAMES_MAX,
  PEER_FRAME_MAX_AGE_SECONDS,
  PEER_FRAME_MAX_BYTES,
  SOCKET_AUTH_TIMEOUT_SECONDS,
  TIMESTAMP_TOLERANCE_SECONDS,
  nowSeconds,
} from "../lib/constants";
import { verifyEcdsaSignature } from "../lib/crypto";
import { lookupDeviceKey } from "../lib/deviceKeys";
import { logEvent } from "../lib/log";
import { pairAtEpoch } from "../lib/pairs";
import { chargeLive } from "../lib/quotas";
import { newChallengeNonce, socketAuthBaseString } from "./socketAuth";

/** collar/relay-messaging: delivery results reported to the sender. */
export type DeliveryResult = "delivered" | "held" | "offline" | "needs_update" | "over_limit" | "over_budget" | "refused";

/** The fields the relay forwards; nonce and ciphertext are opaque to it. */
export interface PeerFrame {
  pairIdHash: string;
  pairEpoch: number;
  from: "owner" | "sub";
  seq: number;
  sentAt: number;
  nonce: string;
  ciphertext: string;
}

interface Attachment {
  state: "pending" | "authed";
  challenge: string;
  openedAt: number;
}

interface CachedPair {
  owner: string;
  sub: string;
  revoked: boolean;
}

const HEX64 = /^[0-9a-f]{64}$/;
const SIG = /^[A-Za-z0-9_-]{86}$/;
const AEAD_NONCE = /^[A-Za-z0-9_-]{16}$/;
const CIPHERTEXT = /^[A-Za-z0-9_-]{22,10944}$/;
const MESSAGE_ID = /^[A-Za-z0-9_-]{1,32}$/;
const PAIR_CACHE_SECONDS = 600;

/**
 * One per device (addressed by its device key id). Holds that device's live connection with the hibernation API, so an
 * idle connection costs nothing, forwards its frames to the other member's hub, and briefly holds frames for it while
 * it reconnects.
 */
export class DeviceHub extends DurableObject<Env> {
  private readonly sql: SqlStorage;
  // Per-device send limits live in memory: a hub that hibernated wasn't sending, so losing them is harmless.
  private sendTimes: number[] = [];
  // Messages are handled concurrently across awaits; chaining sends keeps one device's frames in order, which the
  // receiver's strictly-increasing seq check depends on.
  private sendChain: Promise<void> = Promise.resolve();

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.sql.exec(`CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS pair_cache (
      pair_id_hash TEXT NOT NULL, pair_epoch INTEGER NOT NULL, owner TEXT NOT NULL, sub TEXT NOT NULL,
      revoked INTEGER NOT NULL, fetched_at INTEGER NOT NULL, PRIMARY KEY (pair_id_hash, pair_epoch)) WITHOUT ROWID`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS held_frames (
      id INTEGER PRIMARY KEY AUTOINCREMENT, sender TEXT NOT NULL, message_id TEXT NOT NULL, pair_id_hash TEXT NOT NULL,
      frame TEXT NOT NULL, expires_at INTEGER NOT NULL)`);
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  // ---- connection lifecycle -------------------------------------------------------------------------------------

  override async fetch(request: Request): Promise<Response> {
    const device = request.headers.get("x-hub-device");
    if (!device || !HEX64.test(device) || request.headers.get("upgrade")?.toLowerCase() !== "websocket") {
      return new Response(null, { status: 400 });
    }
    this.setMeta("device", device);

    const { 0: client, 1: server } = new WebSocketPair();
    const attachment: Attachment = { state: "pending", challenge: newChallengeNonce(), openedAt: nowSeconds() };
    this.ctx.acceptWebSocket(server);
    server.serializeAttachment(attachment);
    server.send(JSON.stringify({ type: "challenge", nonce: attachment.challenge, serverTime: attachment.openedAt }));
    await this.scheduleAlarm(attachment.openedAt + SOCKET_AUTH_TIMEOUT_SECONDS);
    return new Response(null, { status: 101, webSocket: client });
  }

  override async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    if (typeof message !== "string" || message.length > PEER_FRAME_MAX_BYTES + 512) return this.reject(ws, "invalid_request");
    let body: Record<string, unknown>;
    try {
      const parsed: unknown = JSON.parse(message);
      if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) throw new Error();
      body = parsed as Record<string, unknown>;
    } catch {
      return this.reject(ws, "invalid_request");
    }

    const attachment = ws.deserializeAttachment() as Attachment | null;
    if (!attachment) return this.reject(ws, "unauthorized");
    if (attachment.state === "pending") return this.authenticate(ws, attachment, body);
    if (body.type === "send") {
      const run = this.sendChain.then(() => this.send(ws, body));
      this.sendChain = run.catch(() => undefined);
      return run;
    }
    return this.reject(ws, "invalid_request");
  }

  override async webSocketClose(ws: WebSocket, code: number): Promise<void> {
    try { ws.close(code === 1005 || code === 1006 ? 1000 : code, "closed"); } catch { /* already closed */ }
  }

  override async webSocketError(ws: WebSocket): Promise<void> {
    try { ws.close(1011, "error"); } catch { /* already closed */ }
  }

  private async authenticate(ws: WebSocket, attachment: Attachment, body: Record<string, unknown>): Promise<void> {
    const device = this.getMeta("device");
    if (body.type !== "auth" || typeof body.deviceKeyId !== "string" || typeof body.signature !== "string") return this.reject(ws, "unauthorized");
    if (!device || body.deviceKeyId !== device || !SIG.test(body.signature)) return this.reject(ws, "unauthorized");
    if (nowSeconds() - attachment.openedAt > SOCKET_AUTH_TIMEOUT_SECONDS) return this.reject(ws, "unauthorized");

    const publicKey = await lookupDeviceKey(this.env, device);
    if (!publicKey || !(await verifyEcdsaSignature(publicKey, body.signature, socketAuthBaseString(device, attachment.challenge)))) {
      return this.reject(ws, "unauthorized");
    }

    for (const other of this.authedSockets()) {
      if (other === ws) continue;
      try {
        other.send(JSON.stringify({ type: "error", code: "replaced" }));
        other.close(4000, "replaced");
      } catch { /* already gone */ }
    }
    // A fresh challenge so the answer can't be replayed on this socket's attachment.
    ws.serializeAttachment({ state: "authed", challenge: newChallengeNonce(), openedAt: attachment.openedAt } satisfies Attachment);
    if (this.getMeta("everConnected") !== "1") this.setMeta("everConnected", "1");
    ws.send(JSON.stringify({ type: "auth-ok" }));
    await this.flushHeld(ws);
  }

  private reject(ws: WebSocket, code: "unauthorized" | "invalid_request"): void {
    try {
      ws.send(JSON.stringify({ type: "error", code }));
      ws.close(4001, code);
    } catch { /* already closed */ }
  }

  private authedSockets(): WebSocket[] {
    return this.ctx.getWebSockets().filter((s) => (s.deserializeAttachment() as Attachment | null)?.state === "authed");
  }

  // ---- sending --------------------------------------------------------------------------------------------------

  private async send(ws: WebSocket, body: Record<string, unknown>): Promise<void> {
    const id = typeof body.id === "string" && MESSAGE_ID.test(body.id) ? body.id : null;
    if (!id) return this.reject(ws, "invalid_request");
    const ack = (result: DeliveryResult) => ws.send(JSON.stringify({ type: "ack", id, result }));

    const frame = parseFrame(body);
    const now = nowSeconds();
    if (!frame || Math.abs(now - frame.sentAt) > PEER_FRAME_MAX_AGE_SECONDS + TIMESTAMP_TOLERANCE_SECONDS) return ack("refused");
    const self = this.getMeta("device")!;

    const pair = await this.pair(frame.pairIdHash, frame.pairEpoch);
    const side = pair?.owner === self ? "owner" : pair?.sub === self ? "sub" : null;
    if (!pair || pair.revoked || !side || side !== frame.from) return ack("refused");
    const peer = side === "owner" ? pair.sub : pair.owner;
    // Only a Sub's safeword is a safety frame; the flag from anyone else is ignored.
    const safety = body.safety === true && side === "sub";

    if (!this.withinLimits(now, safety)) return ack("over_limit");
    if (!safety && !(await chargeLive(this.env))) return ack("over_budget");

    const result = await this.hub(peer).deliver(frame, self, id);
    ack(result);
  }

  private withinLimits(now: number, safety: boolean): boolean {
    this.sendTimes = this.sendTimes.filter((t) => now - t < 3600);
    const burst = this.sendTimes.filter((t) => now - t < DEVICE_BURST_WINDOW_SECONDS).length;
    if (this.sendTimes.length >= DEVICE_HOURLY_FRAMES) return false;
    if (!safety && burst >= DEVICE_BURST_FRAMES) return false;
    this.sendTimes.push(now);
    return true;
  }

  private async pair(pairIdHash: string, pairEpoch: number): Promise<CachedPair | null> {
    const now = nowSeconds();
    const cached = this.sql
      .exec<{ owner: string; sub: string; revoked: number; fetched_at: number }>(
        `SELECT owner, sub, revoked, fetched_at FROM pair_cache WHERE pair_id_hash = ? AND pair_epoch = ?`, pairIdHash, pairEpoch)
      .toArray()[0];
    if (cached && now - cached.fetched_at < PAIR_CACHE_SECONDS) return { owner: cached.owner, sub: cached.sub, revoked: cached.revoked === 1 };

    const row = await pairAtEpoch(this.env, pairIdHash, pairEpoch);
    if (!row) return null;
    const pair = { owner: row.owner_device_key_id, sub: row.sub_device_key_id, revoked: row.revoked_at !== null };
    this.sql.exec(
      `INSERT OR REPLACE INTO pair_cache (pair_id_hash, pair_epoch, owner, sub, revoked, fetched_at) VALUES (?, ?, ?, ?, ?, ?)`,
      pairIdHash, pairEpoch, pair.owner, pair.sub, pair.revoked ? 1 : 0, now);
    return pair;
  }

  private hub(deviceKeyId: string): DurableObjectStub<DeviceHub> {
    return this.env.DEVICE_HUB.get(this.env.DEVICE_HUB.idFromName(deviceKeyId));
  }

  // ---- receiving (RPC from other hubs and from Worker routes) ---------------------------------------------------

  /** A frame for this hub's device from `sender`'s hub. */
  async deliver(frame: PeerFrame, sender: string, messageId: string): Promise<DeliveryResult> {
    const wire = JSON.stringify({ type: "frame", ...frame });
    let sent = false;
    for (const s of this.authedSockets()) {
      // A socket that's closing throws; the frame is then held as if the device were away.
      try { s.send(wire); sent = true; } catch { /* closing */ }
    }
    if (sent) return "delivered";
    if (this.getMeta("everConnected") !== "1") return "needs_update";

    const held = this.sql.exec<{ n: number }>(`SELECT COUNT(*) AS n FROM held_frames WHERE sender = ? AND pair_id_hash = ?`, sender, frame.pairIdHash).one().n;
    if (held >= HELD_FRAMES_MAX) return "offline";
    const expiresAt = Math.min(frame.sentAt, nowSeconds()) + PEER_FRAME_MAX_AGE_SECONDS;
    this.sql.exec(`INSERT INTO held_frames (sender, message_id, pair_id_hash, frame, expires_at) VALUES (?, ?, ?, ?, ?)`,
      sender, messageId, frame.pairIdHash, wire, expiresAt);
    await this.scheduleAlarm(expiresAt);
    return "held";
  }

  /** The final result of a frame this hub's device sent that the peer's hub was holding. */
  async deliveryUpdate(messageId: string, result: "delivered" | "offline"): Promise<void> {
    const wire = JSON.stringify({ type: "ack", id: messageId, result });
    for (const s of this.authedSockets()) s.send(wire);
  }

  /** collar/relay-messaging: something on this pairing changed; the client runs its pair status check. */
  async hint(pairIdHash: string, pairEpoch: number): Promise<void> {
    this.sql.exec(`DELETE FROM pair_cache WHERE pair_id_hash = ?`, pairIdHash);
    const wire = JSON.stringify({ type: "hint", pairIdHash, pairEpoch });
    for (const s of this.authedSockets()) s.send(wire);
  }

  /** Drops a cached pair row, so the next frame on it re-reads the relay's record. */
  async forgetPair(pairIdHash: string): Promise<void> {
    this.sql.exec(`DELETE FROM pair_cache WHERE pair_id_hash = ?`, pairIdHash);
  }

  private async flushHeld(ws: WebSocket): Promise<void> {
    const now = nowSeconds();
    const rows = this.sql
      .exec<{ id: number; sender: string; message_id: string; frame: string; expires_at: number }>(
        `SELECT id, sender, message_id, frame, expires_at FROM held_frames ORDER BY id`)
      .toArray();
    if (rows.length === 0) return;
    this.sql.exec(`DELETE FROM held_frames`);
    for (const row of rows) {
      const fresh = row.expires_at > now;
      if (fresh) ws.send(row.frame);
      this.ctx.waitUntil(this.hub(row.sender).deliveryUpdate(row.message_id, fresh ? "delivered" : "offline").catch(() => undefined));
    }
  }

  // ---- alarm: auth timeouts and held-frame expiry ---------------------------------------------------------------

  override async alarm(): Promise<void> {
    const now = nowSeconds();
    for (const s of this.ctx.getWebSockets()) {
      const a = s.deserializeAttachment() as Attachment | null;
      if (a?.state === "pending" && now - a.openedAt >= SOCKET_AUTH_TIMEOUT_SECONDS) this.reject(s, "unauthorized");
    }

    const expired = this.sql
      .exec<{ id: number; sender: string; message_id: string }>(`SELECT id, sender, message_id FROM held_frames WHERE expires_at <= ?`, now)
      .toArray();
    if (expired.length > 0) {
      this.sql.exec(`DELETE FROM held_frames WHERE expires_at <= ?`, now);
      await Promise.all(expired.map((row) => this.hub(row.sender).deliveryUpdate(row.message_id, "offline").catch(() => undefined)));
      logEvent("held_frames_expired", { count: expired.length });
    }

    const nextHeld = this.sql.exec<{ t: number | null }>(`SELECT MIN(expires_at) AS t FROM held_frames`).one().t;
    const nextAuth = this.ctx.getWebSockets()
      .map((s) => s.deserializeAttachment() as Attachment | null)
      .filter((a): a is Attachment => a?.state === "pending")
      .map((a) => a.openedAt + SOCKET_AUTH_TIMEOUT_SECONDS);
    const next = [nextHeld, ...nextAuth].filter((t): t is number => typeof t === "number");
    if (next.length > 0) await this.ctx.storage.setAlarm(Math.max(Math.min(...next), now + 1) * 1000);
  }

  private async scheduleAlarm(atSeconds: number): Promise<void> {
    const current = await this.ctx.storage.getAlarm();
    if (current === null || current > atSeconds * 1000) await this.ctx.storage.setAlarm(atSeconds * 1000);
  }

  private getMeta(key: string): string | null {
    return this.sql.exec<{ value: string }>(`SELECT value FROM meta WHERE key = ?`, key).toArray()[0]?.value ?? null;
  }

  private setMeta(key: string, value: string): void {
    if (this.getMeta(key) === value) return;
    this.sql.exec(`INSERT OR REPLACE INTO meta (key, value) VALUES (?, ?)`, key, value);
  }
}

function parseFrame(body: Record<string, unknown>): PeerFrame | null {
  const { pairIdHash, pairEpoch, from, seq, sentAt, nonce, ciphertext } = body;
  if (typeof pairIdHash !== "string" || !HEX64.test(pairIdHash)) return null;
  if (!Number.isSafeInteger(pairEpoch) || (pairEpoch as number) < 0) return null;
  if (from !== "owner" && from !== "sub") return null;
  if (!Number.isSafeInteger(seq) || (seq as number) < 1) return null;
  if (!Number.isSafeInteger(sentAt) || (sentAt as number) < 0) return null;
  if (typeof nonce !== "string" || !AEAD_NONCE.test(nonce)) return null;
  if (typeof ciphertext !== "string" || !CIPHERTEXT.test(ciphertext)) return null;
  return { pairIdHash, pairEpoch: pairEpoch as number, from, seq: seq as number, sentAt: sentAt as number, nonce, ciphertext };
}
