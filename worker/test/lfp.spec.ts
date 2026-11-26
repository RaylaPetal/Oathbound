import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import fixture from "../../protocol/fixtures/valid/lfp-profile.json";
import { b64url, relayFetch, signedFetch } from "./helpers";
import { newDevice } from "./socket-helpers";
import { browseIds, del, get, like, matched, me, newBareDevice, PNG, post, putProfile, withProfile, spreadOrigins } from "./lfp-helpers";

spreadOrigins();

describe("profile", () => {
  it("registers a new device and accepts the protocol fixture as a profile", async () => {
    const d = await newDevice();
    const intro = await me(d);
    expect(intro.status).toBe(200);
    expect(intro.json.profile).toBeNull();

    const r = await signedFetch("/v1/lfp/profile", "PUT", fixture, d.key.privateKey, d.id);
    expect(r.status).toBe(200);
    expect(r.json.profile).toMatchObject({
      fields: fixture.fields, visibility: "public", hidden: false, listed: true, picture: null,
    });
    expect(r.json.profile.profileId).toMatch(/^[0-9a-f]{32}$/);
    expect(r.json.profile.code).toMatch(/^[0-9A-HJKMNP-TV-Z]{10}$/);
    expect((await me(d)).json.profile.profileId).toBe(r.json.profile.profileId);
  });

  it("refuses a profile without the 18+ attestation or with invalid fields", async () => {
    const d = await newBareDevice();
    expect((await putProfile(d, {}, { adultAttested: false })).status).toBe(400);
    expect((await putProfile(d, { displayName: "V" })).status).toBe(400);
    expect((await putProfile(d, { bio: "x".repeat(1001) })).status).toBe(400);
    expect((await putProfile(d, { race: "goblin" })).status).toBe(400);
    expect((await putProfile(d, { displayName: "Bad\u0007Name" })).status).toBe(400);
    expect((await putProfile(d, {}, { fields: { ...fixture.fields, world: "Odin" } })).status).toBe(400);
    expect((await me(d)).json.profile).toBeNull();
  });

  it("refuses a profile from a device that never introduced itself", async () => {
    const d = await newDevice();
    expect((await putProfile(d)).status).toBe(401);
  });

  it("invalidates the old code when a new one is made", async () => {
    const owner = await withProfile();
    const viewer = await newBareDevice();
    expect((await get(viewer, `/v1/lfp/code/${owner.profile.code}`)).status).toBe(200);
    const fresh = await post(owner.device, "/v1/lfp/profile/code");
    expect(fresh.json.profile.code).not.toBe(owner.profile.code);
    expect((await get(viewer, `/v1/lfp/code/${owner.profile.code}`)).status).toBe(404);
    expect((await get(viewer, `/v1/lfp/code/${fresh.json.profile.code}`)).json.profile.profileId).toBe(owner.profile.profileId);
  });

  it("shows a private profile by code but never in browse", async () => {
    const hidden = await withProfile({}, { visibility: "private" });
    const viewer = await newBareDevice();
    expect(browseIds(await get(viewer, "/v1/lfp/browse"))).not.toContain(hidden.profile.profileId);
    const r = await get(viewer, `/v1/lfp/code/${hidden.profile.code.toLowerCase().slice(0, 5)}-${hidden.profile.code.slice(5)}`);
    expect(r.status).toBe(200);
    expect(r.json.profile).not.toHaveProperty("code");
    expect(JSON.stringify(r.json)).not.toContain(hidden.device.id);
  });

  it("hides a profile from browse and code lookup until unhidden", async () => {
    const p = await withProfile();
    const viewer = await newBareDevice();
    await putProfile(p.device, {}, { hidden: true });
    expect(browseIds(await get(viewer, "/v1/lfp/browse"))).not.toContain(p.profile.profileId);
    expect((await get(viewer, `/v1/lfp/code/${p.profile.code}`)).status).toBe(404);
    await putProfile(p.device, {}, { hidden: false });
    expect(browseIds(await get(viewer, "/v1/lfp/browse"))).toContain(p.profile.profileId);
  });

  it("drops out of browse after 60 days without a refresh and comes back on refresh", async () => {
    const p = await withProfile();
    const viewer = await newBareDevice();
    await env.RELAY_DB.prepare(`UPDATE lfp_profiles SET active_at = active_at - 61 * 86400 WHERE profile_id = ?1`).bind(p.profile.profileId).run();
    expect(browseIds(await get(viewer, "/v1/lfp/browse"))).not.toContain(p.profile.profileId);
    expect((await get(viewer, `/v1/lfp/code/${p.profile.code}`)).status).toBe(404);
    expect((await me(p.device)).json.profile.listed).toBe(false);
    const refreshed = await post(p.device, "/v1/lfp/profile/refresh");
    expect(refreshed.json.profile.listed).toBe(true);
    expect(browseIds(await get(viewer, "/v1/lfp/browse"))).toContain(p.profile.profileId);
  });

  it("deleting removes the profile and its pending likes but keeps matches", async () => {
    const m = await matched();
    const c = await withProfile({ displayName: "Charlie" });
    await like(m.a.device, c.profile.profileId);
    await like(c.device, m.a.profile.profileId);
    // That second like matched; a fresh one-sided like to delete:
    const d = await withProfile({ displayName: "Delta" });
    await like(d.device, m.a.profile.profileId);

    expect((await del(m.a.device, "/v1/lfp/profile")).status).toBe(200);
    expect((await me(m.a.device)).json.profile).toBeNull();
    const likes = await env.RELAY_DB.prepare(`SELECT COUNT(*) AS n FROM lfp_likes WHERE from_profile = ?1 OR to_profile = ?1`)
      .bind(m.a.profile.profileId).first<{ n: number }>();
    expect(likes?.n).toBe(0);
    const matches = await get(m.b.device, "/v1/lfp/matches");
    expect(matches.json.matches[0]).toMatchObject({ matchId: m.matchId, endedAt: null });
    expect(matches.json.matches[0].peer.fields.displayName).toBe("Alpha");
  });
});

describe("pictures", () => {
  it("serves an uploaded picture and stops serving it when replaced or deleted", async () => {
    const p = await withProfile();
    const up = await post(p.device, "/v1/lfp/profile/picture", { type: "lfp-picture", schemaVersion: 1, contentType: "image/png", dataBase64Url: b64url(PNG) });
    expect(up.status).toBe(200);
    const first = up.json.profile.picture as string;
    const served = await relayFetch(first);
    expect(served.status).toBe(200);
    expect(served.headers.get("content-type")).toBe("image/png");
    expect(new Uint8Array(await served.arrayBuffer())).toEqual(PNG);

    const again = await post(p.device, "/v1/lfp/profile/picture", { type: "lfp-picture", schemaVersion: 1, contentType: "image/png", dataBase64Url: b64url(PNG) });
    expect(again.json.profile.picture).not.toBe(first);
    expect((await relayFetch(first)).status).toBe(404);

    await del(p.device, "/v1/lfp/profile");
    expect((await relayFetch(again.json.profile.picture)).status).toBe(404);
  });

  it("refuses bytes that aren't the declared image type", async () => {
    const p = await withProfile();
    const r = await post(p.device, "/v1/lfp/profile/picture", { type: "lfp-picture", schemaVersion: 1, contentType: "image/jpeg", dataBase64Url: b64url(PNG) });
    expect(r.status).toBe(400);
  });
});

describe("browse", () => {
  it("excludes own, liked, matched and blocked profiles", async () => {
    const viewer = await withProfile({ displayName: "Viewer" });
    const liked = await withProfile({ displayName: "Liked" });
    const blocked = await withProfile({ displayName: "Blocked" });
    const blocker = await withProfile({ displayName: "Blocker" });
    const shown = await withProfile({ displayName: "Shown" });
    const mate = await withProfile({ displayName: "Mate" });
    await like(viewer.device, liked.profile.profileId);
    await post(viewer.device, "/v1/lfp/blocks", { type: "lfp-block", schemaVersion: 1, profileId: blocked.profile.profileId });
    await post(blocker.device, "/v1/lfp/blocks", { type: "lfp-block", schemaVersion: 1, profileId: viewer.profile.profileId });
    await like(mate.device, viewer.profile.profileId);
    expect((await like(viewer.device, mate.profile.profileId)).json.matched).toBe(true);

    const ids = browseIds(await get(viewer.device, "/v1/lfp/browse"));
    expect(ids).toContain(shown.profile.profileId);
    for (const gone of [viewer, liked, blocked, blocker, mate]) expect(ids).not.toContain(gone.profile.profileId);
    expect(browseIds(await get(blocked.device, "/v1/lfp/browse"))).not.toContain(viewer.profile.profileId);
  });

  it("filters by role, seeking, data center, race and gender", async () => {
    const viewer = await newBareDevice();
    const owner = await withProfile({ role: "owner", seeking: "sub", dataCenter: "light", race: "viera", gender: "man" });
    const switchy = await withProfile({ role: "switch", seeking: "either", dataCenter: null, race: "viera", gender: "man" });
    const sub = await withProfile({ role: "sub", seeking: "owner", dataCenter: "light", race: "viera", gender: "man" });
    const elsewhere = await withProfile({ role: "owner", seeking: "sub", dataCenter: "aether", race: "viera", gender: "man" });

    const ids = browseIds(await get(viewer, "/v1/lfp/browse?role=owner&seeking=sub&dataCenter=light&race=viera&gender=man"));
    expect(ids).toContain(owner.profile.profileId);
    expect(ids).toContain(switchy.profile.profileId);
    expect(ids).not.toContain(sub.profile.profileId);
    expect(ids).not.toContain(elsewhere.profile.profileId);
    expect((await get(viewer, "/v1/lfp/browse?race=goblin")).status).toBe(400);
  });

  it("walks pages with a cursor, newest first, without repeats", async () => {
    const viewer = await newBareDevice();
    const made: string[] = [];
    const now = Math.floor(Date.now() / 1000);
    for (let i = 0; i < 23; i++) {
      const id = i.toString(16).padStart(32, "0");
      made.push(id);
      // Seeded directly: 23 sign-ups in one test would trip the per-origin limit. Some share an active_at.
      await env.RELAY_DB.prepare(
        `INSERT INTO lfp_profiles (profile_id, device_key_id, code, visibility, hidden, display_name, race, gender, role, seeking, bio,
           match_public_key, adult_attested_at, created_at, updated_at, active_at)
         VALUES (?1, ?2, ?3, 'public', 0, 'Seed', 'hrothgar', 'nonbinary', 'owner', 'sub', '', '{}', ?4, ?4, ?4, ?5)`,
      ).bind(id, id.padStart(64, "f"), `SEED${i.toString().padStart(6, "0")}`, now, now - Math.floor(i / 3)).run();
    }
    const first = await get(viewer, "/v1/lfp/browse?race=hrothgar&gender=nonbinary");
    expect(first.json.profiles).toHaveLength(20);
    expect(first.json.nextCursor).toMatch(/^\d+\.[0-9a-f]{32}$/);
    const second = await get(viewer, `/v1/lfp/browse?race=hrothgar&gender=nonbinary&cursor=${first.json.nextCursor}`);
    const all = [...browseIds(first), ...browseIds(second)];
    expect(new Set(all).size).toBe(all.length);
    for (const id of made) expect(all).toContain(id);
    expect(second.json.nextCursor).toBeNull();
  });
});

describe("limits", () => {
  it("refuses browse pages past the hourly limit with a Retry-After", async () => {
    const viewer = await newBareDevice();
    await env.RELAY_DB.prepare(`INSERT INTO quota_counters (scope, window_start, count, bytes) VALUES (?1, ?2, 60, 0)`)
      .bind(`lfpBrowse:${viewer.id}`, Math.floor(Date.now() / 1000 / 3600) * 3600)
      .run();
    const r = await get(viewer, "/v1/lfp/browse");
    expect(r.status).toBe(429);
    expect(Number(r.headers.get("retry-after"))).toBeGreaterThan(0);
  });
});

describe("scheduled cleanup", () => {
  it("drops unlisted pictures, idle profiles and old ended matches", async () => {
    const { runScheduledCleanup } = await import("../src/scheduled");
    const pictured = await withProfile();
    const up = await post(pictured.device, "/v1/lfp/profile/picture", { type: "lfp-picture", schemaVersion: 1, contentType: "image/png", dataBase64Url: b64url(PNG) });
    const idle = await withProfile();
    const liker = await withProfile();
    await like(liker.device, idle.profile.profileId);
    const m = await matched();
    await del(m.a.device, `/v1/lfp/matches/${m.matchId}`);

    const day = 86400;
    await env.RELAY_DB.prepare(`UPDATE lfp_profiles SET active_at = active_at - 61 * ?2 WHERE profile_id = ?1`).bind(pictured.profile.profileId, day).run();
    await env.RELAY_DB.prepare(`UPDATE lfp_profiles SET active_at = active_at - 366 * ?2 WHERE profile_id = ?1`).bind(idle.profile.profileId, day).run();
    await env.RELAY_DB.prepare(`UPDATE lfp_matches SET ended_at = ended_at - 31 * ?2 WHERE match_id = ?1`).bind(m.matchId, day).run();

    await runScheduledCleanup(env);

    expect((await relayFetch(up.json.profile.picture)).status).toBe(404);
    expect((await me(pictured.device)).json.profile.picture).toBeNull();
    expect((await me(idle.device)).json.profile).toBeNull();
    expect((await get(liker.device, "/v1/lfp/likes")).json.profiles).toEqual([]);
    expect((await get(m.b.device, "/v1/lfp/matches")).json.matches).toEqual([]);
  });
});
