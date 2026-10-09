import type { Env } from "../env";
import { resolverFromStoredDeviceKeys, READ_ONLY, verifySignedRequest } from "../lib/auth";
import { nowSeconds, PAIR_STATUS_BATCH_MAX, TIMESTAMP_TOLERANCE_SECONDS } from "../lib/constants";
import { RelayError } from "../lib/errors";
import { type CollarState, isMemberOfPair, latestPair, pairAtEpoch, type PairRow } from "../lib/pairs";
import { enforceQuota } from "../lib/quotas";
import { catalogMailboxSummary } from "./mailbox";
import { rulebookMailboxSummary } from "./rulebook";
import { asRecord, isHex64, isNonNegInt, isUnixSeconds, requireField } from "../lib/validate";

/**
 * Authenticated lookup of a pair's current epoch. pairIdHash itself is derivable locally by both
 * peers (SHA-256 of their sorted device key ids) with no server round trip, but the epoch is only
 * decided server-side when the inviter calls consume -- so the Accepter, who never calls consume,
 * has no other way to learn it. Membership-gated the same way checkRevocations is: only a device
 * key already on file as this pair's owner or sub may read it.
 */
export async function fetchPair(request: Request, env: Env, pairIdHash: string): Promise<Response> {
  if (!isHex64(pairIdHash)) throw new RelayError("not_found");
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env), READ_ONLY);

  // collar/pairing: `?epoch=N` asks about one exact pairing. A mutual pair (both directions between the same
  // two devices) shares one pairIdHash across epochs, so "is my pairing over?" must never be answered from
  // whichever epoch happens to be latest. Without it, the latest epoch (the original behavior).
  const epochParam = new URL(request.url).searchParams.get("epoch");
  if (epochParam !== null && !/^\d{1,9}$/.test(epochParam)) throw new RelayError("invalid_request");
  const pair = epochParam !== null ? await pairAtEpoch(env, pairIdHash, Number(epochParam)) : await latestPair(env, pairIdHash);
  if (!pair || !isMemberOfPair(pair, deviceKeyId)) {
    throw new RelayError("unauthorized");
  }

  return Response.json(await pairStatusBody(env, pair, await relayActivity(env)));
}

/**
 * relay-budget: the status of several of the caller's pairings in one read-only request, each answered exactly as
 * fetchPair would, or rejected on its own with the same generic "unauthorized" so nothing is learned about it.
 */
export async function fetchPairStatusBatch(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env), READ_ONLY);
  const body = asRecord(bodyJson);
  if (body.type !== "pair-status-batch-request" || body.schemaVersion !== 1) throw new RelayError("invalid_request");
  const refs = body.pairs;
  if (!Array.isArray(refs) || refs.length === 0 || refs.length > PAIR_STATUS_BATCH_MAX) throw new RelayError("invalid_request");
  const parsed = refs.map((ref) => {
    const r = asRecord(ref);
    return { pairIdHash: requireField(r, "pairIdHash", isHex64), pairEpoch: requireField(r, "pairEpoch", isNonNegInt) };
  });

  const activity = await relayActivity(env);
  const pairs = [];
  for (const { pairIdHash, pairEpoch } of parsed) {
    const pair = await pairAtEpoch(env, pairIdHash, pairEpoch);
    pairs.push(pair && isMemberOfPair(pair, deviceKeyId)
      ? { pairIdHash, pairEpoch, pair: await pairStatusBody(env, pair, activity) }
      : { pairIdHash, pairEpoch, error: "unauthorized" as const });
  }
  return Response.json({ type: "pair-status-batch", schemaVersion: 1, pairs });
}

async function pairStatusBody(env: Env, pair: PairRow, activity: Awaited<ReturnType<typeof relayActivity>>) {
  return {
    type: "pair",
    schemaVersion: 1,
    pairIdHash: pair.pair_id_hash,
    pairEpoch: pair.pair_epoch,
    ownerDeviceKeyId: pair.owner_device_key_id,
    subDeviceKeyId: pair.sub_device_key_id,
    createdAt: pair.created_at,
    revokedAt: pair.revoked_at,
    collarState: pair.collar_state ?? null,
    collarStateAt: pair.collar_state_at ?? null,
    collarCheckinAt: pair.collar_checkin_at ?? null,
    catalogMailbox: await catalogMailboxSummary(env, pair.pair_id_hash, pair.pair_epoch),
    rulebookMailbox: await rulebookMailboxSummary(env, pair.pair_id_hash, pair.pair_epoch),
    ...activity,
  };
}

/** Null until the cron has run once. */
async function relayActivity(env: Env): Promise<{ relayActiveDevices: number | null; relayActiveDevicesAt: number | null }> {
  const row = await env.RELAY_DB.prepare(`SELECT active_devices, computed_at FROM relay_stats WHERE id = 1`)
    .first<{ active_devices: number; computed_at: number }>();
  return { relayActiveDevices: row?.active_devices ?? null, relayActiveDevicesAt: row?.computed_at ?? null };
}

function isCollarState(value: unknown): value is CollarState {
  return value === "locked" || value === "unlocked" || value === "broken";
}

/**
 * collar/collar-status: the pair's Sub device reports whether the collar is on. Only that epoch's Sub may write,
 * and only while the epoch isn't revoked. Every accepted report stamps the check-in time; a report older than the
 * stored state keeps the state but still counts as a check-in, so a delayed retry can't roll the state back.
 */
export async function putCollarStatus(request: Request, env: Env, pairIdHash: string): Promise<Response> {
  if (!isHex64(pairIdHash)) throw new RelayError("not_found");
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "deviceCollarStatus", deviceKeyId);

  const body = asRecord(bodyJson);
  if (body.type !== "collar-status" || body.schemaVersion !== 1) throw new RelayError("invalid_request");
  const pairEpoch = requireField(body, "pairEpoch", isNonNegInt);
  const state = requireField(body, "state", isCollarState);
  const stateAt = requireField(body, "stateAt", isUnixSeconds);
  const now = nowSeconds();
  if (stateAt > now + TIMESTAMP_TOLERANCE_SECONDS) throw new RelayError("invalid_request");

  const pair = await pairAtEpoch(env, pairIdHash, pairEpoch);
  // One generic answer for wrong pair, wrong device or a revoked epoch, so a caller can't tell which.
  if (!pair || pair.sub_device_key_id !== deviceKeyId || pair.revoked_at !== null) throw new RelayError("unauthorized");

  await env.RELAY_DB.prepare(
    `UPDATE pairs SET
       collar_state = CASE WHEN collar_state_at IS NULL OR ?1 >= collar_state_at THEN ?2 ELSE collar_state END,
       collar_state_at = CASE WHEN collar_state_at IS NULL OR ?1 >= collar_state_at THEN ?1 ELSE collar_state_at END,
       collar_checkin_at = ?3
     WHERE pair_id_hash = ?4 AND pair_epoch = ?5 AND revoked_at IS NULL`,
  )
    .bind(stateAt, state, now, pairIdHash, pairEpoch)
    .run();

  return Response.json({ type: "collar-status-ack", schemaVersion: 1 });
}
