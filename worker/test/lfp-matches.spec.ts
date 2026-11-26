import { env } from "cloudflare:test";
import { afterEach, describe, expect, it } from "vitest";
import { connect, type TestSocket } from "./socket-helpers";
import { browseIds, del, get, like, matched, newBareDevice, post, withProfile, spreadOrigins } from "./lfp-helpers";

spreadOrigins();

const open: TestSocket[] = [];
afterEach(() => { for (const s of open.splice(0)) s.close(); });

describe("likes", () => {
  it("keeps a one-sided like private to the liker", async () => {
    const a = await withProfile();
    const b = await withProfile();
    const r = await like(a.device, b.profile.profileId);
    expect(r.json).toMatchObject({ type: "lfp-like-result", matched: false });
    expect(browseIds(await get(a.device, "/v1/lfp/likes"))).toEqual([b.profile.profileId]);
    expect((await get(b.device, "/v1/lfp/likes")).json.profiles).toEqual([]);
    expect((await get(b.device, "/v1/lfp/matches")).json.matches).toEqual([]);

    await del(a.device, `/v1/lfp/likes/${b.profile.profileId}`);
    expect((await get(a.device, "/v1/lfp/likes")).json.profiles).toEqual([]);
  });

  it("refuses a like from a device without its own profile", async () => {
    const b = await withProfile();
    const bare = await newBareDevice();
    expect((await like(bare, b.profile.profileId)).status).toBe(400);
  });

  it("caps likes at 100 a day", async () => {
    const a = await withProfile();
    const target = await withProfile();
    await env.RELAY_DB.prepare(`INSERT INTO quota_counters (scope, window_start, count, bytes) VALUES (?1, ?2, 100, 0)`)
      .bind(`lfpLike:${a.device.id}`, Math.floor(Date.now() / 1000 / 86400) * 86400)
      .run();
    const r = await like(a.device, target.profile.profileId);
    expect(r.status).toBe(429);
    expect(Number(r.headers.get("retry-after"))).toBeGreaterThan(0);
  });
});

describe("matches", () => {
  it("makes a match on a mutual like, hints both and never shows a device key", async () => {
    const a = await withProfile({ displayName: "Alpha" });
    const b = await withProfile({ displayName: "Bravo" });
    const sa = await connect(a.device, ["chat", "match"]);
    const sb = await connect(b.device, ["chat", "match"]);
    open.push(sa, sb);

    await like(a.device, b.profile.profileId);
    const r = await like(b.device, a.profile.profileId);
    expect(r.json.matched).toBe(true);
    expect(r.json.match.peer.fields.displayName).toBe("Alpha");
    expect(await sa.next()).toEqual({ type: "hint", kind: "lfp" });
    expect(await sb.next()).toEqual({ type: "hint", kind: "lfp" });

    const forA = await get(a.device, "/v1/lfp/matches");
    expect(forA.json.matches).toHaveLength(1);
    expect(forA.json.matches[0]).toMatchObject({ matchId: r.json.match.matchId, endedAt: null });
    expect(forA.json.matches[0].peer.fields.displayName).toBe("Bravo");
    expect(forA.json.matches[0].peerMatchPublicKey).toMatchObject({ kty: "EC", crv: "P-256" });
    expect(forA.json.matches[0].side).toBe(a.device.id < b.device.id ? "a" : "b");
    const text = JSON.stringify(forA.json) + JSON.stringify(r.json);
    expect(text).not.toContain(a.device.id);
    expect(text).not.toContain(b.device.id);
    // The likes became the match.
    expect((await get(a.device, "/v1/lfp/likes")).json.profiles).toEqual([]);
  });

  it("creates a single match when both like at the same time", async () => {
    const a = await withProfile();
    const b = await withProfile();
    const [x, y] = await Promise.all([like(a.device, b.profile.profileId), like(b.device, a.profile.profileId)]);
    expect([x.status, y.status]).toEqual([200, 200]);
    expect(x.json.matched || y.json.matched).toBe(true);
    const rows = await env.RELAY_DB.prepare(`SELECT COUNT(*) AS n FROM lfp_matches WHERE profile_a IN (?1, ?2)`)
      .bind(a.profile.profileId, b.profile.profileId).first<{ n: number }>();
    expect(rows?.n).toBe(1);
  });

  it("unmatch ends the match for both and only a member can do it", async () => {
    const m = await matched();
    const stranger = await withProfile();
    expect((await del(stranger.device, `/v1/lfp/matches/${m.matchId}`)).status).toBe(404);
    expect((await del(m.b.device, `/v1/lfp/matches/${m.matchId}`)).status).toBe(200);
    for (const d of [m.a.device, m.b.device]) {
      const r = await get(d, "/v1/lfp/matches");
      expect(r.json.matches[0].endedAt).toBeTypeOf("number");
    }
  });
});

describe("blocks", () => {
  it("ends the match, hides both ways, survives a new code and is removable", async () => {
    const m = await matched();
    const r = await post(m.a.device, "/v1/lfp/blocks", { type: "lfp-block", schemaVersion: 1, matchId: m.matchId });
    expect(r.status).toBe(200);
    expect((await get(m.b.device, "/v1/lfp/matches")).json.matches[0].endedAt).toBeTypeOf("number");

    const fresh = await post(m.b.device, "/v1/lfp/profile/code");
    expect((await get(m.a.device, `/v1/lfp/code/${fresh.json.profile.code}`)).status).toBe(404);
    expect((await get(m.b.device, `/v1/lfp/code/${m.a.profile.code}`)).status).toBe(404);
    expect((await like(m.b.device, m.a.profile.profileId)).status).toBe(404);

    const list = await get(m.a.device, "/v1/lfp/blocks");
    expect(list.json.blocks).toHaveLength(1);
    expect(list.json.blocks[0].displayName).toBe("Bravo");
    expect(JSON.stringify(list.json)).not.toContain(m.b.device.id);
    await del(m.a.device, `/v1/lfp/blocks/${list.json.blocks[0].blockId}`);
    expect((await get(m.a.device, `/v1/lfp/code/${fresh.json.profile.code}`)).status).toBe(200);
  });

  it("survives the blocked person deleting and recreating their profile", async () => {
    const a = await withProfile();
    const b = await withProfile();
    await post(a.device, "/v1/lfp/blocks", { type: "lfp-block", schemaVersion: 1, profileId: b.profile.profileId });
    await del(b.device, "/v1/lfp/profile");
    const again = await post(b.device, "/v1/lfp/me", { type: "lfp-me", schemaVersion: 1, devicePublicKey: b.device.key.publicKeyJwk });
    expect(again.status).toBe(200);
    const { putProfile } = await import("./lfp-helpers");
    const recreated = await putProfile(b.device);
    expect(browseIds(await get(a.device, "/v1/lfp/browse"))).not.toContain(recreated.json.profile.profileId);
  });
});
