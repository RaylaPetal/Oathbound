import type { Env } from "../env";
import { READ_ONLY, resolverFromStoredDeviceKeys, verifySignedRequest } from "../lib/auth";
import { base64UrlToBytes, bytesToBase64Url } from "../lib/base64";
import {
  CHAT_IMAGE_MAX_CIPHERTEXT_BYTES,
  CHAT_IMAGE_TTL_SECONDS,
  CHAT_IMAGES_PER_PAIR_MAX,
  CHAT_IMAGES_PER_PAIR_MAX_BYTES,
  nowSeconds,
} from "../lib/constants";
import { RelayError } from "../lib/errors";
import { pairAtEpoch } from "../lib/pairs";
import { assertCircuitBreakerClosed, enforceQuota } from "../lib/quotas";
import { getCiphertext, putCiphertext, r2KeyForChatImage } from "../lib/r2";
import { asRecord, isHex64, isNonNegInt, requireField } from "../lib/validate";

/**
 * Chat pictures: an encrypted blob either member of a pairing uploads and the other fetches. The key travels only in
 * the sealed chat message, so the relay sees an opaque id, its pairing and its size.
 */

const IMAGE_ID_PATTERN = /^[A-Za-z0-9_-]{22}$/;
/** 12-byte nonce plus a 16-byte tag around at least one byte. */
const MIN_BLOB_BYTES = 29;

function readRef(body: Record<string, unknown>, type: string): { pairIdHash: string; pairEpoch: number; imageId: string } {
  if (body.type !== type || body.schemaVersion !== 1) throw new RelayError("invalid_request");
  return {
    pairIdHash: requireField(body, "pairIdHash", isHex64),
    pairEpoch: requireField(body, "pairEpoch", isNonNegInt),
    imageId: requireField(body, "imageId", (v): v is string => typeof v === "string" && IMAGE_ID_PATTERN.test(v)),
  };
}

async function requireMember(env: Env, pairIdHash: string, pairEpoch: number, deviceKeyId: string): Promise<void> {
  const pair = await pairAtEpoch(env, pairIdHash, pairEpoch);
  if (!pair || pair.revoked_at !== null) throw new RelayError("unauthorized");
  if (pair.owner_device_key_id !== deviceKeyId && pair.sub_device_key_id !== deviceKeyId) throw new RelayError("unauthorized");
}

export async function uploadChatImage(request: Request, env: Env): Promise<Response> {
  await assertCircuitBreakerClosed(env);
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  const body = asRecord(bodyJson);
  const { pairIdHash, pairEpoch, imageId } = readRef(body, "chat-image-upload");
  if (typeof body.ciphertextBase64Url !== "string") throw new RelayError("invalid_request");
  const bytes = base64UrlToBytes(body.ciphertextBase64Url);
  if (bytes.byteLength < MIN_BLOB_BYTES) throw new RelayError("invalid_request");
  if (bytes.byteLength > CHAT_IMAGE_MAX_CIPHERTEXT_BYTES) throw new RelayError("payload_too_large");
  await requireMember(env, pairIdHash, pairEpoch, deviceKeyId);

  const now = nowSeconds();
  const held = await env.RELAY_DB.prepare(
    `SELECT COUNT(*) AS n, COALESCE(SUM(size_bytes), 0) AS total FROM chat_images WHERE pair_id_hash = ?1 AND expires_at > ?2`,
  )
    .bind(pairIdHash, now)
    .first<{ n: number; total: number }>();
  if ((held?.n ?? 0) + 1 > CHAT_IMAGES_PER_PAIR_MAX || (held?.total ?? 0) + bytes.byteLength > CHAT_IMAGES_PER_PAIR_MAX_BYTES) {
    throw new RelayError("payload_too_large");
  }
  await enforceQuota(env, "chatImageUploadBytes", deviceKeyId, bytes.byteLength);

  const expiresAt = now + CHAT_IMAGE_TTL_SECONDS;
  // The id is the sender's random choice; a clash with someone else's picture is refused rather than overwritten.
  const inserted = await env.RELAY_DB.prepare(
    `INSERT INTO chat_images (pair_id_hash, pair_epoch, image_id, uploader_device_key_id, size_bytes, expires_at)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6) ON CONFLICT (pair_id_hash, image_id) DO NOTHING`,
  )
    .bind(pairIdHash, pairEpoch, imageId, deviceKeyId, bytes.byteLength, expiresAt)
    .run();
  if (inserted.meta.changes === 0) throw new RelayError("invalid_request");
  await putCiphertext(env, r2KeyForChatImage(pairIdHash, pairEpoch, imageId), bytes);
  return Response.json({ type: "chat-image-upload-result", schemaVersion: 1, expiresAt });
}

export async function fetchChatImage(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env), READ_ONLY);
  await enforceQuota(env, "deviceChatImageFetch", deviceKeyId);
  const { pairIdHash, pairEpoch, imageId } = readRef(asRecord(bodyJson), "chat-image-fetch");
  await requireMember(env, pairIdHash, pairEpoch, deviceKeyId);

  const row = await env.RELAY_DB.prepare(
    `SELECT expires_at FROM chat_images WHERE pair_id_hash = ?1 AND pair_epoch = ?2 AND image_id = ?3`,
  )
    .bind(pairIdHash, pairEpoch, imageId)
    .first<{ expires_at: number }>();
  if (!row || row.expires_at <= nowSeconds()) throw new RelayError("not_found");
  const bytes = await getCiphertext(env, r2KeyForChatImage(pairIdHash, pairEpoch, imageId));
  if (!bytes) throw new RelayError("not_found");
  return Response.json({ type: "chat-image-fetch-result", schemaVersion: 1, ciphertextBase64Url: bytesToBase64Url(bytes) });
}
