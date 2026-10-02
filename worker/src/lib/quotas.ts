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
  // collar/pairing + collar/pairing-recovery: unauthenticated capability reads that a guesser would hammer.
  // Codes are 80/128 random bits, so these are defense in depth, generous for honest polling.
  originInvitationLookup: { windowSeconds: 3600, maxCount: 120 },
  originBackupFetch: { windowSeconds: 3600, maxCount: 20 },
  deviceBackupWrite: { windowSeconds: 3600, maxCount: 60 },
  // State changes plus a check-in every couple of hours; this only stops runaway loops.
  deviceCollarStatus: { windowSeconds: 3600, maxCount: 30 },
  endpointGlobal: { windowSeconds: 60, maxCount: 6000 },
  catalogUploadBytes: { windowSeconds: 3600, maxCount: 20, maxBytes: 8 * 1024 * 1024 },
  // Together these cap application traffic at 35k/day, under Workers Free's 100k/day request ceiling.
  // D1 Free's 100k/day row-write ceiling is the tighter one: on top of its own writes, a request updates
  // this counter, and a signed one also inserts (and the cron later deletes) a nonce and touches its
  // per-device quotas. Index entries bill as extra rows, so keep indexes off these tables. Revocations have a separate reserve so ordinary abuse cannot starve safety traffic.
  globalDailyWork: { windowSeconds: 86400, maxCount: 25000 },
  globalDailySafety: { windowSeconds: 86400, maxCount: 10000 },
} as const satisfies Record<string, QuotaLimit>;

export type QuotaName = keyof typeof QUOTA_LIMITS;

function windowStart(now: number, windowSeconds: number): number {
  return Math.floor(now / windowSeconds) * windowSeconds;
}

/**
 * Atomically increments the counter for one (name, scopeId) pair and throws
 * RelayError("rate_limited") with a computed Retry-After if this increment
 * exceeds the configured ceiling. The increment is applied even when it
 * pushes the counter over the limit so a caller cannot dodge the ceiling by
 * retrying the same window; it just keeps getting rejected until the window
 * rolls over.
 */
export async function enforceQuota(
  env: Env,
  name: QuotaName,
  scopeId: string,
  incrementBytes = 0,
): Promise<void> {
  const limit: QuotaLimit = QUOTA_LIMITS[name];
  const now = nowSeconds();
  const bucket = windowStart(now, limit.windowSeconds);
  const scope = `${name}:${scopeId}`;

  const maxBytes = limit.maxBytes ?? Number.MAX_SAFE_INTEGER;
  const row = await env.RELAY_DB.prepare(
    `INSERT INTO quota_counters (scope, window_start, count, bytes) VALUES (?1, ?2, 1, ?3)
     ON CONFLICT (scope, window_start) DO UPDATE SET
       count = count + 1,
       bytes = bytes + excluded.bytes
     WHERE quota_counters.count < ?4 AND quota_counters.bytes + excluded.bytes <= ?5
     RETURNING count, bytes`,
  )
    .bind(scope, bucket, incrementBytes, limit.maxCount, maxBytes)
    .first<{ count: number; bytes: number }>();

  const retryAfterSeconds = bucket + limit.windowSeconds - now;

  if (!row) {
    throw new RelayError("rate_limited", retryAfterSeconds);
  }
}

/**
 * Global circuit breaker: trips before configured free-tier/spending
 * ceilings are exceeded (spec: "Operational ceiling is reached"). It gates
 * only new non-safety work (invitation creation, catalog request creation);
 * revocation publish/check and already-accepted retrieval are never gated
 * here so panic/unpair stays available even when the breaker is open.
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
  if (row && row.count >= limit.maxCount) {
    throw new RelayError("service_unavailable", bucket + limit.windowSeconds - now);
  }
}
