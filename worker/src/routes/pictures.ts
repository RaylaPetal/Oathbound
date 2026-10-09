import type { Env } from "../env";
import { READ_ONLY, resolverFromStoredDeviceKeys, verifySignedRequest } from "../lib/auth";
import { base64UrlToBytes, bytesToBase64Url } from "../lib/base64";
import {
  nowSeconds,
  PICTURE_CIPHERTEXT_MAX_BYTES,
  PICTURE_FETCH_BATCH_MAX,
  PICTURES_PER_PAIR_MAX,
  PICTURES_PER_PAIR_MAX_BYTES,
} from "../lib/constants";
import { RelayError } from "../lib/errors";
import { type PairRow, pairAtEpoch } from "../lib/pairs";
import { assertCircuitBreakerClosed, enforceQuota } from "../lib/quotas";
import { getCiphertext, putCiphertext, r2KeyForPicture } from "../lib/r2";
import { asRecord, isHex64, isNonNegInt, requireField } from "../lib/validate";

/**
 * catalog-pictures: the Sub's restraint pictures as separately stored blobs, so a catalog publish only carries a
 * reference (id, content hash, key) inside its own end-to-end encryption and unchanged pictures are never re-sent.
 * The relay sees an opaque id, its pair and its size - never the restraint, the picture or its key.
 */

const PICTURE_ID_PATTERN = /^[A-Za-z0-9_-]{22}$/;
/** A held picture's reference time is refreshed at most this often, so a routine publish writes nothing. */
const REFERENCE_REFRESH_SECONDS = 86400;

interface PictureRow {
  picture_id: string;
  size_bytes: number;
  last_referenced_at: number;
  unreferenced_since: number | null;
}

async function requirePairSide(env: Env, pairIdHash: string, pairEpoch: number, deviceKeyId: string, side: "owner" | "sub"): Promise<PairRow> {
  const pair = await pairAtEpoch(env, pairIdHash, pairEpoch);
  if (!pair || pair.revoked_at !== null) throw new RelayError("unauthorized");
  if ((side === "owner" ? pair.owner_device_key_id : pair.sub_device_key_id) !== deviceKeyId) throw new RelayError("unauthorized");
  return pair;
}

function readIds(body: Record<string, unknown>, min: number, max: number): string[] {
  const ids = body.ids;
  if (!Array.isArray(ids) || ids.length < min || ids.length > max) throw new RelayError("invalid_request");
  if (!ids.every((id): id is string => typeof id === "string" && PICTURE_ID_PATTERN.test(id))) throw new RelayError("invalid_request");
  if (new Set(ids).size !== ids.length) throw new RelayError("invalid_request");
  return ids;
}

function readPair(body: Record<string, unknown>, type: string): { pairIdHash: string; pairEpoch: number } {
  if (body.type !== type || body.schemaVersion !== 1) throw new RelayError("invalid_request");
  return { pairIdHash: requireField(body, "pairIdHash", isHex64), pairEpoch: requireField(body, "pairEpoch", isNonNegInt) };
}

async function heldPictures(env: Env, pairIdHash: string, pairEpoch: number): Promise<PictureRow[]> {
  const rows = await env.RELAY_DB.prepare(
    `SELECT picture_id, size_bytes, last_referenced_at, unreferenced_since FROM catalog_pictures WHERE pair_id_hash = ?1 AND pair_epoch = ?2`,
  )
    .bind(pairIdHash, pairEpoch)
    .all<PictureRow>();
  return rows.results;
}

/** Sub: the full set of ids its latest catalog references. Answers which ones the relay doesn't hold. */
export async function syncPictures(request: Request, env: Env): Promise<Response> {
  await assertCircuitBreakerClosed(env);
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "devicePictureOps", deviceKeyId);
  const body = asRecord(bodyJson);
  const { pairIdHash, pairEpoch } = readPair(body, "picture-sync");
  const ids = readIds(body, 0, PICTURES_PER_PAIR_MAX);
  await requirePairSide(env, pairIdHash, pairEpoch, deviceKeyId, "sub");

  const now = nowSeconds();
  const listed = new Set(ids);
  const held = await heldPictures(env, pairIdHash, pairEpoch);
  const statements: D1PreparedStatement[] = [];
  for (const row of held) {
    if (listed.has(row.picture_id)) {
      // Only rows whose state actually changes are written.
      if (row.unreferenced_since !== null || now - row.last_referenced_at >= REFERENCE_REFRESH_SECONDS) {
        statements.push(
          env.RELAY_DB.prepare(
            `UPDATE catalog_pictures SET last_referenced_at = ?1, unreferenced_since = NULL WHERE pair_id_hash = ?2 AND pair_epoch = ?3 AND picture_id = ?4`,
          ).bind(now, pairIdHash, pairEpoch, row.picture_id),
        );
      }
    } else if (row.unreferenced_since === null) {
      statements.push(
        env.RELAY_DB.prepare(
          `UPDATE catalog_pictures SET unreferenced_since = ?1 WHERE pair_id_hash = ?2 AND pair_epoch = ?3 AND picture_id = ?4`,
        ).bind(now, pairIdHash, pairEpoch, row.picture_id),
      );
    }
  }
  if (statements.length > 0) await env.RELAY_DB.batch(statements);

  const heldIds = new Set(held.map((row) => row.picture_id));
  return Response.json({ type: "picture-sync-result", schemaVersion: 1, missing: ids.filter((id) => !heldIds.has(id)) });
}

/** Sub: one picture blob. Re-uploading an id the relay already holds succeeds without writing anything. */
export async function uploadPicture(request: Request, env: Env): Promise<Response> {
  await assertCircuitBreakerClosed(env);
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  const body = asRecord(bodyJson);
  const { pairIdHash, pairEpoch } = readPair(body, "picture-upload");
  const pictureId = requireField(body, "pictureId", (v): v is string => typeof v === "string" && PICTURE_ID_PATTERN.test(v));
  const ciphertextBase64Url = body.ciphertextBase64Url;
  if (typeof ciphertextBase64Url !== "string") throw new RelayError("invalid_request");
  const bytes = base64UrlToBytes(ciphertextBase64Url);
  if (bytes.byteLength <= 12 || bytes.byteLength > PICTURE_CIPHERTEXT_MAX_BYTES) throw new RelayError("payload_too_large");
  await requirePairSide(env, pairIdHash, pairEpoch, deviceKeyId, "sub");

  const held = await heldPictures(env, pairIdHash, pairEpoch);
  if (held.some((row) => row.picture_id === pictureId)) return Response.json({ type: "picture-upload-result", schemaVersion: 1 });
  const heldBytes = held.reduce((sum, row) => sum + row.size_bytes, 0);
  if (held.length + 1 > PICTURES_PER_PAIR_MAX || heldBytes + bytes.byteLength > PICTURES_PER_PAIR_MAX_BYTES) {
    throw new RelayError("payload_too_large");
  }
  await enforceQuota(env, "pictureUploadBytes", deviceKeyId, bytes.byteLength);

  const now = nowSeconds();
  await putCiphertext(env, r2KeyForPicture(pairIdHash, pairEpoch, pictureId), bytes);
  await env.RELAY_DB.prepare(
    `INSERT INTO catalog_pictures (pair_id_hash, pair_epoch, picture_id, size_bytes, created_at, last_referenced_at, unreferenced_since)
     VALUES (?1, ?2, ?3, ?4, ?5, ?5, NULL)
     ON CONFLICT (pair_id_hash, pair_epoch, picture_id) DO NOTHING`,
  )
    .bind(pairIdHash, pairEpoch, pictureId, bytes.byteLength, now)
    .run();
  return Response.json({ type: "picture-upload-result", schemaVersion: 1 });
}

/** Owner: a batch of picture blobs. Read-only; ids the relay doesn't hold are left out. */
export async function fetchPictures(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env), READ_ONLY);
  await enforceQuota(env, "devicePictureOps", deviceKeyId);
  const body = asRecord(bodyJson);
  const { pairIdHash, pairEpoch } = readPair(body, "picture-fetch");
  const ids = readIds(body, 1, PICTURE_FETCH_BATCH_MAX);
  await requirePairSide(env, pairIdHash, pairEpoch, deviceKeyId, "owner");

  const heldIds = new Set((await heldPictures(env, pairIdHash, pairEpoch)).map((row) => row.picture_id));
  const pictures: { pictureId: string; ciphertextBase64Url: string }[] = [];
  for (const pictureId of ids) {
    if (!heldIds.has(pictureId)) continue;
    const bytes = await getCiphertext(env, r2KeyForPicture(pairIdHash, pairEpoch, pictureId));
    if (bytes) pictures.push({ pictureId, ciphertextBase64Url: bytesToBase64Url(bytes) });
  }
  return Response.json({ type: "picture-fetch-result", schemaVersion: 1, pictures });
}
