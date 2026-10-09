import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { freshPair, publishKey, randomReceiveKeyId, rewindUploadClock, status, upload } from "./mailbox-helpers";

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
