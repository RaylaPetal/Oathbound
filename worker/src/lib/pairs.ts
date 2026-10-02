import type { Env } from "../env";
import { sha256Hex, toCanonicalJson } from "./json";

/**
 * Deterministic so both peers can compute it locally without a server round
 * trip: the SHA-256 of the two device key ids, sorted so order never matters.
 */
export async function computePairIdHash(deviceKeyIdA: string, deviceKeyIdB: string): Promise<string> {
  const [a, b] = [deviceKeyIdA, deviceKeyIdB].sort();
  return sha256Hex(toCanonicalJson({ a, b }));
}

export interface PairRow {
  pair_id_hash: string;
  pair_epoch: number;
  owner_device_key_id: string;
  sub_device_key_id: string;
  created_at: number;
  revoked_at: number | null;
  collar_state: CollarState | null;
  collar_state_at: number | null;
  collar_checkin_at: number | null;
}

export type CollarState = "locked" | "unlocked" | "broken";

export async function latestPair(env: Env, pairIdHash: string): Promise<PairRow | null> {
  return env.RELAY_DB.prepare(
    `SELECT * FROM pairs WHERE pair_id_hash = ?1 ORDER BY pair_epoch DESC LIMIT 1`,
  )
    .bind(pairIdHash)
    .first<PairRow>();
}

/**
 * `pairIdHash` alone is not a unique pairing: it's computed from just the two device keys, so two people
 * paired in both directions (a mutual Owner/Sub pair) share one hash across two different epochs - one row
 * each. Any caller that already knows which specific pairing an envelope/request belongs to (it always
 * carries its own pairEpoch) must look it up by the exact pair, not `latestPair`'s "whichever epoch is
 * newest for this hash" - using `latestPair` here would authorize/reject against the *other* direction's
 * pairing whenever it happens to be the newer one.
 */
export async function pairAtEpoch(env: Env, pairIdHash: string, pairEpoch: number): Promise<PairRow | null> {
  return env.RELAY_DB.prepare(
    `SELECT * FROM pairs WHERE pair_id_hash = ?1 AND pair_epoch = ?2`,
  )
    .bind(pairIdHash, pairEpoch)
    .first<PairRow>();
}

export function isMemberOfPair(pair: PairRow, deviceKeyId: string): boolean {
  return pair.owner_device_key_id === deviceKeyId || pair.sub_device_key_id === deviceKeyId;
}
