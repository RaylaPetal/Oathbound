import type { Env } from "../env";
import { resolverFromStoredDeviceKeys, verifySignedRequest } from "../lib/auth";
import { base64UrlToBytes, bytesToBase64Url } from "../lib/base64";
import {
  CATALOG_MAILBOX_EXPIRY_SECONDS,
  CATALOG_MAILBOX_MIN_UPLOAD_INTERVAL_SECONDS,
  TIMESTAMP_TOLERANCE_SECONDS,
  nowSeconds,
} from "../lib/constants";
import { verifyEcdsaSignature, type EcPublicKeyJwk } from "../lib/crypto";
import { lookupDeviceKey } from "../lib/deviceKeys";
import { RelayError } from "../lib/errors";
import { sha256Hex, toCanonicalJson } from "../lib/json";
import { pairAtEpoch, type PairRow } from "../lib/pairs";
import { assertCircuitBreakerClosed, enforceQuota } from "../lib/quotas";
import { deleteCiphertext, getCiphertext, putCiphertext, r2KeyForMailboxSnapshot } from "../lib/r2";
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
 * collar/catalog-sync automatic sync: a per-(pairIdHash, pairEpoch) mailbox the Sub pushes encrypted catalog
 * snapshots into and the Owner collects from on its own schedule, so neither side ever has to send a chat
 * message and the two never need to be online together. Every route here is a signed POST: the path carries
 * no capability secret (pairIdHash is not treated as one), so possession is proven by the device signature
 * plus the pair row's owner/sub direction instead.
 */

const RECEIVE_KEY_ID = /^[A-Za-z0-9_-]{22}$/;

interface CatalogMailboxKeyEnvelope {
  type: "catalog-mailbox-key";
  schemaVersion: 1;
  pairIdHash: string;
  pairEpoch: number;
  receiveKeyId: string;
  ownerDeviceKeyId: string;
  receivePublicKey: EcPublicKeyJwk;
  createdAt: number;
  signature: string;
}

interface CatalogPushEnvelope {
  type: "catalog-push";
  schemaVersion: 1;
  pairIdHash: string;
  pairEpoch: number;
  receiveKeyId: string;
  snapshotId: number;
  senderDeviceKeyId: string;
  recipientDeviceKeyId: string;
  createdAt: number;
  expiresAt: number;
  algorithm: "ECDH-P256+HKDF-SHA256+AES-256-GCM";
  ciphertextDigest: string;
  ciphertextSizeBytes: number;
  nonce: string;
  senderEphemeralPublicKey: EcPublicKeyJwk;
  signature: string;
}

interface MailboxRow {
  pair_id_hash: string;
  pair_epoch: number;
  receive_key_id: string;
  receive_key_envelope: string;
  key_published_at: number;
  last_upload_at: number | null;
  snapshot_id: number | null;
  snapshot_r2_key: string | null;
  snapshot_envelope: string | null;
  snapshot_created_at: number | null;
  snapshot_expires_at: number | null;
  last_consumed_snapshot_id: number | null;
}

function isReceiveKeyId(value: unknown): value is string {
  return typeof value === "string" && RECEIVE_KEY_ID.test(value);
}

async function verifyEnvelope<T extends { signature: string }>(envelope: T, publicKeyJwk: EcPublicKeyJwk): Promise<boolean> {
  const { signature, ...unsigned } = envelope;
  return verifyEcdsaSignature(publicKeyJwk, signature, toCanonicalJson(unsigned));
}

function cleanJwk(jwk: EcPublicKeyJwk): EcPublicKeyJwk {
  return { kty: jwk.kty, crv: jwk.crv, x: jwk.x, y: jwk.y };
}

/** Resolves the live (non-revoked) pair and checks the caller is on the expected side of it. */
async function requirePairSide(env: Env, pairIdHash: string, pairEpoch: number, deviceKeyId: string, side: "owner" | "sub"): Promise<PairRow> {
  const pair = await pairAtEpoch(env, pairIdHash, pairEpoch);
  if (!pair || pair.revoked_at !== null) throw new RelayError("unauthorized");
  const expected = side === "owner" ? pair.owner_device_key_id : pair.sub_device_key_id;
  if (expected !== deviceKeyId) throw new RelayError("unauthorized");
  return pair;
}

async function loadMailbox(env: Env, pairIdHash: string, pairEpoch: number): Promise<MailboxRow | null> {
  return env.RELAY_DB.prepare(`SELECT * FROM catalog_mailboxes WHERE pair_id_hash = ?1 AND pair_epoch = ?2`)
    .bind(pairIdHash, pairEpoch)
    .first<MailboxRow>();
}

function waitingSnapshotId(mailbox: MailboxRow): number | null {
  return mailbox.snapshot_envelope && (mailbox.snapshot_expires_at ?? 0) > nowSeconds() ? mailbox.snapshot_id : null;
}

export type CatalogMailboxSummary =
  | { exists: false }
  | { exists: true; receiveKeyId: string; waitingSnapshotId: number | null; lastConsumedSnapshotId: number | null; lastUploadAt: number | null };

/** Rides on the pair status response, which both members already poll, so an unchanged catalog costs no mailbox call. */
export async function catalogMailboxSummary(env: Env, pairIdHash: string, pairEpoch: number): Promise<CatalogMailboxSummary> {
  const mailbox = await loadMailbox(env, pairIdHash, pairEpoch);
  if (!mailbox) return { exists: false };
  return {
    exists: true,
    receiveKeyId: mailbox.receive_key_id,
    waitingSnapshotId: waitingSnapshotId(mailbox),
    lastConsumedSnapshotId: mailbox.last_consumed_snapshot_id,
    lastUploadAt: mailbox.last_upload_at,
  };
}

function readPairRef(body: Record<string, unknown>): { pairIdHash: string; pairEpoch: number } {
  return { pairIdHash: requireField(body, "pairIdHash", isHex64), pairEpoch: requireField(body, "pairEpoch", isNonNegInt) };
}

/** Parses and fully verifies an Owner-signed receive key for the given pair: shape, owner identity, freshness, signature. */
async function parseOwnerKeyEnvelope(env: Env, raw: unknown, pair: PairRow): Promise<CatalogMailboxKeyEnvelope> {
  const e = asRecord(raw);
  const envelope: CatalogMailboxKeyEnvelope = {
    type: requireField(e, "type", (v): v is "catalog-mailbox-key" => v === "catalog-mailbox-key"),
    schemaVersion: requireField(e, "schemaVersion", (v): v is 1 => v === 1),
    pairIdHash: requireField(e, "pairIdHash", isHex64),
    pairEpoch: requireField(e, "pairEpoch", isNonNegInt),
    receiveKeyId: requireField(e, "receiveKeyId", isReceiveKeyId),
    ownerDeviceKeyId: requireField(e, "ownerDeviceKeyId", isHex64),
    receivePublicKey: cleanJwk(requireField(e, "receivePublicKey", isEcPublicKeyJwk)),
    createdAt: requireField(e, "createdAt", isUnixSeconds),
    signature: requireField(e, "signature", isSignature),
  };
  if (envelope.pairIdHash !== pair.pair_id_hash || envelope.pairEpoch !== pair.pair_epoch) throw new RelayError("invalid_request");
  if (envelope.ownerDeviceKeyId !== pair.owner_device_key_id) throw new RelayError("unauthorized");
  if (envelope.createdAt > nowSeconds() + TIMESTAMP_TOLERANCE_SECONDS) throw new RelayError("invalid_request");

  const ownerKey = await lookupDeviceKey(env, pair.owner_device_key_id);
  if (!ownerKey || !(await verifyEnvelope(envelope, ownerKey))) throw new RelayError("unauthorized");
  return envelope;
}

/**
 * Owner: publish (or replace) this pair's receive key. Replacing it discards any waiting snapshot, which could only
 * have been encrypted to the old key, and reopens the upload interval so the Sub's republish isn't held back.
 */
export async function publishMailboxKey(request: Request, env: Env): Promise<Response> {
  await assertCircuitBreakerClosed(env);
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "deviceMailboxOps", deviceKeyId);

  const body = asRecord(bodyJson);
  const { pairIdHash, pairEpoch } = readPairRef(body);
  const pair = await requirePairSide(env, pairIdHash, pairEpoch, deviceKeyId, "owner");
  const envelope = await parseOwnerKeyEnvelope(env, body.key, pair);

  const existing = await loadMailbox(env, pairIdHash, pairEpoch);
  await env.RELAY_DB.prepare(
    `INSERT INTO catalog_mailboxes (pair_id_hash, pair_epoch, receive_key_id, receive_key_envelope, key_published_at)
     VALUES (?1, ?2, ?3, ?4, ?5)
     ON CONFLICT (pair_id_hash, pair_epoch) DO UPDATE SET
       receive_key_id = excluded.receive_key_id,
       receive_key_envelope = excluded.receive_key_envelope,
       key_published_at = excluded.key_published_at,
       last_upload_at = NULL,
       snapshot_id = NULL, snapshot_r2_key = NULL, snapshot_envelope = NULL,
       snapshot_created_at = NULL, snapshot_expires_at = NULL`,
  )
    .bind(pairIdHash, pairEpoch, envelope.receiveKeyId, toCanonicalJson(envelope), nowSeconds())
    .run();
  if (existing?.snapshot_r2_key) await deleteCiphertext(env, existing.snapshot_r2_key);

  return Response.json(envelope);
}

/**
 * Sub: fetch the Owner's current signed receive key (the Sub re-verifies the Owner's signature itself), plus
 * the delivery receipt - which snapshot is waiting and which was last consumed - so the Sub can tell when a
 * push it made never reached the Owner (key reset, or expired unread) and publish it again.
 */
export async function fetchMailboxKey(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "deviceMailboxOps", deviceKeyId);

  const { pairIdHash, pairEpoch } = readPairRef(asRecord(bodyJson));
  await requirePairSide(env, pairIdHash, pairEpoch, deviceKeyId, "sub");
  const mailbox = await loadMailbox(env, pairIdHash, pairEpoch);
  if (!mailbox) throw new RelayError("not_found");
  return Response.json({
    key: JSON.parse(mailbox.receive_key_envelope),
    waitingSnapshotId: waitingSnapshotId(mailbox),
    lastConsumedSnapshotId: mailbox.last_consumed_snapshot_id,
  });
}

/** Sub: leave an encrypted snapshot in the mailbox, replacing any earlier one. */
export async function uploadMailboxSnapshot(request: Request, env: Env): Promise<Response> {
  await assertCircuitBreakerClosed(env);
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));

  const body = asRecord(bodyJson);
  const envelopeField = body.envelope;
  if (typeof envelopeField !== "object" || envelopeField === null) throw new RelayError("invalid_request");
  const e = asRecord(envelopeField);
  const envelope: CatalogPushEnvelope = {
    type: requireField(e, "type", (v): v is "catalog-push" => v === "catalog-push"),
    schemaVersion: requireField(e, "schemaVersion", (v): v is 1 => v === 1),
    pairIdHash: requireField(e, "pairIdHash", isHex64),
    pairEpoch: requireField(e, "pairEpoch", isNonNegInt),
    receiveKeyId: requireField(e, "receiveKeyId", isReceiveKeyId),
    snapshotId: requireField(e, "snapshotId", isNonNegInt),
    senderDeviceKeyId: requireField(e, "senderDeviceKeyId", isHex64),
    recipientDeviceKeyId: requireField(e, "recipientDeviceKeyId", isHex64),
    createdAt: requireField(e, "createdAt", isUnixSeconds),
    expiresAt: requireField(e, "expiresAt", isUnixSeconds),
    algorithm: requireField(e, "algorithm", (v): v is "ECDH-P256+HKDF-SHA256+AES-256-GCM" => v === "ECDH-P256+HKDF-SHA256+AES-256-GCM"),
    ciphertextDigest: requireField(e, "ciphertextDigest", isHex64),
    ciphertextSizeBytes: requireField(e, "ciphertextSizeBytes", isNonNegInt),
    nonce: requireField(e, "nonce", isAeadNonce),
    senderEphemeralPublicKey: cleanJwk(requireField(e, "senderEphemeralPublicKey", isEcPublicKeyJwk)),
    signature: requireField(e, "signature", isSignature),
  };

  const now = nowSeconds();
  const pair = await requirePairSide(env, envelope.pairIdHash, envelope.pairEpoch, deviceKeyId, "sub");
  if (envelope.senderDeviceKeyId !== deviceKeyId || envelope.recipientDeviceKeyId !== pair.owner_device_key_id) {
    throw new RelayError("unauthorized");
  }
  // Server-enforced retention cap: a Sub cannot make its snapshot outlive the mailbox retention window.
  if (envelope.expiresAt <= now || envelope.expiresAt - envelope.createdAt > CATALOG_MAILBOX_EXPIRY_SECONDS) {
    throw new RelayError("invalid_request");
  }
  if (envelope.createdAt > now + TIMESTAMP_TOLERANCE_SECONDS) throw new RelayError("invalid_request");

  const subKey = await lookupDeviceKey(env, deviceKeyId);
  if (!subKey || !(await verifyEnvelope(envelope, subKey))) throw new RelayError("unauthorized");

  const ciphertextBase64Url = body.ciphertextBase64Url;
  if (typeof ciphertextBase64Url !== "string") throw new RelayError("invalid_request");
  const ciphertextBytes = base64UrlToBytes(ciphertextBase64Url);
  if (ciphertextBytes.byteLength === 0 || ciphertextBytes.byteLength !== envelope.ciphertextSizeBytes) throw new RelayError("invalid_request");
  if ((await sha256Hex(ciphertextBytes.buffer as ArrayBuffer)) !== envelope.ciphertextDigest) throw new RelayError("invalid_request");

  const mailbox = await loadMailbox(env, envelope.pairIdHash, envelope.pairEpoch);
  if (!mailbox) throw new RelayError("not_found"); // The Owner has never published a receive key.
  // "expired" specifically means "encrypted to a receive key the Owner has since rotated away from" - the
  // Sub's cue to refetch the key and publish again.
  if (mailbox.receive_key_id !== envelope.receiveKeyId) throw new RelayError("expired");

  // Per-pair minimum interval, claimed atomically so two racing uploads can't both slip under it.
  const intervalClaim = await env.RELAY_DB.prepare(
    `UPDATE catalog_mailboxes SET last_upload_at = ?1
     WHERE pair_id_hash = ?2 AND pair_epoch = ?3 AND receive_key_id = ?4
       AND (last_upload_at IS NULL OR last_upload_at <= ?1 - ?5)`,
  )
    .bind(now, envelope.pairIdHash, envelope.pairEpoch, envelope.receiveKeyId, CATALOG_MAILBOX_MIN_UPLOAD_INTERVAL_SECONDS)
    .run();
  if ((intervalClaim.meta.changes ?? 0) === 0) {
    const current = await loadMailbox(env, envelope.pairIdHash, envelope.pairEpoch);
    if (!current || current.receive_key_id !== envelope.receiveKeyId) throw new RelayError("expired");
    const waited = now - (current.last_upload_at ?? 0);
    throw new RelayError("rate_limited", Math.max(CATALOG_MAILBOX_MIN_UPLOAD_INTERVAL_SECONDS - waited, 1));
  }

  await enforceQuota(env, "catalogUploadBytes", deviceKeyId, ciphertextBytes.byteLength);

  // Shares the pair epoch's monotonic snapshot sequence with catalog-response (collar/catalog-sync), so a
  // late manual response and a push can never overwrite each other out of order.
  await env.RELAY_DB.prepare(
    `INSERT INTO pair_cooldowns (pair_id_hash, pair_epoch, last_accepted_sync_at, last_snapshot_id, active_request_id_hash)
     VALUES (?1, ?2, 0, 0, NULL) ON CONFLICT (pair_id_hash, pair_epoch) DO NOTHING`,
  )
    .bind(envelope.pairIdHash, envelope.pairEpoch)
    .run();
  const sequenceGuard = await env.RELAY_DB.prepare(
    `UPDATE pair_cooldowns SET last_snapshot_id = ?1 WHERE pair_id_hash = ?2 AND pair_epoch = ?3 AND last_snapshot_id < ?1`,
  )
    .bind(envelope.snapshotId, envelope.pairIdHash, envelope.pairEpoch)
    .run();
  if ((sequenceGuard.meta.changes ?? 0) === 0) throw new RelayError("invalid_request");

  const r2Key = r2KeyForMailboxSnapshot(envelope.pairIdHash, envelope.pairEpoch, envelope.snapshotId);
  await putCiphertext(env, r2Key, ciphertextBytes);

  // Conditional on the key still being current: if the Owner rotated between our check and now, this
  // snapshot is undecryptable for them - drop it and tell the Sub to republish rather than park it.
  const stored = await env.RELAY_DB.prepare(
    `UPDATE catalog_mailboxes SET snapshot_id = ?1, snapshot_r2_key = ?2, snapshot_envelope = ?3,
       snapshot_created_at = ?4, snapshot_expires_at = ?5
     WHERE pair_id_hash = ?6 AND pair_epoch = ?7 AND receive_key_id = ?8`,
  )
    .bind(envelope.snapshotId, r2Key, toCanonicalJson(envelope), envelope.createdAt, envelope.expiresAt, envelope.pairIdHash, envelope.pairEpoch, envelope.receiveKeyId)
    .run();
  if ((stored.meta.changes ?? 0) === 0) {
    await deleteCiphertext(env, r2Key);
    throw new RelayError("expired");
  }
  if (mailbox.snapshot_r2_key && mailbox.snapshot_r2_key !== r2Key) await deleteCiphertext(env, mailbox.snapshot_r2_key);

  return Response.json(envelope);
}

/** Owner: cheap check of what's waiting - drives both the auto-import and the "is my copy current" status. */
export async function mailboxStatus(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "deviceMailboxOps", deviceKeyId);

  const { pairIdHash, pairEpoch } = readPairRef(asRecord(bodyJson));
  await requirePairSide(env, pairIdHash, pairEpoch, deviceKeyId, "owner");
  const mailbox = await loadMailbox(env, pairIdHash, pairEpoch);
  const hasSnapshot = !!mailbox?.snapshot_envelope && (mailbox.snapshot_expires_at ?? 0) > nowSeconds();

  return Response.json({
    type: "catalog-mailbox-status",
    schemaVersion: 1,
    hasKey: !!mailbox,
    receiveKeyId: mailbox?.receive_key_id ?? null,
    hasSnapshot,
    snapshotId: hasSnapshot ? mailbox!.snapshot_id : null,
    snapshotCreatedAt: hasSnapshot ? mailbox!.snapshot_created_at : null,
    lastUploadAt: mailbox?.last_upload_at ?? null,
  });
}

/**
 * Owner: one-use retrieval of the waiting snapshot, atomically rotating the mailbox to the next receive key
 * carried in the body - after this, nothing can still be encrypted to the key that decrypts this snapshot.
 */
export async function consumeMailboxSnapshot(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "deviceMailboxOps", deviceKeyId);

  const body = asRecord(bodyJson);
  const { pairIdHash, pairEpoch } = readPairRef(body);
  const expectedSnapshotId = requireField(body, "snapshotId", isNonNegInt);
  const pair = await requirePairSide(env, pairIdHash, pairEpoch, deviceKeyId, "owner");
  const nextKey = await parseOwnerKeyEnvelope(env, body.nextKey, pair);

  const mailbox = await loadMailbox(env, pairIdHash, pairEpoch);
  if (!mailbox?.snapshot_envelope || !mailbox.snapshot_r2_key || mailbox.snapshot_id !== expectedSnapshotId) throw new RelayError("not_found");
  if ((mailbox.snapshot_expires_at ?? 0) <= nowSeconds()) throw new RelayError("not_found");

  const ciphertextBytes = await getCiphertext(env, mailbox.snapshot_r2_key);
  if (!ciphertextBytes) throw new RelayError("not_found");

  // Claim by exact snapshot identity: a concurrent upload replacing it, or a concurrent consume, makes this
  // match nothing, so each snapshot is handed out at most once and never paired with the wrong ciphertext.
  const claim = await env.RELAY_DB.prepare(
    `UPDATE catalog_mailboxes SET
       receive_key_id = ?1, receive_key_envelope = ?2, key_published_at = ?3,
       snapshot_id = NULL, snapshot_r2_key = NULL, snapshot_envelope = NULL,
       snapshot_created_at = NULL, snapshot_expires_at = NULL, last_consumed_snapshot_id = ?6
     WHERE pair_id_hash = ?4 AND pair_epoch = ?5 AND snapshot_id = ?6 AND snapshot_r2_key = ?7`,
  )
    .bind(nextKey.receiveKeyId, toCanonicalJson(nextKey), nowSeconds(), pairIdHash, pairEpoch, mailbox.snapshot_id, mailbox.snapshot_r2_key)
    .run();
  if ((claim.meta.changes ?? 0) === 0) throw new RelayError("not_found");
  await deleteCiphertext(env, mailbox.snapshot_r2_key);

  return Response.json({
    envelope: JSON.parse(mailbox.snapshot_envelope),
    ciphertextBase64Url: bytesToBase64Url(ciphertextBytes),
  });
}
