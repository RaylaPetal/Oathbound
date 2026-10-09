import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { runScheduledCleanup } from "../src/scheduled";
import { signedFetch, useFetcher } from "./helpers";
import { freshPair } from "./mailbox-helpers";
import { emptyTally, meteredFetcher } from "./write-meter";

const today = () => Math.floor(Date.now() / 1000 / 86400);

describe("daily active marker", () => {
  it("writes a device's marker once a day however often it polls", async () => {
    const p = await freshPair();
    await env.RELAY_DB.prepare(`UPDATE device_keys SET active_day = NULL WHERE device_key_id = ?1`).bind(p.ownerId).run();

    const tally = emptyTally();
    useFetcher(meteredFetcher(tally));
    try {
      for (let i = 0; i < 10; i++) {
        const r = await signedFetch(`/v1/pairs/${p.pairIdHash}?epoch=0`, "GET", undefined, p.owner.privateKey, p.ownerId);
        expect(r.status).toBe(200);
      }
    } finally {
      useFetcher();
    }
    // One global budget counter write per poll (sampling is off locally), plus the one marker.
    expect(tally.rowsWritten).toBe(11);
    const row = await env.RELAY_DB.prepare(`SELECT active_day FROM device_keys WHERE device_key_id = ?1`).bind(p.ownerId).first<{ active_day: number }>();
    expect(row?.active_day).toBe(today());
  });
});

describe("active device count", () => {
  it("counts devices active today or yesterday, and recomputes at most hourly", async () => {
    await env.RELAY_DB.prepare(`DELETE FROM relay_stats`).run();
    await env.RELAY_DB.prepare(`UPDATE device_keys SET active_day = NULL`).run();
    const insert = (id: string, day: number | null) =>
      env.RELAY_DB.prepare(`INSERT INTO device_keys (device_key_id, public_key_jwk, first_seen_at, active_day) VALUES (?1, '{}', 0, ?2)`).bind(id, day).run();
    await insert("today", today());
    await insert("yesterday", today() - 1);
    await insert("last-week", today() - 7);
    await insert("never", null);

    await runScheduledCleanup(env);
    const first = await env.RELAY_DB.prepare(`SELECT active_devices, computed_at FROM relay_stats WHERE id = 1`).first<{ active_devices: number; computed_at: number }>();
    expect(first?.active_devices).toBe(2);

    await insert("late", today());
    await runScheduledCleanup(env);
    const second = await env.RELAY_DB.prepare(`SELECT active_devices, computed_at FROM relay_stats WHERE id = 1`).first<{ active_devices: number; computed_at: number }>();
    expect(second).toEqual(first);
  });
});
