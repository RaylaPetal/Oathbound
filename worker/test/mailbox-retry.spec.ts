import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { b64url } from "./helpers";
import { consume, consumeWith, freshPair, postStatus, publishKey, signedKeyEnvelope, status, upload } from "./mailbox-helpers";
import { connect } from "./socket-helpers";

// Own file: the per-origin limiter is shared by every request in a test file.
describe("catalog mailbox: retry-safe consume", () => {
  const held = async (p: { pairIdHash: string }) =>
    (await env.RELAY_CATALOG_BUCKET.list({ prefix: `mailbox/${p.pairIdHash}/` })).objects.map((o) => o.key);

  it("hands the same snapshot again to a retry carrying the same next key", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    const { ciphertext } = await upload(p, key.receiveKeyId, 1);
    const nextKey = await signedKeyEnvelope(p);
    expect((await consumeWith(p, 1, nextKey)).status).toBe(200);

    const retry = await consumeWith(p, 1, nextKey);
    expect(retry.status).toBe(200);
    expect(retry.json.envelope.snapshotId).toBe(1);
    expect(retry.json.ciphertextBase64Url).toBe(b64url(ciphertext));
    expect((await status(p)).json.receiveKeyId).toBe(nextKey.receiveKeyId);
  });

  it("refuses a retry with a different next key, or for another snapshot", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    await upload(p, key.receiveKeyId, 1);
    const nextKey = await signedKeyEnvelope(p);
    await consumeWith(p, 1, nextKey);

    expect((await consumeWith(p, 1, await signedKeyEnvelope(p))).status).toBe(404);
    expect((await consumeWith(p, 2, nextKey)).status).toBe(404);
  });

  it("releases the held snapshot on the Owner's receipt", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    await upload(p, key.receiveKeyId, 1);
    const nextKey = await signedKeyEnvelope(p);
    await consumeWith(p, 1, nextKey);
    expect(await held(p)).toEqual([`mailbox/${p.pairIdHash}/0/1`]);

    expect((await postStatus(p, { sequence: 1, code: "decrypt_failed" })).r.status).toBe(200);
    expect(await held(p)).toEqual([]);
    expect((await consumeWith(p, 1, nextKey)).status).toBe(404);
  });

  it("releases the held snapshot when the Owner publishes a key outside a pickup", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    await upload(p, key.receiveKeyId, 1);
    const nextKey = await signedKeyEnvelope(p);
    await consumeWith(p, 1, nextKey);

    await publishKey(p);
    expect(await held(p)).toEqual([]);
    expect((await consumeWith(p, 1, nextKey)).status).toBe(404);
  });

  it("hints the Sub on a pickup", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    await upload(p, key.receiveKeyId, 1);
    const sub = await connect({ key: p.sub, id: p.subId });
    try {
      expect((await consume(p, 1)).r.status).toBe(200);
      expect(await sub.next()).toEqual({ type: "hint", pairIdHash: p.pairIdHash, pairEpoch: 0 });
    } finally {
      sub.close();
    }
  });
});
