import type { Env } from "./env";
import { ACTIVE_DEVICE_WINDOW_SECONDS, NONCE_RETENTION_SECONDS, nowSeconds } from "./lib/constants";
import { deleteCiphertext } from "./lib/r2";
import { logEvent } from "./lib/log";
import { QUOTA_LIMITS } from "./lib/quotas";

/**
 * Runs on the cron trigger configured in wrangler.toml (every 15 minutes).
 * Deletes everything past its documented expiry -- see
 * protocol/docs/threat-model.md for why each table exists and how long it
 * is retained -- and releases any pair's active-catalog-request slot that
 * expired without ever being uploaded.
 */
export async function runScheduledCleanup(env: Env): Promise<void> {
  const now = nowSeconds();

  const expiredInvitations = await env.RELAY_DB.prepare(
    // A consumed tell invitation has done its job; a code invitation's end state (consumed, cancelled, rejected)
    // is kept until its own expiry so the accepter's client can still read the outcome at its next check.
    `DELETE FROM invitations WHERE expires_at <= ?1 OR (status = 'consumed' AND kind = 'tell')`,
  )
    .bind(now)
    .run();

  const expiredRevocations = await env.RELAY_DB.prepare(
    `DELETE FROM revocations WHERE expires_at <= ?1`,
  )
    .bind(now)
    .run();

  const staleActiveRequests = await env.RELAY_DB.prepare(
    `SELECT request_id_hash, pair_id_hash, pair_epoch FROM catalog_requests WHERE status = 'pending' AND expires_at <= ?1`,
  )
    .bind(now)
    .all<{ request_id_hash: string; pair_id_hash: string; pair_epoch: number }>();
  for (const row of staleActiveRequests.results) {
    await env.RELAY_DB.prepare(
      `UPDATE pair_cooldowns SET active_request_id_hash = NULL WHERE pair_id_hash = ?1 AND pair_epoch = ?2 AND active_request_id_hash = ?3`,
    )
      .bind(row.pair_id_hash, row.pair_epoch, row.request_id_hash)
      .run();
  }
  const removedPendingRequests = await env.RELAY_DB.prepare(
    `DELETE FROM catalog_requests WHERE status = 'pending' AND expires_at <= ?1`,
  )
    .bind(now)
    .run();

  const expiredObjects = await env.RELAY_DB.prepare(
    `SELECT request_id_hash, r2_key FROM catalog_objects WHERE expires_at <= ?1`,
  )
    .bind(now)
    .all<{ request_id_hash: string; r2_key: string }>();
  for (const row of expiredObjects.results) {
    await deleteCiphertext(env, row.r2_key);
    await env.RELAY_DB.prepare(`DELETE FROM catalog_objects WHERE request_id_hash = ?1`).bind(row.request_id_hash).run();
    await env.RELAY_DB.prepare(`DELETE FROM catalog_requests WHERE request_id_hash = ?1`).bind(row.request_id_hash).run();
  }

  // Catalog mailboxes: a waiting snapshot past its own retention is dropped (the key stays, so the Sub can
  // still publish again); a revoked pair's mailbox goes entirely - it can never be written or read again.
  const expiredMailboxSnapshots = await env.RELAY_DB.prepare(
    `SELECT pair_id_hash, pair_epoch, snapshot_r2_key FROM catalog_mailboxes WHERE snapshot_expires_at <= ?1`,
  )
    .bind(now)
    .all<{ pair_id_hash: string; pair_epoch: number; snapshot_r2_key: string | null }>();
  for (const row of expiredMailboxSnapshots.results) {
    if (row.snapshot_r2_key) await deleteCiphertext(env, row.snapshot_r2_key);
    await env.RELAY_DB.prepare(
      `UPDATE catalog_mailboxes SET snapshot_id = NULL, snapshot_r2_key = NULL, snapshot_envelope = NULL,
         snapshot_created_at = NULL, snapshot_expires_at = NULL
       WHERE pair_id_hash = ?1 AND pair_epoch = ?2 AND snapshot_r2_key IS ?3`,
    )
      .bind(row.pair_id_hash, row.pair_epoch, row.snapshot_r2_key)
      .run();
  }
  const revokedMailboxes = await env.RELAY_DB.prepare(
    `SELECT m.pair_id_hash, m.pair_epoch, m.snapshot_r2_key FROM catalog_mailboxes m
     JOIN pairs p ON p.pair_id_hash = m.pair_id_hash AND p.pair_epoch = m.pair_epoch
     WHERE p.revoked_at IS NOT NULL`,
  ).all<{ pair_id_hash: string; pair_epoch: number; snapshot_r2_key: string | null }>();
  for (const row of revokedMailboxes.results) {
    if (row.snapshot_r2_key) await deleteCiphertext(env, row.snapshot_r2_key);
    await env.RELAY_DB.prepare(`DELETE FROM catalog_mailboxes WHERE pair_id_hash = ?1 AND pair_epoch = ?2`)
      .bind(row.pair_id_hash, row.pair_epoch)
      .run();
  }

  // Rulebook mailboxes: same treatment per channel - an expired item goes, the key stays; a revoked pair's rows go.
  const expiredRulebookItems = await env.RELAY_DB.prepare(
    `SELECT pair_id_hash, pair_epoch, channel, item_r2_key FROM rulebook_mailboxes WHERE item_expires_at <= ?1`,
  )
    .bind(now)
    .all<{ pair_id_hash: string; pair_epoch: number; channel: string; item_r2_key: string | null }>();
  for (const row of expiredRulebookItems.results) {
    if (row.item_r2_key) await deleteCiphertext(env, row.item_r2_key);
    await env.RELAY_DB.prepare(
      `UPDATE rulebook_mailboxes SET item_sequence = NULL, item_r2_key = NULL, item_envelope = NULL,
         item_created_at = NULL, item_expires_at = NULL
       WHERE pair_id_hash = ?1 AND pair_epoch = ?2 AND channel = ?3 AND item_r2_key IS ?4`,
    )
      .bind(row.pair_id_hash, row.pair_epoch, row.channel, row.item_r2_key)
      .run();
  }
  const revokedRulebookMailboxes = await env.RELAY_DB.prepare(
    `SELECT m.pair_id_hash, m.pair_epoch, m.channel, m.item_r2_key FROM rulebook_mailboxes m
     JOIN pairs p ON p.pair_id_hash = m.pair_id_hash AND p.pair_epoch = m.pair_epoch
     WHERE p.revoked_at IS NOT NULL`,
  ).all<{ pair_id_hash: string; pair_epoch: number; channel: string; item_r2_key: string | null }>();
  for (const row of revokedRulebookMailboxes.results) {
    if (row.item_r2_key) await deleteCiphertext(env, row.item_r2_key);
    await env.RELAY_DB.prepare(`DELETE FROM rulebook_mailboxes WHERE pair_id_hash = ?1 AND pair_epoch = ?2 AND channel = ?3`)
      .bind(row.pair_id_hash, row.pair_epoch, row.channel)
      .run();
  }

  const removedNonces = await env.RELAY_DB.prepare(
    `DELETE FROM nonces WHERE seen_at <= ?1`,
  )
    .bind(now - NONCE_RETENTION_SECONDS)
    .run();

  const active = await env.RELAY_DB.prepare(`SELECT COUNT(DISTINCT device_key_id) AS n FROM nonces WHERE seen_at > ?1`)
    .bind(now - ACTIVE_DEVICE_WINDOW_SECONDS)
    .first<{ n: number }>();
  await env.RELAY_DB.prepare(
    `INSERT INTO relay_stats (id, active_devices, computed_at) VALUES (1, ?1, ?2)
     ON CONFLICT (id) DO UPDATE SET active_devices = excluded.active_devices, computed_at = excluded.computed_at`,
  )
    .bind(active?.n ?? 0, now)
    .run();

  const maxWindowSeconds = Math.max(...Object.values(QUOTA_LIMITS).map((limit) => limit.windowSeconds));
  const removedQuotaCounters = await env.RELAY_DB.prepare(
    `DELETE FROM quota_counters WHERE window_start <= ?1`,
  )
    .bind(now - maxWindowSeconds)
    .run();

  await sweepOrphanCatalogObjects(env);
  await sweepOrphanMailboxObjects(env);
  await sweepOrphanRulebookObjects(env);

  logEvent("scheduled_cleanup_complete", {
    expiredInvitations: expiredInvitations.meta.changes ?? 0,
    expiredRevocations: expiredRevocations.meta.changes ?? 0,
    removedPendingRequests: removedPendingRequests.meta.changes ?? 0,
    expiredObjects: expiredObjects.results.length,
    expiredMailboxSnapshots: expiredMailboxSnapshots.results.length,
    removedRevokedMailboxes: revokedMailboxes.results.length,
    expiredRulebookItems: expiredRulebookItems.results.length,
    removedRevokedRulebookMailboxes: revokedRulebookMailboxes.results.length,
    removedNonces: removedNonces.meta.changes ?? 0,
    activeDevices: active?.n ?? 0,
    removedQuotaCounters: removedQuotaCounters.meta.changes ?? 0,
  });

  await checkQuotaAlarm(env);
}

/**
 * Best-effort: catches an R2 object left behind by a crash between the R2
 * put and the D1 insert that references it (or the reverse on delete). A
 * missed sweep is not a correctness problem -- the object still expires and
 * is deleted on a later run -- so this is bounded per invocation rather than
 * paginating the whole bucket.
 */
async function sweepOrphanCatalogObjects(env: Env): Promise<void> {
  const listed = await env.RELAY_CATALOG_BUCKET.list({ prefix: "catalog/", limit: 200 });
  for (const object of listed.objects) {
    const requestIdHash = object.key.slice("catalog/".length);
    const row = await env.RELAY_DB.prepare(`SELECT 1 FROM catalog_objects WHERE request_id_hash = ?1`)
      .bind(requestIdHash)
      .first();
    if (!row) {
      await deleteCiphertext(env, object.key);
    }
  }
}

/**
 * Same best-effort sweep for mailbox snapshots: an object no mailbox row points at (a crash between the R2
 * put and the row update, or between a replacement and deleting the object it replaced) is removed.
 */
async function sweepOrphanMailboxObjects(env: Env): Promise<void> {
  const listed = await env.RELAY_CATALOG_BUCKET.list({ prefix: "mailbox/", limit: 200 });
  for (const object of listed.objects) {
    const row = await env.RELAY_DB.prepare(`SELECT 1 FROM catalog_mailboxes WHERE snapshot_r2_key = ?1`)
      .bind(object.key)
      .first();
    if (!row) {
      await deleteCiphertext(env, object.key);
    }
  }
}

async function sweepOrphanRulebookObjects(env: Env): Promise<void> {
  const listed = await env.RELAY_CATALOG_BUCKET.list({ prefix: "rulebook/", limit: 200 });
  for (const object of listed.objects) {
    const row = await env.RELAY_DB.prepare(`SELECT 1 FROM rulebook_mailboxes WHERE item_r2_key = ?1`)
      .bind(object.key)
      .first();
    if (!row) {
      await deleteCiphertext(env, object.key);
    }
  }
}

async function checkQuotaAlarm(env: Env): Promise<void> {
  for (const name of ["globalDailyWork", "globalDailySafety"] as const) {
    const limit = QUOTA_LIMITS[name];
    const bucket = Math.floor(nowSeconds() / limit.windowSeconds) * limit.windowSeconds;
    const row = await env.RELAY_DB.prepare(`SELECT count FROM quota_counters WHERE scope = ?1 AND window_start = ?2`)
      .bind(`${name}:all`, bucket)
      .first<{ count: number }>();
    const usageRatio = (row?.count ?? 0) / limit.maxCount;
    if (usageRatio >= 0.8) {
      logEvent("quota_alarm", { scope: name, usageRatioPercent: Math.round(usageRatio * 100) });
    }
  }
}
