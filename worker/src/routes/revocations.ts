import type { Env } from "../env";
import { resolverFromStoredDeviceKeys, READ_ONLY, verifySignedRequest } from "../lib/auth";
import { verifyEcdsaSignature, type EcPublicKeyJwk } from "../lib/crypto";
import { toCanonicalJson } from "../lib/json";
import { enforceQuota } from "../lib/quotas";
import { REVOCATION_RETENTION_SECONDS_MAX, nowSeconds } from "../lib/constants";
import { lookupDeviceKey } from "../lib/deviceKeys";
import { RelayError } from "../lib/errors";
import { isMemberOfPair, pairAtEpoch } from "../lib/pairs";
import { asRecord, isHex64, isNonNegInt, isSignature, isUnixSeconds, requireField } from "../lib/validate";

interface RevocationEnvelope {
  type: "revocation";
  schemaVersion: 1;
  pairIdHash: string;
  pairEpoch: number;
  sequence: number;
  reason: "unpair" | "panic";
  issuedByDeviceKeyId: string;
  createdAt: number;
  expiresAt: number;
  signature: string;
}

function isReason(value: unknown): value is "unpair" | "panic" {
  return value === "unpair" || value === "panic";
}

async function verifyEnvelopeSignature(envelope: RevocationEnvelope, publicKeyJwk: EcPublicKeyJwk): Promise<boolean> {
  const { signature, ...unsigned } = envelope;
  return verifyEcdsaSignature(publicKeyJwk, signature, toCanonicalJson(unsigned));
}

/** Publish: best-effort from the client's perspective, but the relay itself never gates this behind the circuit breaker (spec: "safety-priority availability"). */
export async function publishRevocation(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "pairMutation", deviceKeyId);

  const body = asRecord(bodyJson);
  const envelope: RevocationEnvelope = {
    type: "revocation",
    schemaVersion: 1,
    pairIdHash: requireField(body, "pairIdHash", isHex64),
    pairEpoch: requireField(body, "pairEpoch", isNonNegInt),
    sequence: requireField(body, "sequence", isNonNegInt),
    reason: requireField(body, "reason", isReason),
    issuedByDeviceKeyId: requireField(body, "issuedByDeviceKeyId", isHex64),
    createdAt: requireField(body, "createdAt", isUnixSeconds),
    expiresAt: requireField(body, "expiresAt", isUnixSeconds),
    signature: requireField(body, "signature", isSignature),
  };

  if (envelope.issuedByDeviceKeyId !== deviceKeyId) throw new RelayError("unauthorized");
  if (envelope.expiresAt - envelope.createdAt > REVOCATION_RETENTION_SECONDS_MAX) throw new RelayError("invalid_request");
  if (envelope.expiresAt <= nowSeconds()) throw new RelayError("invalid_request");

  const pair = await pairAtEpoch(env, envelope.pairIdHash, envelope.pairEpoch);
  if (!pair || !isMemberOfPair(pair, deviceKeyId) || pair.revoked_at !== null) {
    // Wrong pair/epoch, wrong device, or this exact epoch was already revoked (a stale, late-arriving
    // revocation for a pairing that's already ended must not re-trigger anything): fails closed without
    // hinting which. `pairAtEpoch` (unlike the `latestPair` this used to call) finds this exact epoch's row
    // even when a newer epoch now exists for the same pairIdHash - a mutual pair's *other* direction being
    // newer must never make this epoch's own already-revoked state pass silently.
    throw new RelayError("unauthorized");
  }

  const publicKeyJwk = await lookupDeviceKey(env, deviceKeyId);
  if (!publicKeyJwk || !(await verifyEnvelopeSignature(envelope, publicKeyJwk))) {
    throw new RelayError("unauthorized");
  }

  // Scoped by (pairIdHash, pairEpoch): a mutual pair's two directions share one pairIdHash but each keeps
  // its own independent sequence counter, so one direction's sequence numbers must never be checked
  // against the other's history.
  const highestSequenceRow = await env.RELAY_DB.prepare(
    `SELECT MAX(sequence) AS max_sequence FROM revocations WHERE pair_id_hash = ?1 AND pair_epoch = ?2`,
  )
    .bind(envelope.pairIdHash, envelope.pairEpoch)
    .first<{ max_sequence: number | null }>();
  if (highestSequenceRow?.max_sequence !== null && highestSequenceRow?.max_sequence !== undefined && envelope.sequence <= highestSequenceRow.max_sequence) {
    throw new RelayError("invalid_request");
  }

  await env.RELAY_DB.batch([
    env.RELAY_DB.prepare(
      `INSERT INTO revocations (pair_id_hash, sequence, pair_epoch, reason, issued_by_device_key_id, signature, created_at, expires_at)
       VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)`,
    ).bind(
      envelope.pairIdHash,
      envelope.sequence,
      envelope.pairEpoch,
      envelope.reason,
      envelope.issuedByDeviceKeyId,
      envelope.signature,
      envelope.createdAt,
      envelope.expiresAt,
    ),
    env.RELAY_DB.prepare(
      `UPDATE pairs SET revoked_at = ?1 WHERE pair_id_hash = ?2 AND pair_epoch = ?3 AND revoked_at IS NULL`,
    ).bind(nowSeconds(), envelope.pairIdHash, envelope.pairEpoch),
  ]);

  return Response.json(envelope);
}

/** Check: available even when a device's own pair was already revoked, and never gated by the circuit breaker. */
export async function checkRevocations(request: Request, env: Env, pairIdHash: string): Promise<Response> {
  if (!isHex64(pairIdHash)) throw new RelayError("not_found");
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env), READ_ONLY);

  const membership = await env.RELAY_DB.prepare(
    `SELECT 1 FROM pairs WHERE pair_id_hash = ?1 AND (owner_device_key_id = ?2 OR sub_device_key_id = ?2) LIMIT 1`,
  )
    .bind(pairIdHash, deviceKeyId)
    .first();
  if (!membership) throw new RelayError("unauthorized");

  const url = new URL(request.url);
  const sinceSequenceParam = url.searchParams.get("sinceSequence") ?? "0";
  const sinceSequence = Number.parseInt(sinceSequenceParam, 10);
  if (!Number.isFinite(sinceSequence) || sinceSequence < 0) throw new RelayError("invalid_request");

  const now = nowSeconds();
  const rows = await env.RELAY_DB.prepare(
    `SELECT * FROM revocations WHERE pair_id_hash = ?1 AND sequence > ?2 AND expires_at > ?3 ORDER BY sequence ASC LIMIT 50`,
  )
    .bind(pairIdHash, sinceSequence, now)
    .all<{
      pair_id_hash: string;
      sequence: number;
      pair_epoch: number;
      reason: string;
      issued_by_device_key_id: string;
      signature: string;
      created_at: number;
      expires_at: number;
    }>();

  return Response.json({
    revocations: rows.results.map((row) => ({
      type: "revocation",
      schemaVersion: 1,
      pairIdHash: row.pair_id_hash,
      pairEpoch: row.pair_epoch,
      sequence: row.sequence,
      reason: row.reason,
      issuedByDeviceKeyId: row.issued_by_device_key_id,
      createdAt: row.created_at,
      expiresAt: row.expires_at,
      signature: row.signature,
    })),
  });
}
