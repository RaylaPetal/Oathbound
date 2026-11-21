import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { runScheduledCleanup } from "../src/scheduled";
import { b64urlToBytes, signEnvelope, signedFetch } from "./helpers";
import { freshPair, type Pair } from "./mailbox-helpers";
import { fetchChatImage, imageId, uploadChatImage } from "./chat-image-helpers";

const now = () => Math.floor(Date.now() / 1000);

async function revoke(p: Pair) {
  const unsigned = {
    type: "revocation", schemaVersion: 1, pairIdHash: p.pairIdHash, pairEpoch: 0, sequence: 1, reason: "unpair",
    issuedByDeviceKeyId: p.subId, createdAt: now(), expiresAt: now() + 604800,
  };
  const signature = await signEnvelope(p.sub.privateKey, unsigned);
  return signedFetch("/v1/revocations", "POST", { ...unsigned, signature }, p.sub.privateKey, p.subId);
}

async function rowCount(p: Pair): Promise<number> {
  const row = await env.RELAY_DB.prepare(`SELECT COUNT(*) AS n FROM chat_images WHERE pair_id_hash = ?1`).bind(p.pairIdHash).first<{ n: number }>();
  return row?.n ?? 0;
}

describe("chat pictures", () => {
  it("lets either member upload and the other fetch the same bytes", async () => {
    const p = await freshPair();
    const id = imageId();
    const bytes = crypto.getRandomValues(new Uint8Array(4096));
    const up = await uploadChatImage(p, id, bytes, "owner");
    expect(up.status).toBe(200);
    expect((up.json as { expiresAt: number }).expiresAt).toBeGreaterThan(now());

    const got = await fetchChatImage(p, id, "sub");
    expect(got.status).toBe(200);
    const body = got.json as { ciphertextBase64Url: string };
    expect(b64urlToBytes(body.ciphertextBase64Url)).toEqual(bytes);

    const back = imageId();
    expect((await uploadChatImage(p, back, undefined, "sub")).status).toBe(200);
    expect((await fetchChatImage(p, back, "owner")).status).toBe(200);
  });

  it("refuses a device that isn't a member, both ways", async () => {
    const p = await freshPair();
    const other = await freshPair();
    const id = imageId();
    expect((await uploadChatImage(p, id)).status).toBe(200);

    const stranger = { ...p, owner: other.owner, ownerId: other.ownerId };
    expect((await fetchChatImage(stranger, id, "owner")).status).toBe(401);
    expect((await uploadChatImage(stranger, imageId(), undefined, "owner")).status).toBe(401);
  });

  it("refuses an oversized blob, a reused id, and a missing picture", async () => {
    const p = await freshPair();
    expect((await uploadChatImage(p, imageId(), new Uint8Array(524289))).status).toBe(413);

    const id = imageId();
    expect((await uploadChatImage(p, id)).status).toBe(200);
    expect((await uploadChatImage(p, id, undefined, "sub")).status).toBe(400);
    expect((await fetchChatImage(p, imageId())).status).toBe(404);
  });

  it("stops serving a picture after its TTL, and the cron deletes it", async () => {
    const p = await freshPair();
    const id = imageId();
    expect((await uploadChatImage(p, id)).status).toBe(200);
    await env.RELAY_DB.prepare(`UPDATE chat_images SET expires_at = ?1 WHERE pair_id_hash = ?2`).bind(now() - 1, p.pairIdHash).run();
    expect((await fetchChatImage(p, id)).status).toBe(404);

    await runScheduledCleanup(env);
    expect(await rowCount(p)).toBe(0);
    expect(await env.RELAY_CATALOG_BUCKET.get(`chat-images/${p.pairIdHash}/0/${id}`)).toBeNull();
  });

  it("drops a pairing's pictures once it's revoked", async () => {
    const p = await freshPair();
    const id = imageId();
    expect((await uploadChatImage(p, id)).status).toBe(200);
    expect((await revoke(p)).status).toBe(200);
    expect((await fetchChatImage(p, id)).status).toBe(401);

    await runScheduledCleanup(env);
    expect(await rowCount(p)).toBe(0);
  });
});
