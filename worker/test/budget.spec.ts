import { env } from "cloudflare:test";
import { describe, expect, it, vi } from "vitest";
import { CIRCUIT_BREAKER_OPEN_RATIO, QUOTA_LIMITS } from "../src/lib/quotas";
import { ROUTE_WEIGHTS } from "../src/lib/routeWeights";
import { runScheduledCleanup } from "../src/scheduled";
import { createInvitation, deviceKeyId, genSigningKeyPair, relayFetch, signEnvelope, signedFetch } from "./helpers";
import { freshPair, publishKey, upload, type Pair } from "./mailbox-helpers";

type Pool = "globalDailyWork" | "globalDailySafety";

const today = () => Math.floor(Date.now() / 1000 / 86400) * 86400;

async function used(pool: Pool): Promise<number> {
  const row = await env.RELAY_DB.prepare(`SELECT count FROM quota_counters WHERE scope = ?1 AND window_start = ?2`)
    .bind(`${pool}:all`, today())
    .first<{ count: number }>();
  return row?.count ?? 0;
}

async function seed(pool: Pool, count: number): Promise<void> {
  await env.RELAY_DB.prepare(
    `INSERT INTO quota_counters (scope, window_start, count, bytes) VALUES (?1, ?2, ?3, 0)
     ON CONFLICT (scope, window_start) DO UPDATE SET count = excluded.count`,
  )
    .bind(`${pool}:all`, today(), count)
    .run();
}

const pairStatus = (p: Pair) => signedFetch(`/v1/pairs/${p.pairIdHash}?epoch=0`, "GET", undefined, p.owner.privateKey, p.ownerId);

async function revoke(p: Pair) {
  const now = Math.floor(Date.now() / 1000);
  const unsigned = {
    type: "revocation",
    schemaVersion: 1,
    pairIdHash: p.pairIdHash,
    pairEpoch: 0,
    sequence: 1,
    reason: "unpair",
    issuedByDeviceKeyId: p.subId,
    createdAt: now,
    expiresAt: now + 604800,
  };
  const signature = await signEnvelope(p.sub.privateKey, unsigned);
  return signedFetch("/v1/revocations", "POST", { ...unsigned, signature }, p.sub.privateKey, p.subId);
}

describe("daily write budget", () => {
  it("charges each request its route's weight", async () => {
    const p = await freshPair();
    const { envelope } = await publishKey(p);

    let before = await used("globalDailyWork");
    expect((await pairStatus(p)).status).toBe(200);
    expect(await used("globalDailyWork")).toBe(before + ROUTE_WEIGHTS["GET /v1/pairs/:id"]!);

    before = await used("globalDailyWork");
    expect((await upload(p, envelope.receiveKeyId, 1)).r.status).toBe(200);
    expect(await used("globalDailyWork")).toBe(before + ROUTE_WEIGHTS["POST /v1/catalog/mailbox/upload"]!);
  });

  it("rejects work that no longer fits, with a Retry-After ending by the next UTC midnight", async () => {
    const p = await freshPair();
    await seed("globalDailyWork", QUOTA_LIMITS.globalDailyWork.maxCount - 1);

    const r = await pairStatus(p);
    expect(r.status).toBe(429);
    expect(r.json.code).toBe("rate_limited");
    const retryAfter = Number(r.headers.get("retry-after"));
    const untilMidnight = today() + 86400 - Math.floor(Date.now() / 1000);
    expect(retryAfter).toBeGreaterThan(0);
    expect(retryAfter).toBeLessThanOrEqual(untilMidnight);
    expect(await used("globalDailyWork")).toBe(QUOTA_LIMITS.globalDailyWork.maxCount - 1);
  });

  it("keeps revocations working after the work pool is spent, without ordinary traffic touching the reserve", async () => {
    const p = await freshPair();
    await seed("globalDailyWork", QUOTA_LIMITS.globalDailyWork.maxCount);

    const safetyBefore = await used("globalDailySafety");
    expect((await pairStatus(p)).status).toBe(429);
    expect((await relayFetch("/v1/health")).status).toBe(429);
    expect(await used("globalDailySafety")).toBe(safetyBefore);

    expect((await revoke(p)).status).toBe(200);
    expect(await used("globalDailySafety")).toBe(safetyBefore + ROUTE_WEIGHTS["POST /v1/revocations"]!);
  });

  it("opens the circuit breaker for new work once the work pool is nearly spent", async () => {
    const inviter = await genSigningKeyPair();
    const inviterId = await deviceKeyId(inviter.publicKeyJwk);
    await seed("globalDailyWork", Math.ceil(QUOTA_LIMITS.globalDailyWork.maxCount * CIRCUIT_BREAKER_OPEN_RATIO));

    const r = await createInvitation(inviter, inviterId);
    expect(r.status).toBe(503);
    expect(r.json.code).toBe("service_unavailable");
  });

  it("logs a quota alarm once a pool passes 80%", async () => {
    await seed("globalDailyWork", Math.ceil(QUOTA_LIMITS.globalDailyWork.maxCount * 0.8));
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      await runScheduledCleanup(env);
      const alarms = spy.mock.calls.map((call) => JSON.parse(call[0] as string)).filter((line) => line.event === "quota_alarm");
      expect(alarms).toEqual([expect.objectContaining({ scope: "globalDailyWork", usageRatioPercent: 80 })]);
    } finally {
      spy.mockRestore();
    }
  });

  it("rejects a flooding origin before charging the daily budget", async () => {
    const headers = { "cf-connecting-ip": "203.0.113.77" };
    const before = await used("globalDailyWork");
    let admitted = 0;
    let limited = 0;
    for (let i = 0; i < 140; i++) {
      const status = (await relayFetch("/v1/health", { headers })).status;
      if (status === 200) admitted++;
      else if (status === 429) limited++;
    }
    expect(limited).toBeGreaterThan(0);
    expect(await used("globalDailyWork")).toBe(before + admitted * ROUTE_WEIGHTS["GET /v1/health"]!);
  });
});
