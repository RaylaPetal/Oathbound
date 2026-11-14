import type { Env } from "../env";
import { logEvent } from "./log";
import { chargeLive } from "./quotas";

interface PairMembers {
  pair_id_hash: string;
  pair_epoch: number;
  owner_device_key_id: string;
  sub_device_key_id: string;
}

/**
 * collar/relay-messaging: tells the other member's live connection that this pairing changed, so its client runs the
 * pair status check now. Best effort: an offline member gets nothing and catches up on its regular check. A
 * revocation's hint is never held back by the live budget.
 */
export async function notifyPeer(env: Env, pair: PairMembers, writerDeviceKeyId: string, safety = false): Promise<void> {
  const peer = pair.owner_device_key_id === writerDeviceKeyId ? pair.sub_device_key_id : pair.owner_device_key_id;
  try {
    if (!safety && !(await chargeLive(env))) return;
    await env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(peer)).hint(pair.pair_id_hash, pair.pair_epoch);
  } catch {
    logEvent("hint_failed");
  }
}
