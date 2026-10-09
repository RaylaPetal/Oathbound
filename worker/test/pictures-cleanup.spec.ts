import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { runScheduledCleanup } from "../src/scheduled";
import { freshPair } from "./mailbox-helpers";
import { pictureId, syncPictures, uploadPicture } from "./picture-helpers";

async function held(id: string): Promise<{ row: boolean; object: boolean }> {
  const row = await env.RELAY_DB.prepare(`SELECT 1 FROM catalog_pictures WHERE picture_id = ?1`).bind(id).first();
  const objects = (await env.RELAY_CATALOG_BUCKET.list({ prefix: "pictures/" })).objects;
  return { row: row !== null, object: objects.some((o) => o.key.endsWith(`/${id}`)) };
}

describe("catalog picture cleanup", () => {
  it("deletes a picture dropped longer than the grace period, keeping a recently dropped one", async () => {
    const p = await freshPair();
    const [kept, old, recent] = [pictureId(), pictureId(), pictureId()];
    for (const id of [kept!, old!, recent!]) await uploadPicture(p, id);
    await syncPictures(p, [kept!]);
    await env.RELAY_DB.prepare(`UPDATE catalog_pictures SET unreferenced_since = unreferenced_since - 90000 WHERE picture_id = ?1`).bind(old).run();

    await runScheduledCleanup(env);
    expect(await held(kept!)).toEqual({ row: true, object: true });
    expect(await held(recent!)).toEqual({ row: true, object: true });
    expect(await held(old!)).toEqual({ row: false, object: false });
  });

  it("deletes a picture no sync has referenced for 30 days", async () => {
    const p = await freshPair();
    const id = pictureId();
    await uploadPicture(p, id);
    await env.RELAY_DB.prepare(`UPDATE catalog_pictures SET last_referenced_at = last_referenced_at - 2600000 WHERE picture_id = ?1`).bind(id).run();
    await runScheduledCleanup(env);
    expect(await held(id)).toEqual({ row: false, object: false });
  });

  it("deletes every picture of a revoked pair epoch", async () => {
    const p = await freshPair();
    const [a, b] = [pictureId(), pictureId()];
    await uploadPicture(p, a!);
    await uploadPicture(p, b!);
    await env.RELAY_DB.prepare(`UPDATE pairs SET revoked_at = ?1 WHERE pair_id_hash = ?2`).bind(Math.floor(Date.now() / 1000), p.pairIdHash).run();
    await runScheduledCleanup(env);
    expect(await held(a!)).toEqual({ row: false, object: false });
    expect(await held(b!)).toEqual({ row: false, object: false });
  });

  it("keeps a just-written object that has no row yet", async () => {
    const key = `pictures/${"a".repeat(64)}/0/${pictureId()}`;
    await env.RELAY_CATALOG_BUCKET.put(key, new Uint8Array(32));
    await runScheduledCleanup(env);
    expect(await env.RELAY_CATALOG_BUCKET.head(key)).not.toBeNull();
  });
});
