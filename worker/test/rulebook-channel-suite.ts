import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { b64url, signedFetch } from "./helpers";
import { freshPair } from "./mailbox-helpers";
import {
  type Channel,
  consumeItem,
  fetchRulebookKey,
  publishRulebookKey,
  recipientOf,
  rewindRulebookUploadClock,
  senderOf,
  signedRulebookKey,
  uploadItem,
} from "./rulebook-helpers";

/** Split across one spec file per channel: the test pool's origin rate limiter is shared within a file. */
export function defineChannelSuite(channel: Channel): void {
  describe(`rulebook mailbox, channel ${channel}`, () => {
    it("lets the recipient publish a key and the sender fetch the same recipient-signed envelope", async () => {
      const p = await freshPair();
      const { r, envelope } = await publishRulebookKey(p, channel);
      expect(r.status).toBe(200);
      const fetched = await fetchRulebookKey(p, channel);
      expect(fetched.status).toBe(200);
      expect(fetched.json).toEqual({ key: envelope, waitingSequence: null, lastConsumedSequence: null, lastSequence: 0 });
    });

    it("rejects a key published from the sender side, or signed by anyone but the recipient", async () => {
      const p = await freshPair();
      expect((await publishRulebookKey(p, channel, { as: senderOf(p, channel) })).r.status).toBe(401);
      const forged = await signedRulebookKey(p, channel, { signer: senderOf(p, channel).keys });
      expect((await publishRulebookKey(p, channel, { key: forged })).r.status).toBe(401);
    });

    it("rejects a key whose channel doesn't match the request", async () => {
      const p = await freshPair();
      const other: Channel = channel === "rulebook" ? "report" : "rulebook";
      const key = await signedRulebookKey(p, channel);
      const caller = recipientOf(p, channel);
      const r = await signedFetch("/v1/rulebook/key", "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0, channel: other, key }, caller.keys.privateKey, caller.id);
      expect(r.status).not.toBe(200);
    });

    it("rejects the recipient fetching the key through the sender route", async () => {
      const p = await freshPair();
      await publishRulebookKey(p, channel);
      expect((await fetchRulebookKey(p, channel, recipientOf(p, channel))).status).toBe(401);
    });

    it("stores an item, hands it to the recipient once, and rotates the key", async () => {
      const p = await freshPair();
      const { envelope: key } = await publishRulebookKey(p, channel);
      const { r, ciphertext } = await uploadItem(p, channel, key.receiveKeyId, 1);
      expect(r.status).toBe(200);

      const { r: consumed, nextKey } = await consumeItem(p, channel, 1);
      expect(consumed.status).toBe(200);
      expect(consumed.json.ciphertextBase64Url).toBe(b64url(ciphertext));
      expect(consumed.json.envelope.sequence).toBe(1);

      expect((await consumeItem(p, channel, 1)).r.status).toBe(404);
      const fetched = await fetchRulebookKey(p, channel);
      expect(fetched.json).toMatchObject({ key: nextKey, waitingSequence: null, lastConsumedSequence: 1, lastSequence: 1 });
    });

    it("rejects an upload from the recipient side", async () => {
      const p = await freshPair();
      const { envelope: key } = await publishRulebookKey(p, channel);
      expect((await uploadItem(p, channel, key.receiveKeyId, 1, { as: recipientOf(p, channel) })).r.status).toBe(401);
    });

    it("rejects an upload to a stale key as expired", async () => {
      const p = await freshPair();
      const { envelope: oldKey } = await publishRulebookKey(p, channel);
      await publishRulebookKey(p, channel);
      expect((await uploadItem(p, channel, oldKey.receiveKeyId, 1)).r.status).toBe(410);
    });

    it("rejects an upload before the recipient ever published a key", async () => {
      const p = await freshPair();
      expect((await uploadItem(p, channel, "rbKeyIDrbKeyIDrbKeyIDr", 1)).r.status).toBe(404);
    });

    it("rejects a sequence that isn't greater than the last accepted one, even after a key replacement", async () => {
      const p = await freshPair();
      const { envelope: key } = await publishRulebookKey(p, channel);
      expect((await uploadItem(p, channel, key.receiveKeyId, 5)).r.status).toBe(200);
      await rewindRulebookUploadClock(p, channel);
      expect((await uploadItem(p, channel, key.receiveKeyId, 5)).r.status).toBe(400);

      const { envelope: newKey } = await publishRulebookKey(p, channel);
      expect((await uploadItem(p, channel, newKey.receiveKeyId, 4)).r.status).toBe(400);
      expect((await uploadItem(p, channel, newKey.receiveKeyId, 6)).r.status).toBe(200);
    });

    it("enforces the per-channel upload interval", async () => {
      const p = await freshPair();
      const { envelope: key } = await publishRulebookKey(p, channel);
      expect((await uploadItem(p, channel, key.receiveKeyId, 1)).r.status).toBe(200);
      const second = await uploadItem(p, channel, key.receiveKeyId, 2);
      expect(second.r.status).toBe(429);
      await rewindRulebookUploadClock(p, channel);
      expect((await uploadItem(p, channel, key.receiveKeyId, 2)).r.status).toBe(200);
    });

    it("rejects a ciphertext over the channel's size limit", async () => {
      const p = await freshPair();
      const { envelope: key } = await publishRulebookKey(p, channel);
      const limit = channel === "rulebook" ? 65536 : 32768;
      expect((await uploadItem(p, channel, key.receiveKeyId, 1, { sizeBytes: limit + 1 })).r.status).toBe(413);
    });

    it("rejects an item that would outlive the retention window", async () => {
      const p = await freshPair();
      const { envelope: key } = await publishRulebookKey(p, channel);
      expect((await uploadItem(p, channel, key.receiveKeyId, 1, { expiresIn: 1209600 + 60 })).r.status).toBe(400);
    });

    it("rejects every operation on a revoked pair", async () => {
      const p = await freshPair();
      const { envelope: key } = await publishRulebookKey(p, channel);
      await uploadItem(p, channel, key.receiveKeyId, 1);
      await env.RELAY_DB.prepare(`UPDATE pairs SET revoked_at = ?1 WHERE pair_id_hash = ?2`).bind(Math.floor(Date.now() / 1000), p.pairIdHash).run();

      expect((await publishRulebookKey(p, channel)).r.status).toBe(401);
      expect((await fetchRulebookKey(p, channel)).status).toBe(401);
      await rewindRulebookUploadClock(p, channel);
      expect((await uploadItem(p, channel, key.receiveKeyId, 2)).r.status).toBe(401);
      expect((await consumeItem(p, channel, 1)).r.status).toBe(401);
    });
  });
}
