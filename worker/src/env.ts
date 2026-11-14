import type { DeviceHub } from "./hub/DeviceHub";

export interface Env {
  RELAY_DB: D1Database;
  RELAY_CATALOG_BUCKET: R2Bucket;
  ORIGIN_RATE_LIMITER: RateLimit;
  DEVICE_HUB: DurableObjectNamespace<DeviceHub>;
  RELAY_ENVIRONMENT: "local" | "testing" | "staging" | "production";
  CIRCUIT_BREAKER_FORCE_OPEN: string;
  /** One in N work-pool admissions is charged N times its weight; "1" charges every request (local, tests). */
  GLOBAL_CHARGE_SAMPLING?: string;
  /** Same for the live budget; defaults to 16. */
  LIVE_CHARGE_SAMPLING?: string;
}
