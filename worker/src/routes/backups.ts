import type { Env } from "../env";
import { resolverFromStoredDeviceKeys, verifySignedRequest } from "../lib/auth";
import { capabilityHash, isValidCapabilityShape } from "../lib/capability";
import { BACKUP_CIPHERTEXT_MAX_BYTES, nowSeconds } from "../lib/constants";
import { RelayError } from "../lib/errors";
import { originScope } from "../lib/origin";
import { assertCircuitBreakerClosed, enforceQuota } from "../lib/quotas";
import { asRecord, isAeadNonce, requireField } from "../lib/validate";

/**
 * collar/pairing-recovery: one encrypted backup per recovery code. The path id is the client's backupId
 * (derived from its recovery code); the relay stores only its SHA-256, the same way invitation and catalog
 * request capabilities are stored. The ciphertext is opaque here - identity, pairings and character names
 * are all inside it, under a key the relay never sees.
 */

interface BackupRow {
  backup_id_hash: string;
  device_key_id: string;
  nonce: string;
  ciphertext: string;
  updated_at: number;
}

// base64url of at most BACKUP_CIPHERTEXT_MAX_BYTES bytes.
const MAX_CIPHERTEXT_CHARS = Math.ceil((BACKUP_CIPHERTEXT_MAX_BYTES * 4) / 3);
const CIPHERTEXT_PATTERN = /^[A-Za-z0-9_-]+$/;

function isBackupCiphertext(value: unknown): value is string {
  return typeof value === "string" && value.length >= 24 && value.length <= MAX_CIPHERTEXT_CHARS && CIPHERTEXT_PATTERN.test(value);
}

async function loadBackup(env: Env, backupIdHash: string): Promise<BackupRow | null> {
  return env.RELAY_DB.prepare(`SELECT * FROM backups WHERE backup_id_hash = ?1`).bind(backupIdHash).first<BackupRow>();
}

/**
 * Signed write. The first write binds the backup to the writing device key; every later write (and delete)
 * must come from that same key, so a leaked backupId alone can never overwrite someone's backup. A restore
 * on a new install uses the same (restored) device key, so it can keep refreshing its own backup.
 */
export async function putBackup(request: Request, env: Env, backupId: string): Promise<Response> {
  if (!isValidCapabilityShape(backupId)) throw new RelayError("not_found");
  await assertCircuitBreakerClosed(env);
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "deviceBackupWrite", deviceKeyId);

  const body = asRecord(bodyJson);
  if (body.type !== "backup" || body.schemaVersion !== 1) throw new RelayError("invalid_request");
  const nonce = requireField(body, "nonce", isAeadNonce);
  const ciphertext = requireField(body, "ciphertext", isBackupCiphertext);

  const backupIdHash = await capabilityHash(backupId);
  const existing = await loadBackup(env, backupIdHash);
  if (existing && existing.device_key_id !== deviceKeyId) throw new RelayError("unauthorized");

  const now = nowSeconds();
  await env.RELAY_DB.prepare(
    `INSERT INTO backups (backup_id_hash, device_key_id, nonce, ciphertext, updated_at) VALUES (?1, ?2, ?3, ?4, ?5)
     ON CONFLICT (backup_id_hash) DO UPDATE SET nonce = excluded.nonce, ciphertext = excluded.ciphertext, updated_at = excluded.updated_at
     WHERE backups.device_key_id = excluded.device_key_id`,
  )
    .bind(backupIdHash, deviceKeyId, nonce, ciphertext, now)
    .run();

  return Response.json({ type: "backup-status", schemaVersion: 1, updatedAt: now });
}

/**
 * Capability read: possessing the backupId (i.e. the recovery code) is the proof. Returns the device key id
 * the backup is bound to, so the restoring client can confirm the decrypted identity matches it.
 */
export async function fetchBackup(request: Request, env: Env, backupId: string): Promise<Response> {
  if (!isValidCapabilityShape(backupId)) throw new RelayError("not_found");
  await enforceQuota(env, "originBackupFetch", await originScope(request));
  const row = await loadBackup(env, await capabilityHash(backupId));
  if (!row) throw new RelayError("not_found");
  return Response.json({
    type: "backup",
    schemaVersion: 1,
    deviceKeyId: row.device_key_id,
    nonce: row.nonce,
    ciphertext: row.ciphertext,
    updatedAt: row.updated_at,
  });
}

/** Signed delete, by the owning device only (recovery code regenerated, or device identity reset). */
export async function deleteBackup(request: Request, env: Env, backupId: string): Promise<Response> {
  if (!isValidCapabilityShape(backupId)) throw new RelayError("not_found");
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "deviceBackupWrite", deviceKeyId);
  const backupIdHash = await capabilityHash(backupId);
  const existing = await loadBackup(env, backupIdHash);
  if (!existing) return Response.json({ type: "backup-status", schemaVersion: 1, deleted: false });
  if (existing.device_key_id !== deviceKeyId) throw new RelayError("unauthorized");
  await env.RELAY_DB.prepare(`DELETE FROM backups WHERE backup_id_hash = ?1 AND device_key_id = ?2`).bind(backupIdHash, deviceKeyId).run();
  return Response.json({ type: "backup-status", schemaVersion: 1, deleted: true });
}
