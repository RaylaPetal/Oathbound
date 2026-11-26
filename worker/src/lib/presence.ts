import type { Env } from "../env";
import { PRESENCE_COUNT_REFRESH_SECONDS, PRESENCE_ONLINE_TTL_SECONDS, PRESENCE_REWRITE_AFTER_SECONDS, nowSeconds } from "./constants";

/**
 * Who's online now: a device is online while its live connection is open. Connect and refresh push its expiry
 * forward, the last connection closing clears it, and the expiry ends whatever a crash or deploy left behind.
 */
export async function markOnline(env: Env, deviceKeyId: string, force: boolean): Promise<void> {
  const now = nowSeconds();
  const until = now + PRESENCE_ONLINE_TTL_SECONDS;
  // Rewritten only once half run down, so refreshes while the window is open are reads.
  await env.RELAY_DB.prepare(
    `UPDATE device_keys SET online_until = ?2 WHERE device_key_id = ?1 AND (?3 = 1 OR online_until IS NULL OR online_until < ?4)`,
  )
    .bind(deviceKeyId, until, force ? 1 : 0, until - PRESENCE_REWRITE_AFTER_SECONDS)
    .run();
}

export async function markOffline(env: Env, deviceKeyId: string): Promise<void> {
  await env.RELAY_DB.prepare(`UPDATE device_keys SET online_until = NULL WHERE device_key_id = ?1 AND online_until IS NOT NULL`)
    .bind(deviceKeyId)
    .run();
}

/** The cached count, recounted when it's older than a minute. Two hubs racing just both recount. */
export async function onlineCount(env: Env): Promise<{ online: number; onlineAt: number }> {
  const now = nowSeconds();
  const cached = await env.RELAY_DB.prepare(`SELECT online_devices, computed_at FROM relay_presence WHERE id = 1`)
    .first<{ online_devices: number; computed_at: number }>();
  if (cached && now - cached.computed_at < PRESENCE_COUNT_REFRESH_SECONDS) return { online: cached.online_devices, onlineAt: cached.computed_at };

  const row = await env.RELAY_DB.prepare(`SELECT COUNT(*) AS n FROM device_keys WHERE online_until IS NOT NULL AND online_until > ?1`)
    .bind(now)
    .first<{ n: number }>();
  const online = row?.n ?? 0;
  await env.RELAY_DB.prepare(
    `INSERT INTO relay_presence (id, online_devices, computed_at) VALUES (1, ?1, ?2)
     ON CONFLICT (id) DO UPDATE SET online_devices = excluded.online_devices, computed_at = excluded.computed_at`,
  )
    .bind(online, now)
    .run();
  return { online, onlineAt: now };
}
