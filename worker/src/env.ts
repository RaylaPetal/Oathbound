export interface Env {
  RELAY_DB: D1Database;
  RELAY_CATALOG_BUCKET: R2Bucket;
  ORIGIN_RATE_LIMITER: RateLimit;
  RELAY_ENVIRONMENT: "local" | "staging" | "production";
  CIRCUIT_BREAKER_FORCE_OPEN: string;
  /** One in N work-pool admissions is charged N times its weight; "1" charges every request (local, tests). */
  GLOBAL_CHARGE_SAMPLING?: string;
}
