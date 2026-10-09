import type { Env } from "../env";
import { RelayError } from "./errors";
import { nowSeconds } from "./constants";

/**
 * Layered abuse/cost controls (spec: "Relay enforces layered abuse and cost
 * controls"). Each named quota is a fixed time window with a request-count
 * ceiling and, for byte-bearing endpoints, a byte ceiling. All windows share
 * the same `quota_counters` table; a scope is just a string key so per-device,
 * per-pair, per-origin, per-endpoint, and global limits are the same
 * mechanism applied to different keys.
 */
export interface QuotaLimit {
  windowSeconds: number;
  maxCount: number;
  maxBytes?: number;
}

export const QUOTA_LIMITS = {
  deviceInvitationCreate: { windowSeconds: 3600, maxCount: 10 },
  deviceCatalogRequestCreate: { windowSeconds: 3600, maxCount: 5 },
  pairMutation: { windowSeconds: 3600, maxCount: 60 },
  // Catalog mailbox key publish/fetch, status and consume (not upload, which catalogUploadBytes bounds).
  // Hourly Owner checks and debounced Sub publishes need a handful per hour; this only stops runaway loops.
  deviceMailboxOps: { windowSeconds: 3600, maxCount: 120 },
  // collar/rulebook key publish/fetch and consume, both channels (uploads are bounded by rulebookUploadBytes).
  deviceRulebookOps: { windowSeconds: 3600, maxCount: 120 },
  // collar/pairing + collar/pairing-recovery: unauthenticated capability reads that a guesser would hammer.
  // Codes are 80/128 random bits, so these are defense in depth, generous for honest polling.
  originInvitationLookup: { windowSeconds: 3600, maxCount: 120 },
  originBackupFetch: { windowSeconds: 3600, maxCount: 20 },
  deviceBackupWrite: { windowSeconds: 3600, maxCount: 60 },
  // State changes plus a check-in every couple of hours; this only stops runaway loops.
  deviceCollarStatus: { windowSeconds: 3600, maxCount: 30 },
  endpointGlobal: { windowSeconds: 60, maxCount: 6000 },
  catalogUploadBytes: { windowSeconds: 3600, maxCount: 20, maxBytes: 8 * 1024 * 1024 },
  // catalog-pictures: a first publish after updating sends every picture once (dozens at most 64 KB each); after
  // that only changed pictures go up.
  pictureUploadBytes: { windowSeconds: 3600, maxCount: 300, maxBytes: 8 * 1024 * 1024 },
  // Picture reference syncs (one per publish) and the Owner's batched fetches.
  devicePictureOps: { windowSeconds: 3600, maxCount: 120 },
  // The per-channel upload interval already allows at most 60 rulebooks or 6 reports per hour per pair.
  rulebookUploadBytes: { windowSeconds: 3600, maxCount: 60, maxBytes: 2 * 1024 * 1024 },
  // Counted in estimated D1 rows written (ROUTE_WEIGHTS), not requests: D1 Free fails every query after 100k
  // rows written in a UTC day, long before Workers Free's 100k requests. 70k + 15k leaves ~15k for the cron's own
  // writes and estimation error. Revocations have a separate reserve so ordinary abuse cannot starve safety traffic.
  globalDailyWork: { windowSeconds: 86400, maxCount: 70000 },
  globalDailySafety: { windowSeconds: 86400, maxCount: 15000 },
} as const satisfies Record<string, QuotaLimit>;

export type QuotaName = keyof typeof QUOTA_LIMITS;

export const CIRCUIT_BREAKER_OPEN_RATIO = 0.95;

function windowStart(now: number, windowSeconds: number): number {
  return Math.floor(now / windowSeconds) * windowSeconds;
}

/**
 * Atomically adds `units` to the counter for one (name, scopeId) pair and throws
 * RelayError("rate_limited") with a computed Retry-After if that would exceed the
 * configured ceiling. A rejected charge leaves the counter where it was, so the
 * window stays full and every retry is rejected until it rolls over.
 */
export async function enforceQuota(
  env: Env,
  name: QuotaName,
  scopeId: string,
  incrementBytes = 0,
  units = 1,
): Promise<void> {
  const limit: QuotaLimit = QUOTA_LIMITS[name];
  const now = nowSeconds();
  const bucket = windowStart(now, limit.windowSeconds);
  const scope = `${name}:${scopeId}`;
  const retryAfterSeconds = bucket + limit.windowSeconds - now;
  if (units > limit.maxCount) throw new RelayError("rate_limited", retryAfterSeconds);

  const maxBytes = limit.maxBytes ?? Number.MAX_SAFE_INTEGER;
  const row = await env.RELAY_DB.prepare(
    `INSERT INTO quota_counters (scope, window_start, count, bytes) VALUES (?1, ?2, ?6, ?3)
     ON CONFLICT (scope, window_start) DO UPDATE SET
       count = count + excluded.count,
       bytes = bytes + excluded.bytes
     WHERE quota_counters.count + excluded.count <= ?4 AND quota_counters.bytes + excluded.bytes <= ?5
     RETURNING count, bytes`,
  )
    .bind(scope, bucket, incrementBytes, limit.maxCount, maxBytes, units)
    .first<{ count: number; bytes: number }>();

  if (!row) {
    throw new RelayError("rate_limited", retryAfterSeconds);
  }
}

/**
 * Units to charge one admission when only one in `factor` admissions writes the counter: `weight * factor` for the
 * sampled one, 0 for the rest, so the expected daily total equals charging every request. `random` is in [0, 1).
 */
export function sampledUnits(weight: number, factor: number, random: number): number {
  if (!(factor > 1)) return weight;
  return random < 1 / factor ? weight * factor : 0;
}

/** Read-only counterpart of enforceQuota for an admission that isn't charged, so a spent pool still rejects it. */
export async function assertQuotaRemaining(env: Env, name: QuotaName, scopeId: string, units: number): Promise<void> {
  const limit: QuotaLimit = QUOTA_LIMITS[name];
  const now = nowSeconds();
  const bucket = windowStart(now, limit.windowSeconds);
  const row = await env.RELAY_DB.prepare(`SELECT count FROM quota_counters WHERE scope = ?1 AND window_start = ?2`)
    .bind(`${name}:${scopeId}`, bucket)
    .first<{ count: number }>();
  if ((row?.count ?? 0) + units > limit.maxCount) throw new RelayError("rate_limited", bucket + limit.windowSeconds - now);
}

/**
 * Global circuit breaker: gates only new non-safety work (invitations, catalog requests, uploads, key publishes,
 * backup writes) once the work pool is nearly spent, leaving its last units for traffic between existing pairs.
 * Revocation publish/check and already-accepted retrieval are never gated here so panic/unpair stays available.
 */
export async function assertCircuitBreakerClosed(env: Env): Promise<void> {
  if (env.CIRCUIT_BREAKER_FORCE_OPEN === "true") {
    throw new RelayError("service_unavailable", 300);
  }
  const limit = QUOTA_LIMITS.globalDailyWork;
  const now = nowSeconds();
  const bucket = windowStart(now, limit.windowSeconds);
  const row = await env.RELAY_DB.prepare(
    `SELECT count FROM quota_counters WHERE scope = ?1 AND window_start = ?2`,
  )
    .bind(`globalDailyWork:all`, bucket)
    .first<{ count: number }>();
  if (row && row.count >= limit.maxCount * CIRCUIT_BREAKER_OPEN_RATIO) {
    throw new RelayError("service_unavailable", bucket + limit.windowSeconds - now);
  }
}
