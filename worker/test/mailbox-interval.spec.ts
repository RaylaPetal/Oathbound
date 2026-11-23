import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import {
  consume,
  fetchKey,
  freshPair,
  publishKey,
  publishRefreshKey,
  randomReceiveKeyId,
  rewindUploadClock,
  signedKeyEnvelope,
  status,
  upload,
} from "./mailbox-helpers";

// Own file: the per-origin limiter is shared by every request in a test file.
describe("catalog mailbox: upload interval claim", () => {
  it("doesn't start the upload interval for an upload it rejects", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    expect((await upload(p, randomReceiveKeyId(), 1)).r.status).toBe(410);
    expect((await upload(p, key.receiveKeyId, 2)).r.status).toBe(200);

    await rewindUploadClock(p);
    expect((await upload(p, key.receiveKeyId, 2)).r.status).toBe(400);
    expect((await upload(p, key.receiveKeyId, 3)).r.status).toBe(200);
    expect((await status(p)).json.snapshotId).toBe(3);
  });

  it("accepts exactly one of two racing uploads and keeps only its ciphertext", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    const results = await Promise.all([upload(p, key.receiveKeyId, 1), upload(p, key.receiveKeyId, 2)]);
    const statuses = results.map(({ r }) => r.status).sort();
    expect(statuses).toEqual([200, 429]);

    const winner = results.find(({ r }) => r.status === 200)!.r.json.snapshotId;
    const listed = await env.RELAY_CATALOG_BUCKET.list({ prefix: `mailbox/${p.pairIdHash}/` });
    expect(listed.objects.map((o) => o.key)).toEqual([`mailbox/${p.pairIdHash}/0/${winner}`]);
  });
});

describe("catalog mailbox: Get latest", () => {
  it("reopens the upload interval and clears the pickup record, so an older Sub's delivery check republishes", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    expect((await upload(p, key.receiveKeyId, 1)).r.status).toBe(200);
    const { nextKey } = await consume(p, 1);
    // 0.3.2.2's check: delivered when the last published snapshot is waiting or was consumed.
    expect((await fetchKey(p)).json).toMatchObject({ waitingSnapshotId: null, lastConsumedSnapshotId: 1 });
    expect((await upload(p, nextKey.receiveKeyId, 2)).r.status).toBe(429);

    const refreshKey = await signedKeyEnvelope(p);
    const refreshed = await publishRefreshKey(p, refreshKey);
    expect(refreshed.status).toBe(200);
    const after = await fetchKey(p);
    expect(after.json).toMatchObject({ key: { receiveKeyId: refreshKey.receiveKeyId }, waitingSnapshotId: null, lastConsumedSnapshotId: null });
    expect((await upload(p, refreshKey.receiveKeyId, 2)).r.status).toBe(200);
  });

  it("refuses a second refresh within a minute with a retry-after, but still allows a plain key publish", async () => {
    const p = await freshPair();
    await publishKey(p);
    expect((await publishRefreshKey(p, await signedKeyEnvelope(p))).status).toBe(200);
    const second = await publishRefreshKey(p, await signedKeyEnvelope(p));
    expect(second.status).toBe(429);
    expect(second.json.code).toBe("rate_limited");
    expect(second.json.retryAfterSeconds).toBeGreaterThan(0);
    expect(second.json.retryAfterSeconds).toBeLessThanOrEqual(60);
    expect((await publishKey(p)).r.status).toBe(200);

    await env.RELAY_DB.prepare(`UPDATE catalog_mailboxes SET refresh_requested_at = refresh_requested_at - 61 WHERE pair_id_hash = ?1`).bind(p.pairIdHash).run();
    expect((await publishRefreshKey(p, await signedKeyEnvelope(p))).status).toBe(200);
  });
});
