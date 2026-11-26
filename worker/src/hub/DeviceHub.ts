import { DurableObject } from "cloudflare:workers";
import type { Env } from "../env";
import {
  CHAT_DEVICE_BURST_MESSAGES,
  CHAT_DEVICE_BURST_WINDOW_SECONDS,
  CHAT_DEVICE_HOURLY_MESSAGES,
  CHAT_STORED_PER_SENDER_PER_PAIR_MAX,
  CHAT_STORED_TTL_SECONDS,
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
import { matchById } from "../lib/lfp";
import { markOffline, markOnline, onlineCount } from "../lib/presence";
import { pairAtEpoch } from "../lib/pairs";
import { chargeChat, chargeLive } from "../lib/quotas";
import { newChallengeNonce, socketAuthBaseString } from "./socketAuth";

/** collar/relay-messaging: delivery results reported to the sender. */
export type DeliveryResult = "delivered" | "held" | "offline" | "needs_update" | "over_limit" | "over_budget" | "refused";

/** Results for a chat message; "stored" is followed later by "delivered" or "expired". */
export type ChatResult = "delivered" | "stored" | "mailbox_full" | "needs_update" | "over_limit" | "over_budget" | "refused";
export type ChatReceipt = "delivered" | "expired";

/**
 * The fields the relay forwards; nonce and ciphertext are opaque to it. A match frame carries its match id in
 * pairIdHash with epoch 0, and a/b as its sides, so it shares the chat mailbox and receipts.
 */
export interface PeerFrame {
  channel: "live" | "chat" | "match";
  pairIdHash: string;
  pairEpoch: number;
  from: "owner" | "sub" | "a" | "b";
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

interface CachedMatch {
  a: string;
  b: string;
  ended: boolean;
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
const PRESENCE_MIN_INTERVAL_SECONDS = 20;

/**
 * One per device (addressed by its device key id). Holds that device's live connection with the hibernation API, so an
 * idle connection costs nothing, forwards its frames to the other member's hub, and briefly holds frames for it while
 * it reconnects.
 */
export class DeviceHub extends DurableObject<Env> {
  private readonly sql: SqlStorage;
  // Per-device send limits live in memory: a hub that hibernated wasn't sending, so losing them is harmless.
  private sendTimes: number[] = [];
  private chatTimes: number[] = [];
  private lastPresenceAt = 0;
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
    // Chat for this device while it's offline, and outcomes of this device's own chat while it was offline.
    this.sql.exec(`CREATE TABLE IF NOT EXISTS chat_mailbox (
      id INTEGER PRIMARY KEY AUTOINCREMENT, sender TEXT NOT NULL, message_id TEXT NOT NULL, pair_id_hash TEXT NOT NULL,
      pair_epoch INTEGER NOT NULL, frame TEXT NOT NULL, expires_at INTEGER NOT NULL)`);
    this.sql.exec(`CREATE INDEX IF NOT EXISTS chat_mailbox_sender ON chat_mailbox (sender, pair_id_hash)`);
    // Hubs created before match chat have no scope column; every row they hold is pairing chat.
    if (!this.sql.exec<{ name: string }>(`PRAGMA table_info(chat_mailbox)`).toArray().some((c) => c.name === "scope")) {
      this.sql.exec(`ALTER TABLE chat_mailbox ADD COLUMN scope TEXT NOT NULL DEFAULT 'pair'`);
    }
    this.sql.exec(`CREATE TABLE IF NOT EXISTS match_cache (
      match_id TEXT PRIMARY KEY, a TEXT NOT NULL, b TEXT NOT NULL, ended INTEGER NOT NULL, fetched_at INTEGER NOT NULL) WITHOUT ROWID`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS chat_receipts (
      id INTEGER PRIMARY KEY AUTOINCREMENT, message_id TEXT NOT NULL, result TEXT NOT NULL, expires_at INTEGER NOT NULL)`);
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
    if (body.type === "presence") return this.presence(ws, false);
    if (body.type === "send") {
      const run = this.sendChain.then(() => this.send(ws, body));
      this.sendChain = run.catch(() => undefined);
      return run;
    }
    return this.reject(ws, "invalid_request");
  }

  override async webSocketClose(ws: WebSocket, code: number): Promise<void> {
    try { ws.close(code === 1005 || code === 1006 ? 1000 : code, "closed"); } catch { /* already closed */ }
    await this.offlineIfLast(ws);
  }

  override async webSocketError(ws: WebSocket): Promise<void> {
    try { ws.close(1011, "error"); } catch { /* already closed */ }
    await this.offlineIfLast(ws);
  }

  /** A replaced connection closing leaves its successor online. */
  private async offlineIfLast(closed: WebSocket): Promise<void> {
    if ((closed.deserializeAttachment() as Attachment | null)?.state !== "authed") return;
    if (this.authedSockets().some((s) => s !== closed && s.readyState === WebSocket.OPEN)) return;
    const device = this.getMeta("device");
    if (device) await markOffline(this.env, device).catch(() => logEvent("presence_failed"));
  }

  /** Marks the device online and tells it how many are. At most one a few seconds, so a looping client costs nothing. */
  private async presence(ws: WebSocket, connected: boolean): Promise<void> {
    const now = nowSeconds();
    if (!connected && now - this.lastPresenceAt < PRESENCE_MIN_INTERVAL_SECONDS) return;
    this.lastPresenceAt = now;
    try {
      await markOnline(this.env, this.getMeta("device")!, connected);
      const { online, onlineAt } = await onlineCount(this.env);
      ws.send(JSON.stringify({ type: "presence", online, onlineAt }));
    } catch {
      logEvent("presence_failed");
    }
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
    if (Array.isArray(body.features) && body.features.includes("chat")) this.setMeta("chatCapable", "1");
    if (Array.isArray(body.features) && body.features.includes("match")) this.setMeta("matchCapable", "1");
    ws.send(JSON.stringify({ type: "auth-ok" }));
    await this.presence(ws, true);
    await this.flushHeld(ws);
    await this.flushChat(ws);
    this.flushReceipts(ws);
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
    if (frame?.channel === "chat") return this.sendChat(ws, id, frame);
    if (frame?.channel === "match") return this.sendMatchChat(ws, id, frame);
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

  private async sendChat(ws: WebSocket, id: string, frame: PeerFrame): Promise<void> {
    const ack = (result: ChatResult) => ws.send(JSON.stringify({ type: "ack", id, result }));
    const now = nowSeconds();
    if (frame.sentAt > now + TIMESTAMP_TOLERANCE_SECONDS || frame.sentAt < now - CHAT_STORED_TTL_SECONDS) return ack("refused");
    const self = this.getMeta("device")!;

    const pair = await this.pair(frame.pairIdHash, frame.pairEpoch);
    const side = pair?.owner === self ? "owner" : pair?.sub === self ? "sub" : null;
    if (!pair || pair.revoked || !side || side !== frame.from) return ack("refused");
    const peer = side === "owner" ? pair.sub : pair.owner;

    if (!this.withinChatLimits(now)) return ack("over_limit");
    if (!(await chargeChat(this.env))) return ack("over_budget");
    ack(await this.hub(peer).deliverChat(frame, self, id));
  }

  private async sendMatchChat(ws: WebSocket, id: string, frame: PeerFrame): Promise<void> {
    const ack = (result: ChatResult) => ws.send(JSON.stringify({ type: "ack", id, result }));
    const now = nowSeconds();
    if (frame.sentAt > now + TIMESTAMP_TOLERANCE_SECONDS || frame.sentAt < now - CHAT_STORED_TTL_SECONDS) return ack("refused");
    const self = this.getMeta("device")!;

    const match = await this.match(frame.pairIdHash);
    const side = match?.a === self ? "a" : match?.b === self ? "b" : null;
    if (!match || match.ended || !side || side !== frame.from) return ack("refused");
    const peer = side === "a" ? match.b : match.a;

    if (!this.withinChatLimits(now)) return ack("over_limit");
    if (!(await chargeChat(this.env))) return ack("over_budget");
    ack(await this.hub(peer).deliverChat(frame, self, id));
  }

  private async match(matchId: string): Promise<CachedMatch | null> {
    const now = nowSeconds();
    const cached = this.sql
      .exec<{ a: string; b: string; ended: number; fetched_at: number }>(`SELECT a, b, ended, fetched_at FROM match_cache WHERE match_id = ?`, matchId)
      .toArray()[0];
    if (cached && now - cached.fetched_at < PAIR_CACHE_SECONDS) return { a: cached.a, b: cached.b, ended: cached.ended === 1 };

    const row = await matchById(this.env, matchId);
    if (!row) return null;
    const match = { a: row.device_a, b: row.device_b, ended: row.ended_at !== null };
    this.sql.exec(`INSERT OR REPLACE INTO match_cache (match_id, a, b, ended, fetched_at) VALUES (?, ?, ?, ?, ?)`,
      matchId, match.a, match.b, match.ended ? 1 : 0, now);
    return match;
  }

  private withinChatLimits(now: number): boolean {
    this.chatTimes = this.chatTimes.filter((t) => now - t < 3600);
    const burst = this.chatTimes.filter((t) => now - t < CHAT_DEVICE_BURST_WINDOW_SECONDS).length;
    if (this.chatTimes.length >= CHAT_DEVICE_HOURLY_MESSAGES || burst >= CHAT_DEVICE_BURST_MESSAGES) return false;
    this.chatTimes.push(now);
    return true;
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
    // Live frames keep the wire shape older clients expect (no channel field).
    const wire = JSON.stringify({
      type: "frame", pairIdHash: frame.pairIdHash, pairEpoch: frame.pairEpoch, from: frame.from, seq: frame.seq,
      sentAt: frame.sentAt, nonce: frame.nonce, ciphertext: frame.ciphertext,
    });
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

  /** A chat message for this hub's device from `sender`'s hub; stored while the device is away. */
  async deliverChat(frame: PeerFrame, sender: string, messageId: string): Promise<ChatResult> {
    const scope = frame.channel === "match" ? "match" : "pair";
    if (this.getMeta(scope === "match" ? "matchCapable" : "chatCapable") !== "1") return "needs_update";
    const wire = JSON.stringify(scope === "match"
      ? { type: "frame", channel: "match", matchId: frame.pairIdHash, from: frame.from, seq: frame.seq, sentAt: frame.sentAt, nonce: frame.nonce, ciphertext: frame.ciphertext }
      : { type: "frame", ...frame });
    let sent = false;
    for (const s of this.authedSockets()) {
      try { s.send(wire); sent = true; } catch { /* closing */ }
    }
    if (sent) return "delivered";

    const stored = this.sql.exec<{ n: number }>(`SELECT COUNT(*) AS n FROM chat_mailbox WHERE sender = ? AND pair_id_hash = ?`, sender, frame.pairIdHash).one().n;
    if (stored >= CHAT_STORED_PER_SENDER_PER_PAIR_MAX) return "mailbox_full";
    const expiresAt = nowSeconds() + CHAT_STORED_TTL_SECONDS;
    this.sql.exec(`INSERT INTO chat_mailbox (sender, message_id, pair_id_hash, pair_epoch, frame, expires_at, scope) VALUES (?, ?, ?, ?, ?, ?, ?)`,
      sender, messageId, frame.pairIdHash, frame.pairEpoch, wire, expiresAt, scope);
    await this.scheduleAlarm(expiresAt);
    return "stored";
  }

  /** The final outcome of a stored chat message this hub's device sent; kept until it next connects if it's away. */
  async chatReceipt(messageId: string, result: ChatReceipt): Promise<void> {
    const wire = JSON.stringify({ type: "ack", id: messageId, result });
    let sent = false;
    for (const s of this.authedSockets()) {
      try { s.send(wire); sent = true; } catch { /* closing */ }
    }
    if (sent) return;
    const expiresAt = nowSeconds() + CHAT_STORED_TTL_SECONDS;
    this.sql.exec(`INSERT INTO chat_receipts (message_id, result, expires_at) VALUES (?, ?, ?)`, messageId, result, expiresAt);
    await this.scheduleAlarm(expiresAt);
  }

  /** The pairing or match ended: chat still waiting here for it is dropped, and its senders are told it expired. */
  async purgeChat(pairIdHash: string): Promise<void> {
    this.sql.exec(`DELETE FROM pair_cache WHERE pair_id_hash = ?`, pairIdHash);
    this.sql.exec(`DELETE FROM match_cache WHERE match_id = ?`, pairIdHash);
    const rows = this.sql
      .exec<{ sender: string; message_id: string }>(`SELECT sender, message_id FROM chat_mailbox WHERE pair_id_hash = ?`, pairIdHash)
      .toArray();
    if (rows.length === 0) return;
    this.sql.exec(`DELETE FROM chat_mailbox WHERE pair_id_hash = ?`, pairIdHash);
    await Promise.all(rows.map((row) => this.hub(row.sender).chatReceipt(row.message_id, "expired").catch(() => undefined)));
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

  /** collar/looking-for-pair: a match was made or ended for this device; the client re-reads its matches. */
  async lfpHint(): Promise<void> {
    const wire = JSON.stringify({ type: "hint", kind: "lfp" });
    for (const s of this.authedSockets()) {
      try { s.send(wire); } catch { /* closing */ }
    }
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

  private async flushChat(ws: WebSocket): Promise<void> {
    const rows = this.sql
      .exec<{ id: number; sender: string; message_id: string; pair_id_hash: string; pair_epoch: number; frame: string; scope: string }>(
        `SELECT id, sender, message_id, pair_id_hash, pair_epoch, frame, scope FROM chat_mailbox ORDER BY id`)
      .toArray();
    if (rows.length === 0) return;
    const self = this.getMeta("device");
    const live = new Map<string, boolean>();
    for (const row of rows) {
      const key = `${row.scope}:${row.pair_id_hash}:${row.pair_epoch}`;
      if (!live.has(key)) {
        if (row.scope === "match") {
          const match = await this.match(row.pair_id_hash);
          live.set(key, !!match && !match.ended && (match.a === self || match.b === self) && (match.a === row.sender || match.b === row.sender));
        } else {
          const pair = await this.pair(row.pair_id_hash, row.pair_epoch);
          live.set(key, !!pair && !pair.revoked && (pair.owner === self || pair.sub === self) && (pair.owner === row.sender || pair.sub === row.sender));
        }
      }
      let sent = false;
      if (live.get(key)) {
        try { ws.send(row.frame); sent = true; } catch { /* closing: kept for the next connect */ }
        if (!sent) return;
      }
      this.sql.exec(`DELETE FROM chat_mailbox WHERE id = ?`, row.id);
      this.ctx.waitUntil(this.hub(row.sender).chatReceipt(row.message_id, sent ? "delivered" : "expired").catch(() => undefined));
    }
  }

  private flushReceipts(ws: WebSocket): void {
    const rows = this.sql.exec<{ id: number; message_id: string; result: string }>(`SELECT id, message_id, result FROM chat_receipts ORDER BY id`).toArray();
    for (const row of rows) {
      try { ws.send(JSON.stringify({ type: "ack", id: row.message_id, result: row.result })); } catch { return; }
      this.sql.exec(`DELETE FROM chat_receipts WHERE id = ?`, row.id);
    }
  }

  // ---- alarm: auth timeouts, held-frame and chat expiry ---------------------------------------------------------

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

    const expiredChat = this.sql
      .exec<{ sender: string; message_id: string }>(`SELECT sender, message_id FROM chat_mailbox WHERE expires_at <= ?`, now)
      .toArray();
    if (expiredChat.length > 0) {
      this.sql.exec(`DELETE FROM chat_mailbox WHERE expires_at <= ?`, now);
      await Promise.all(expiredChat.map((row) => this.hub(row.sender).chatReceipt(row.message_id, "expired").catch(() => undefined)));
      logEvent("chat_expired", { count: expiredChat.length });
    }
    this.sql.exec(`DELETE FROM chat_receipts WHERE expires_at <= ?`, now);

    const nextHeld = this.sql.exec<{ t: number | null }>(`SELECT MIN(expires_at) AS t FROM held_frames`).one().t;
    const nextChat = this.sql.exec<{ t: number | null }>(`SELECT MIN(expires_at) AS t FROM chat_mailbox`).one().t;
    const nextReceipt = this.sql.exec<{ t: number | null }>(`SELECT MIN(expires_at) AS t FROM chat_receipts`).one().t;
    const nextAuth = this.ctx.getWebSockets()
      .map((s) => s.deserializeAttachment() as Attachment | null)
      .filter((a): a is Attachment => a?.state === "pending")
      .map((a) => a.openedAt + SOCKET_AUTH_TIMEOUT_SECONDS);
    const next = [nextHeld, nextChat, nextReceipt, ...nextAuth].filter((t): t is number => typeof t === "number");
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
  if (body.channel === "match") {
    const { matchId, from, seq, sentAt, nonce, ciphertext } = body;
    if (typeof matchId !== "string" || !HEX64.test(matchId)) return null;
    if (from !== "a" && from !== "b") return null;
    if (!Number.isSafeInteger(seq) || (seq as number) < 1) return null;
    if (!Number.isSafeInteger(sentAt) || (sentAt as number) < 0) return null;
    if (typeof nonce !== "string" || !AEAD_NONCE.test(nonce)) return null;
    if (typeof ciphertext !== "string" || !CIPHERTEXT.test(ciphertext)) return null;
    return { channel: "match", pairIdHash: matchId, pairEpoch: 0, from, seq: seq as number, sentAt: sentAt as number, nonce, ciphertext };
  }
  const { pairIdHash, pairEpoch, from, seq, sentAt, nonce, ciphertext } = body;
  const channel = body.channel === undefined ? "live" : body.channel;
  if (channel !== "live" && channel !== "chat") return null;
  if (typeof pairIdHash !== "string" || !HEX64.test(pairIdHash)) return null;
  if (!Number.isSafeInteger(pairEpoch) || (pairEpoch as number) < 0) return null;
  if (from !== "owner" && from !== "sub") return null;
  if (!Number.isSafeInteger(seq) || (seq as number) < 1) return null;
  if (!Number.isSafeInteger(sentAt) || (sentAt as number) < 0) return null;
  if (typeof nonce !== "string" || !AEAD_NONCE.test(nonce)) return null;
  if (typeof ciphertext !== "string" || !CIPHERTEXT.test(ciphertext)) return null;
  return { channel, pairIdHash, pairEpoch: pairEpoch as number, from, seq: seq as number, sentAt: sentAt as number, nonce, ciphertext };
}
