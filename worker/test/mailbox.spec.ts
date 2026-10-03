import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { b64url, signedFetch } from "./helpers";
import { consume, fetchKey, freshPair, publishKey, randomReceiveKeyId, rewindUploadClock, signedKeyEnvelope, status, upload } from "./mailbox-helpers";

describe("catalog mailbox: receive key", () => {
  it("lets the Owner publish a key and the Sub fetch the same Owner-signed envelope", async () => {
    const p = await freshPair();
    const { r, envelope } = await publishKey(p);
    expect(r.status).toBe(200);

    const fetched = await fetchKey(p);
    expect(fetched.status).toBe(200);
    expect(fetched.json).toEqual({ key: envelope, waitingSnapshotId: null, lastConsumedSnapshotId: null });
  });

  it("reports not_found to the Sub before the Owner ever published a key", async () => {
    const p = await freshPair();
    const fetched = await signedFetch("/v1/catalog/mailbox/key/fetch", "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0 }, p.sub.privateKey, p.subId);
    expect(fetched.status).toBe(404);
  });

  it("rejects a key published by the Sub side, or signed by anyone but the Owner", async () => {
    const p = await freshPair();
    const bySub = await signedFetch("/v1/catalog/mailbox/key", "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0, key: await signedKeyEnvelope(p) }, p.sub.privateKey, p.subId);
    expect(bySub.status).toBe(401);

    const forged = await publishKey(p, await signedKeyEnvelope(p, undefined, p.sub));
    expect(forged.r.status).toBe(401);
  });

  it("rejects the Owner fetching the key through the Sub-only route", async () => {
    const p = await freshPair();
    await publishKey(p);
    const fetched = await signedFetch("/v1/catalog/mailbox/key/fetch", "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0 }, p.owner.privateKey, p.ownerId);
    expect(fetched.status).toBe(401);
  });

  it("rejects every mailbox operation on a revoked pair", async () => {
    const p = await freshPair();
    const { envelope } = await publishKey(p);
    await env.RELAY_DB.prepare(`UPDATE pairs SET revoked_at = ?1 WHERE pair_id_hash = ?2`).bind(Math.floor(Date.now() / 1000), p.pairIdHash).run();

    expect((await publishKey(p)).r.status).toBe(401);
    expect((await upload(p, envelope.receiveKeyId, 1)).r.status).toBe(401);
    expect((await status(p)).status).toBe(401);
  });
});

describe("catalog mailbox: upload", () => {
  it("stores a snapshot encrypted to the current key and reports it in status", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);

    const before = await status(p);
    expect(before.json).toMatchObject({ hasKey: true, hasSnapshot: false, lastUploadAt: null });

    const { r } = await upload(p, key.receiveKeyId, 1);
    expect(r.status).toBe(200);

    const after = await status(p);
    expect(after.json).toMatchObject({ hasKey: true, hasSnapshot: true, snapshotId: 1, receiveKeyId: key.receiveKeyId });
    expect(after.json.lastUploadAt).toBeGreaterThan(0);
  });

  it("rejects an upload before the Owner ever published a key", async () => {
    const p = await freshPair();
    const { r } = await upload(p, randomReceiveKeyId(), 1);
    expect(r.status).toBe(404);
  });

  it("rejects an upload encrypted to a stale receive key with expired", async () => {
    const p = await freshPair();
    await publishKey(p);
    const { r } = await upload(p, randomReceiveKeyId(), 1);
    expect(r.status).toBe(410);
    expect(r.json.code).toBe("expired");
  });

  it("rate-limits a second upload inside the per-pair minimum interval, then accepts it once the interval has passed", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    expect((await upload(p, key.receiveKeyId, 1)).r.status).toBe(200);

    const tooSoon = await upload(p, key.receiveKeyId, 2);
    expect(tooSoon.r.status).toBe(429);
    expect(tooSoon.r.json.code).toBe("rate_limited");
    expect(tooSoon.r.json.retryAfterSeconds).toBeGreaterThan(0);
    expect(tooSoon.r.json.retryAfterSeconds).toBeLessThanOrEqual(1800);
    expect(tooSoon.r.json.retryAfterSeconds).toBeGreaterThan(1700);

    await rewindUploadClock(p);
    expect((await upload(p, key.receiveKeyId, 3)).r.status).toBe(200);
    expect((await status(p)).json.snapshotId).toBe(3);
  });

  it("replaces the waiting snapshot and deletes the previous ciphertext object", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    await upload(p, key.receiveKeyId, 1);
    await rewindUploadClock(p);
    await upload(p, key.receiveKeyId, 2);

    const listed = await env.RELAY_CATALOG_BUCKET.list({ prefix: `mailbox/${p.pairIdHash}/` });
    expect(listed.objects.map((o) => o.key)).toEqual([`mailbox/${p.pairIdHash}/0/2`]);
  });

  it("rejects a replayed or older snapshot id", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    await upload(p, key.receiveKeyId, 5);
    await rewindUploadClock(p);
    const older = await upload(p, key.receiveKeyId, 5);
    expect(older.r.status).toBe(400);
    expect((await status(p)).json.snapshotId).toBe(5);
  });

  it("rejects an upload from the Owner's device", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    const { r } = await upload(p, key.receiveKeyId, 1, { as: p.owner, asId: p.ownerId });
    expect(r.status).toBe(401);
    expect((await status(p)).json.hasSnapshot).toBe(false);
  });
});

describe("catalog mailbox: consume", () => {
  it("hands out the snapshot once, rotates to the next key, and rejects later uploads to the old key", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    const { ciphertext } = await upload(p, key.receiveKeyId, 1);

    const { r, nextKey } = await consume(p, 1);
    expect(r.status).toBe(200);
    expect(r.json.envelope.snapshotId).toBe(1);
    expect(r.json.envelope.receiveKeyId).toBe(key.receiveKeyId);
    expect(r.json.ciphertextBase64Url).toBe(b64url(ciphertext));

    const again = await consume(p, 1);
    expect(again.r.status).toBe(404);

    const after = await status(p);
    expect(after.json).toMatchObject({ hasSnapshot: false, receiveKeyId: nextKey.receiveKeyId });
    expect(after.json.lastUploadAt).toBeGreaterThan(0); // the Sub has published before, even though nothing is waiting

    await rewindUploadClock(p);
    const stale = await upload(p, key.receiveKeyId, 2);
    expect(stale.r.status).toBe(410);
    expect((await upload(p, nextKey.receiveKeyId, 3)).r.status).toBe(200);

    const listed = await env.RELAY_CATALOG_BUCKET.list({ prefix: `mailbox/${p.pairIdHash}/` });
    expect(listed.objects.map((o) => o.key)).toEqual([`mailbox/${p.pairIdHash}/0/3`]);
  });

  it("refuses to consume a snapshot id that isn't the one waiting", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    await upload(p, key.receiveKeyId, 4);
    expect((await consume(p, 3)).r.status).toBe(404);
    expect((await status(p)).json.hasSnapshot).toBe(true);
  });

  it("rejects a consume by the Sub", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    await upload(p, key.receiveKeyId, 1);
    const nextKey = await signedKeyEnvelope(p);
    const r = await signedFetch("/v1/catalog/mailbox/consume", "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0, snapshotId: 1, nextKey }, p.sub.privateKey, p.subId);
    expect(r.status).toBe(401);
  });

  it("gives the Sub a delivery receipt: waiting while unread, consumed after pickup, neither after a key reset", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    await upload(p, key.receiveKeyId, 1);
    expect((await fetchKey(p)).json).toMatchObject({ waitingSnapshotId: 1, lastConsumedSnapshotId: null });

    const { nextKey } = await consume(p, 1);
    expect((await fetchKey(p)).json).toMatchObject({ key: { receiveKeyId: nextKey.receiveKeyId }, waitingSnapshotId: null, lastConsumedSnapshotId: 1 });

    await rewindUploadClock(p);
    await upload(p, nextKey.receiveKeyId, 2);
    await publishKey(p); // Owner lost its key and reset: snapshot 2 is gone and was never consumed
    expect((await fetchKey(p)).json).toMatchObject({ waitingSnapshotId: null, lastConsumedSnapshotId: 1 });
  });

  it("republishing a key discards the snapshot that could only be read with the old one", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    await upload(p, key.receiveKeyId, 1);
    await publishKey(p);
    expect((await status(p)).json.hasSnapshot).toBe(false);
    const listed = await env.RELAY_CATALOG_BUCKET.list({ prefix: `mailbox/${p.pairIdHash}/` });
    expect(listed.objects).toHaveLength(0);
  });

  it("reopens the upload interval when the Owner replaces its key, but not after a normal pickup", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    expect((await upload(p, key.receiveKeyId, 1)).r.status).toBe(200);

    const { envelope: resetKey } = await publishKey(p);
    expect((await upload(p, resetKey.receiveKeyId, 2)).r.status).toBe(200);

    const { nextKey } = await consume(p, 2);
    const tooSoon = await upload(p, nextKey.receiveKeyId, 3);
    expect(tooSoon.r.status).toBe(429);
    expect(tooSoon.r.json.code).toBe("rate_limited");
  });
});
