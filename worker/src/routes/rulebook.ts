import type { Env } from "../env";
import { resolverFromStoredDeviceKeys, verifySignedRequest } from "../lib/auth";
import { base64UrlToBytes, bytesToBase64Url } from "../lib/base64";
import {
  RULEBOOK_CIPHERTEXT_MAX_BYTES,
  RULEBOOK_MAILBOX_EXPIRY_SECONDS,
  RULEBOOK_MIN_UPLOAD_INTERVAL_SECONDS,
  RULEBOOK_REPORT_CIPHERTEXT_MAX_BYTES,
  RULEBOOK_REPORT_MIN_UPLOAD_INTERVAL_SECONDS,
  TIMESTAMP_TOLERANCE_SECONDS,
  nowSeconds,
} from "../lib/constants";
import { verifyEcdsaSignature, type EcPublicKeyJwk } from "../lib/crypto";
import { lookupDeviceKey } from "../lib/deviceKeys";
import { RelayError } from "../lib/errors";
import { sha256Hex, toCanonicalJson } from "../lib/json";
import { pairAtEpoch, type PairRow } from "../lib/pairs";
import { assertCircuitBreakerClosed, enforceQuota } from "../lib/quotas";
import { deleteCiphertext, getCiphertext, putCiphertext, r2KeyForRulebookItem } from "../lib/r2";
import {
  asRecord,
  isAeadNonce,
  isEcPublicKeyJwk,
  isHex64,
  isNonNegInt,
  isSignature,
  isUnixSeconds,
  requireField,
} from "../lib/validate";

/**
 * collar/rulebook: a per-(pairIdHash, pairEpoch, channel) mailbox in each direction. Channel "rulebook" carries
 * Owner-authored rulebooks to the Sub, channel "report" carries the Sub's activity reports to the Owner. Each
 * direction works like the catalog mailbox: the recipient publishes a signed receive key, the sender leaves one
 * encrypted item, the recipient consumes it while rotating to its next key. Every route is a signed POST and
 * possession is proven by the device signature plus the pair row's owner/sub direction.
 */

const RECEIVE_KEY_ID = /^[A-Za-z0-9_-]{22}$/;
const ALGORITHM = "ECDH-P256+HKDF-SHA256+AES-256-GCM";

export type RulebookChannel = "rulebook" | "report";

interface ChannelRules {
  itemType: "rulebook-push" | "rulebook-report";
  recipient: "owner" | "sub";
  sender: "owner" | "sub";
  maxCiphertextBytes: number;
  minUploadIntervalSeconds: number;
}

const CHANNELS: Record<RulebookChannel, ChannelRules> = {
  rulebook: {
    itemType: "rulebook-push",
    recipient: "sub",
    sender: "owner",
    maxCiphertextBytes: RULEBOOK_CIPHERTEXT_MAX_BYTES,
    minUploadIntervalSeconds: RULEBOOK_MIN_UPLOAD_INTERVAL_SECONDS,
  },
  report: {
    itemType: "rulebook-report",
    recipient: "owner",
    sender: "sub",
    maxCiphertextBytes: RULEBOOK_REPORT_CIPHERTEXT_MAX_BYTES,
    minUploadIntervalSeconds: RULEBOOK_REPORT_MIN_UPLOAD_INTERVAL_SECONDS,
  },
};

interface RulebookKeyEnvelope {
  type: "rulebook-key";
  schemaVersion: 1;
  channel: RulebookChannel;
  pairIdHash: string;
  pairEpoch: number;
  receiveKeyId: string;
  recipientDeviceKeyId: string;
  receivePublicKey: EcPublicKeyJwk;
  createdAt: number;
  signature: string;
}

interface RulebookItemEnvelope {
  type: "rulebook-push" | "rulebook-report";
  schemaVersion: 1;
  pairIdHash: string;
  pairEpoch: number;
  receiveKeyId: string;
  sequence: number;
  senderDeviceKeyId: string;
  recipientDeviceKeyId: string;
  createdAt: number;
  expiresAt: number;
  algorithm: typeof ALGORITHM;
  ciphertextDigest: string;
  ciphertextSizeBytes: number;
  nonce: string;
  senderEphemeralPublicKey: EcPublicKeyJwk;
  signature: string;
}

interface RulebookMailboxRow {
  pair_id_hash: string;
  pair_epoch: number;
  channel: RulebookChannel;
  receive_key_id: string;
  receive_key_envelope: string;
  key_published_at: number;
  last_upload_at: number | null;
  last_sequence: number;
  item_sequence: number | null;
  item_r2_key: string | null;
  item_envelope: string | null;
  item_created_at: number | null;
  item_expires_at: number | null;
  last_consumed_sequence: number | null;
}

function isReceiveKeyId(value: unknown): value is string {
  return typeof value === "string" && RECEIVE_KEY_ID.test(value);
}

function isChannel(value: unknown): value is RulebookChannel {
  return value === "rulebook" || value === "report";
}

async function verifyEnvelope<T extends { signature: string }>(envelope: T, publicKeyJwk: EcPublicKeyJwk): Promise<boolean> {
  const { signature, ...unsigned } = envelope;
  return verifyEcdsaSignature(publicKeyJwk, signature, toCanonicalJson(unsigned));
}

function cleanJwk(jwk: EcPublicKeyJwk): EcPublicKeyJwk {
  return { kty: jwk.kty, crv: jwk.crv, x: jwk.x, y: jwk.y };
}

function sideDeviceKeyId(pair: PairRow, side: "owner" | "sub"): string {
  return side === "owner" ? pair.owner_device_key_id : pair.sub_device_key_id;
}

/** Resolves the live (non-revoked) pair and checks the caller is on the expected side of it. */
async function requirePairSide(env: Env, pairIdHash: string, pairEpoch: number, deviceKeyId: string, side: "owner" | "sub"): Promise<PairRow> {
  const pair = await pairAtEpoch(env, pairIdHash, pairEpoch);
  if (!pair || pair.revoked_at !== null) throw new RelayError("unauthorized");
  if (sideDeviceKeyId(pair, side) !== deviceKeyId) throw new RelayError("unauthorized");
  return pair;
}

async function loadMailbox(env: Env, pairIdHash: string, pairEpoch: number, channel: RulebookChannel): Promise<RulebookMailboxRow | null> {
  return env.RELAY_DB.prepare(`SELECT * FROM rulebook_mailboxes WHERE pair_id_hash = ?1 AND pair_epoch = ?2 AND channel = ?3`)
    .bind(pairIdHash, pairEpoch, channel)
    .first<RulebookMailboxRow>();
}

function waitingSequence(mailbox: RulebookMailboxRow): number | null {
  return mailbox.item_envelope && (mailbox.item_expires_at ?? 0) > nowSeconds() ? mailbox.item_sequence : null;
}

export type RulebookChannelSummary =
  | { exists: false }
  | { exists: true; receiveKeyId: string; waitingSequence: number | null; lastConsumedSequence: number | null; lastUploadAt: number | null };

export interface RulebookMailboxSummary {
  rulebook: RulebookChannelSummary;
  report: RulebookChannelSummary;
}

/** Rides on the pair status response, so neither side needs a separate poll to learn something is waiting. */
export async function rulebookMailboxSummary(env: Env, pairIdHash: string, pairEpoch: number): Promise<RulebookMailboxSummary> {
  const rows = await env.RELAY_DB.prepare(`SELECT * FROM rulebook_mailboxes WHERE pair_id_hash = ?1 AND pair_epoch = ?2`)
    .bind(pairIdHash, pairEpoch)
    .all<RulebookMailboxRow>();
  const summarize = (channel: RulebookChannel): RulebookChannelSummary => {
    const row = rows.results.find((r) => r.channel === channel);
    if (!row) return { exists: false };
    return {
      exists: true,
      receiveKeyId: row.receive_key_id,
      waitingSequence: waitingSequence(row),
      lastConsumedSequence: row.last_consumed_sequence,
      lastUploadAt: row.last_upload_at,
    };
  };
  return { rulebook: summarize("rulebook"), report: summarize("report") };
}

function readMailboxRef(body: Record<string, unknown>): { pairIdHash: string; pairEpoch: number; channel: RulebookChannel } {
  return {
    pairIdHash: requireField(body, "pairIdHash", isHex64),
    pairEpoch: requireField(body, "pairEpoch", isNonNegInt),
    channel: requireField(body, "channel", isChannel),
  };
}

/** Parses and fully verifies a recipient-signed receive key for the given pair and channel. */
async function parseKeyEnvelope(env: Env, raw: unknown, pair: PairRow, channel: RulebookChannel): Promise<RulebookKeyEnvelope> {
  const e = asRecord(raw);
  const envelope: RulebookKeyEnvelope = {
    type: requireField(e, "type", (v): v is "rulebook-key" => v === "rulebook-key"),
    schemaVersion: requireField(e, "schemaVersion", (v): v is 1 => v === 1),
    channel: requireField(e, "channel", isChannel),
    pairIdHash: requireField(e, "pairIdHash", isHex64),
    pairEpoch: requireField(e, "pairEpoch", isNonNegInt),
    receiveKeyId: requireField(e, "receiveKeyId", isReceiveKeyId),
    recipientDeviceKeyId: requireField(e, "recipientDeviceKeyId", isHex64),
    receivePublicKey: cleanJwk(requireField(e, "receivePublicKey", isEcPublicKeyJwk)),
    createdAt: requireField(e, "createdAt", isUnixSeconds),
    signature: requireField(e, "signature", isSignature),
  };
  if (envelope.pairIdHash !== pair.pair_id_hash || envelope.pairEpoch !== pair.pair_epoch || envelope.channel !== channel) {
    throw new RelayError("invalid_request");
  }
  const recipientId = sideDeviceKeyId(pair, CHANNELS[channel].recipient);
  if (envelope.recipientDeviceKeyId !== recipientId) throw new RelayError("unauthorized");
  if (envelope.createdAt > nowSeconds() + TIMESTAMP_TOLERANCE_SECONDS) throw new RelayError("invalid_request");

  const recipientKey = await lookupDeviceKey(env, recipientId);
  if (!recipientKey || !(await verifyEnvelope(envelope, recipientKey))) throw new RelayError("unauthorized");
  return envelope;
}

/**
 * Recipient: publish (or replace) this channel's receive key. Replacing it discards any waiting item, which
 * could only have been encrypted to the old key, and reopens the upload interval so the sender's republish
 * isn't held back. last_sequence is kept, so a replay of an older item still can't land afterwards.
 */
export async function publishRulebookKey(request: Request, env: Env): Promise<Response> {
  await assertCircuitBreakerClosed(env);
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "deviceRulebookOps", deviceKeyId);

  const body = asRecord(bodyJson);
  const { pairIdHash, pairEpoch, channel } = readMailboxRef(body);
  const pair = await requirePairSide(env, pairIdHash, pairEpoch, deviceKeyId, CHANNELS[channel].recipient);
  const envelope = await parseKeyEnvelope(env, body.key, pair, channel);

  const existing = await loadMailbox(env, pairIdHash, pairEpoch, channel);
  await env.RELAY_DB.prepare(
    `INSERT INTO rulebook_mailboxes (pair_id_hash, pair_epoch, channel, receive_key_id, receive_key_envelope, key_published_at)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6)
     ON CONFLICT (pair_id_hash, pair_epoch, channel) DO UPDATE SET
       receive_key_id = excluded.receive_key_id,
       receive_key_envelope = excluded.receive_key_envelope,
       key_published_at = excluded.key_published_at,
       last_upload_at = NULL,
       item_sequence = NULL, item_r2_key = NULL, item_envelope = NULL,
       item_created_at = NULL, item_expires_at = NULL`,
  )
    .bind(pairIdHash, pairEpoch, channel, envelope.receiveKeyId, toCanonicalJson(envelope), nowSeconds())
    .run();
  if (existing?.item_r2_key) await deleteCiphertext(env, existing.item_r2_key);

  return Response.json(envelope);
}

/**
 * Sender: fetch the recipient's current signed receive key (the sender re-verifies the signature itself), plus
 * the delivery receipt, so it can tell when an item never reached the recipient and send it again.
 */
export async function fetchRulebookKey(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "deviceRulebookOps", deviceKeyId);

  const { pairIdHash, pairEpoch, channel } = readMailboxRef(asRecord(bodyJson));
  await requirePairSide(env, pairIdHash, pairEpoch, deviceKeyId, CHANNELS[channel].sender);
  const mailbox = await loadMailbox(env, pairIdHash, pairEpoch, channel);
  if (!mailbox) throw new RelayError("not_found");
  return Response.json({
    key: JSON.parse(mailbox.receive_key_envelope),
    waitingSequence: waitingSequence(mailbox),
    lastConsumedSequence: mailbox.last_consumed_sequence,
    lastSequence: mailbox.last_sequence,
  });
}

/** Sender: leave an encrypted item on the channel, replacing any earlier one. */
export async function uploadRulebookItem(request: Request, env: Env): Promise<Response> {
  await assertCircuitBreakerClosed(env);
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));

  const body = asRecord(bodyJson);
  const envelopeField = body.envelope;
  if (typeof envelopeField !== "object" || envelopeField === null) throw new RelayError("invalid_request");
  const e = asRecord(envelopeField);
  const type = requireField(e, "type", (v): v is RulebookItemEnvelope["type"] => v === "rulebook-push" || v === "rulebook-report");
  const channel: RulebookChannel = type === "rulebook-push" ? "rulebook" : "report";
  const rules = CHANNELS[channel];
  const envelope: RulebookItemEnvelope = {
    type,
    schemaVersion: requireField(e, "schemaVersion", (v): v is 1 => v === 1),
    pairIdHash: requireField(e, "pairIdHash", isHex64),
    pairEpoch: requireField(e, "pairEpoch", isNonNegInt),
    receiveKeyId: requireField(e, "receiveKeyId", isReceiveKeyId),
    sequence: requireField(e, "sequence", isNonNegInt),
    senderDeviceKeyId: requireField(e, "senderDeviceKeyId", isHex64),
    recipientDeviceKeyId: requireField(e, "recipientDeviceKeyId", isHex64),
    createdAt: requireField(e, "createdAt", isUnixSeconds),
    expiresAt: requireField(e, "expiresAt", isUnixSeconds),
    algorithm: requireField(e, "algorithm", (v): v is typeof ALGORITHM => v === ALGORITHM),
    ciphertextDigest: requireField(e, "ciphertextDigest", isHex64),
    ciphertextSizeBytes: requireField(e, "ciphertextSizeBytes", isNonNegInt),
    nonce: requireField(e, "nonce", isAeadNonce),
    senderEphemeralPublicKey: cleanJwk(requireField(e, "senderEphemeralPublicKey", isEcPublicKeyJwk)),
    signature: requireField(e, "signature", isSignature),
  };

  const now = nowSeconds();
  const pair = await requirePairSide(env, envelope.pairIdHash, envelope.pairEpoch, deviceKeyId, rules.sender);
  if (envelope.senderDeviceKeyId !== deviceKeyId || envelope.recipientDeviceKeyId !== sideDeviceKeyId(pair, rules.recipient)) {
    throw new RelayError("unauthorized");
  }
  if (envelope.expiresAt <= now || envelope.expiresAt - envelope.createdAt > RULEBOOK_MAILBOX_EXPIRY_SECONDS) {
    throw new RelayError("invalid_request");
  }
  if (envelope.createdAt > now + TIMESTAMP_TOLERANCE_SECONDS) throw new RelayError("invalid_request");
  if (envelope.ciphertextSizeBytes > rules.maxCiphertextBytes) throw new RelayError("payload_too_large");

  const senderKey = await lookupDeviceKey(env, deviceKeyId);
  if (!senderKey || !(await verifyEnvelope(envelope, senderKey))) throw new RelayError("unauthorized");

  const ciphertextBase64Url = body.ciphertextBase64Url;
  if (typeof ciphertextBase64Url !== "string") throw new RelayError("invalid_request");
  const ciphertextBytes = base64UrlToBytes(ciphertextBase64Url);
  if (ciphertextBytes.byteLength === 0 || ciphertextBytes.byteLength !== envelope.ciphertextSizeBytes) throw new RelayError("invalid_request");
  if ((await sha256Hex(ciphertextBytes.buffer as ArrayBuffer)) !== envelope.ciphertextDigest) throw new RelayError("invalid_request");

  const mailbox = await loadMailbox(env, envelope.pairIdHash, envelope.pairEpoch, channel);
  if (!mailbox) throw new RelayError("not_found"); // The recipient has never published a receive key.
  // "expired" means "encrypted to a key the recipient has since rotated away from" - refetch it and resend.
  if (mailbox.receive_key_id !== envelope.receiveKeyId) throw new RelayError("expired");
  if (envelope.sequence <= mailbox.last_sequence) throw new RelayError("invalid_request");

  // Counted before the claim, so a quota rejection can't leave the interval claimed for an upload that never landed.
  await enforceQuota(env, "rulebookUploadBytes", deviceKeyId, ciphertextBytes.byteLength);

  // Interval and sequence claimed in one conditional update, so two racing uploads can't both slip through.
  const claim = await env.RELAY_DB.prepare(
    `UPDATE rulebook_mailboxes SET last_upload_at = ?1, last_sequence = ?2
     WHERE pair_id_hash = ?3 AND pair_epoch = ?4 AND channel = ?5 AND receive_key_id = ?6
       AND last_sequence < ?2
       AND (last_upload_at IS NULL OR last_upload_at <= ?1 - ?7)`,
  )
    .bind(now, envelope.sequence, envelope.pairIdHash, envelope.pairEpoch, channel, envelope.receiveKeyId, rules.minUploadIntervalSeconds)
    .run();
  if ((claim.meta.changes ?? 0) === 0) {
    const current = await loadMailbox(env, envelope.pairIdHash, envelope.pairEpoch, channel);
    if (!current || current.receive_key_id !== envelope.receiveKeyId) throw new RelayError("expired");
    if (envelope.sequence <= current.last_sequence) throw new RelayError("invalid_request");
    const waited = now - (current.last_upload_at ?? 0);
    throw new RelayError("rate_limited", Math.max(rules.minUploadIntervalSeconds - waited, 1));
  }

  // Nothing was stored, so the claim is handed back and the sender can retry at once. Conditional on no later
  // upload having claimed the row since.
  const releaseClaim = () =>
    env.RELAY_DB.prepare(
      `UPDATE rulebook_mailboxes SET last_upload_at = ?1, last_sequence = ?2
       WHERE pair_id_hash = ?3 AND pair_epoch = ?4 AND channel = ?5 AND last_sequence = ?6 AND last_upload_at = ?7`,
    )
      .bind(mailbox.last_upload_at, mailbox.last_sequence, envelope.pairIdHash, envelope.pairEpoch, channel, envelope.sequence, now)
      .run();

  const r2Key = r2KeyForRulebookItem(envelope.pairIdHash, envelope.pairEpoch, channel, envelope.sequence);
  try {
    await putCiphertext(env, r2Key, ciphertextBytes);
  } catch (error) {
    await releaseClaim();
    throw error;
  }

  // Conditional on the key still being current: a rotation between our check and now makes this item
  // undecryptable for the recipient, so drop it and tell the sender to resend.
  const stored = await env.RELAY_DB.prepare(
    `UPDATE rulebook_mailboxes SET item_sequence = ?1, item_r2_key = ?2, item_envelope = ?3,
       item_created_at = ?4, item_expires_at = ?5
     WHERE pair_id_hash = ?6 AND pair_epoch = ?7 AND channel = ?8 AND receive_key_id = ?9`,
  )
    .bind(envelope.sequence, r2Key, toCanonicalJson(envelope), envelope.createdAt, envelope.expiresAt, envelope.pairIdHash, envelope.pairEpoch, channel, envelope.receiveKeyId)
    .run();
  if ((stored.meta.changes ?? 0) === 0) {
    await deleteCiphertext(env, r2Key);
    await releaseClaim();
    throw new RelayError("expired");
  }
  if (mailbox.item_r2_key && mailbox.item_r2_key !== r2Key) await deleteCiphertext(env, mailbox.item_r2_key);

  return Response.json(envelope);
}

/**
 * Recipient: one-use retrieval of the waiting item, atomically rotating the channel to the next receive key
 * carried in the body - after this, nothing can still be encrypted to the key that decrypts this item.
 */
export async function consumeRulebookItem(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "deviceRulebookOps", deviceKeyId);

  const body = asRecord(bodyJson);
  const { pairIdHash, pairEpoch, channel } = readMailboxRef(body);
  const expectedSequence = requireField(body, "sequence", isNonNegInt);
  const pair = await requirePairSide(env, pairIdHash, pairEpoch, deviceKeyId, CHANNELS[channel].recipient);
  const nextKey = await parseKeyEnvelope(env, body.nextKey, pair, channel);

  const mailbox = await loadMailbox(env, pairIdHash, pairEpoch, channel);
  if (!mailbox?.item_envelope || !mailbox.item_r2_key || mailbox.item_sequence !== expectedSequence) throw new RelayError("not_found");
  if ((mailbox.item_expires_at ?? 0) <= nowSeconds()) throw new RelayError("not_found");

  const ciphertextBytes = await getCiphertext(env, mailbox.item_r2_key);
  if (!ciphertextBytes) throw new RelayError("not_found");

  // Claim by exact item identity, so each item is handed out at most once and never with the wrong ciphertext.
  const claim = await env.RELAY_DB.prepare(
    `UPDATE rulebook_mailboxes SET
       receive_key_id = ?1, receive_key_envelope = ?2, key_published_at = ?3,
       item_sequence = NULL, item_r2_key = NULL, item_envelope = NULL,
       item_created_at = NULL, item_expires_at = NULL, last_consumed_sequence = ?7
     WHERE pair_id_hash = ?4 AND pair_epoch = ?5 AND channel = ?6 AND item_sequence = ?7 AND item_r2_key = ?8`,
  )
    .bind(nextKey.receiveKeyId, toCanonicalJson(nextKey), nowSeconds(), pairIdHash, pairEpoch, channel, mailbox.item_sequence, mailbox.item_r2_key)
    .run();
  if ((claim.meta.changes ?? 0) === 0) throw new RelayError("not_found");
  await deleteCiphertext(env, mailbox.item_r2_key);

  return Response.json({
    envelope: JSON.parse(mailbox.item_envelope),
    ciphertextBase64Url: bytesToBase64Url(ciphertextBytes),
  });
}
