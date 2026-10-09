import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { assertQuotaRemaining, QUOTA_LIMITS, sampledUnits } from "../src/lib/quotas";
import { RelayError } from "../src/lib/errors";

const today = () => Math.floor(Date.now() / 1000 / 86400) * 86400;

describe("sampled work-pool charge", () => {
  it("charges every request its weight when sampling is off", () => {
    expect(sampledUnits(3, 1, 0.99)).toBe(3);
    expect(sampledUnits(3, Number.NaN, 0.99)).toBe(3);
  });

  it("charges one draw in four four times its weight", () => {
    expect(sampledUnits(3, 4, 0.1)).toBe(12);
    expect(sampledUnits(3, 4, 0.3)).toBe(0);
  });

  it("records a total within 10% of the direct total over 4,000 admissions, writing about a quarter as often", () => {
    const weight = 2;
    let total = 0;
    let writes = 0;
    for (let i = 0; i < 4000; i++) {
      const units = sampledUnits(weight, 4, Math.random());
      total += units;
      if (units > 0) writes++;
    }
    expect(Math.abs(total - 4000 * weight)).toBeLessThan(4000 * weight * 0.1);
    expect(writes).toBeGreaterThan(800);
    expect(writes).toBeLessThan(1200);
  });

  it("still rejects an uncharged admission once the pool is spent", async () => {
    const limit = QUOTA_LIMITS.globalDailyWork.maxCount;
    await env.RELAY_DB.prepare(
      `INSERT INTO quota_counters (scope, window_start, count, bytes) VALUES ('globalDailyWork:all', ?1, ?2, 0)
       ON CONFLICT (scope, window_start) DO UPDATE SET count = excluded.count`,
    )
      .bind(today(), limit - 1)
      .run();
    await expect(assertQuotaRemaining(env, "globalDailyWork", "all", 1)).resolves.toBeUndefined();
    await expect(assertQuotaRemaining(env, "globalDailyWork", "all", 2)).rejects.toBeInstanceOf(RelayError);
  });
});
