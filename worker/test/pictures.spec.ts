import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { b64url } from "./helpers";
import { freshPair } from "./mailbox-helpers";
import { fetchPictures, pictureId, syncPictures, uploadPicture } from "./picture-helpers";

describe("catalog pictures", () => {
  it("tells the Sub which referenced pictures it lacks, and only those", async () => {
    const p = await freshPair();
    const [a, b, c] = [pictureId(), pictureId(), pictureId()];
    expect((await syncPictures(p, [a!, b!, c!])).json.missing).toEqual([a, b, c]);
    expect((await uploadPicture(p, a!)).status).toBe(200);
    expect((await uploadPicture(p, b!)).status).toBe(200);
    expect((await syncPictures(p, [a!, b!, c!])).json.missing).toEqual([c]);
  });

  it("serves the Owner the exact blob and leaves out ids it does not hold", async () => {
    const p = await freshPair();
    const id = pictureId();
    const bytes = crypto.getRandomValues(new Uint8Array(900));
    await uploadPicture(p, id, bytes);
    const r = await fetchPictures(p, [id, pictureId()]);
    expect(r.status).toBe(200);
    expect(r.json.pictures).toEqual([{ pictureId: id, ciphertextBase64Url: b64url(bytes) }]);
  });

  it("treats a re-upload of a held id as done without storing it twice", async () => {
    const p = await freshPair();
    const id = pictureId();
    await uploadPicture(p, id);
    expect((await uploadPicture(p, id)).status).toBe(200);
    const rows = await env.RELAY_DB.prepare(`SELECT COUNT(*) AS n FROM catalog_pictures WHERE pair_id_hash = ?1`).bind(p.pairIdHash).first<{ n: number }>();
    expect(rows?.n).toBe(1);
  });

  it("marks a picture the Sub stopped referencing, and clears the mark when it is referenced again", async () => {
    const p = await freshPair();
    const [a, b] = [pictureId(), pictureId()];
    await uploadPicture(p, a!);
    await uploadPicture(p, b!);
    const since = (id: string) =>
      env.RELAY_DB.prepare(`SELECT unreferenced_since FROM catalog_pictures WHERE picture_id = ?1`).bind(id).first<{ unreferenced_since: number | null }>();

    await syncPictures(p, [a!]);
    expect((await since(a!))?.unreferenced_since).toBeNull();
    expect((await since(b!))?.unreferenced_since).not.toBeNull();
    await syncPictures(p, [a!, b!]);
    expect((await since(b!))?.unreferenced_since).toBeNull();
  });

  it("refuses an oversized picture and one past the pair's picture cap", async () => {
    const p = await freshPair();
    expect((await uploadPicture(p, pictureId(), new Uint8Array(65537))).status).toBe(413);

    const now = Math.floor(Date.now() / 1000);
    const fill = Array.from({ length: 256 }, (_, i) =>
      env.RELAY_DB.prepare(
        `INSERT INTO catalog_pictures (pair_id_hash, pair_epoch, picture_id, size_bytes, created_at, last_referenced_at) VALUES (?1, 0, ?2, 10, ?3, ?3)`,
      ).bind(p.pairIdHash, `filler${String(i).padStart(16, "0")}`, now),
    );
    await env.RELAY_DB.batch(fill);
    expect((await uploadPicture(p, pictureId())).status).toBe(413);
  });
});

describe("catalog pictures: authorization", () => {
  it("accepts sync and upload only from the Sub, and fetch only from the Owner", async () => {
    const p = await freshPair();
    const id = pictureId();
    expect((await syncPictures(p, [id], "owner")).status).toBe(401);
    expect((await uploadPicture(p, id, undefined, "owner")).status).toBe(401);
    await uploadPicture(p, id);
    expect((await fetchPictures(p, [id], "sub")).status).toBe(401);

    const stranger = await freshPair();
    expect((await fetchPictures({ ...stranger, pairIdHash: p.pairIdHash }, [id])).status).toBe(401);
  });

  it("rejects a revoked pair and an epoch that does not exist", async () => {
    const p = await freshPair();
    expect((await uploadPicture(p, pictureId(), undefined, "sub", 5)).status).toBe(401);
    await env.RELAY_DB.prepare(`UPDATE pairs SET revoked_at = ?1 WHERE pair_id_hash = ?2`).bind(Math.floor(Date.now() / 1000), p.pairIdHash).run();
    expect((await syncPictures(p, [])).status).toBe(401);
    expect((await uploadPicture(p, pictureId())).status).toBe(401);
    expect((await fetchPictures(p, [pictureId()])).status).toBe(401);
  });
});
