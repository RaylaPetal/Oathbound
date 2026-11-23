import { env } from "cloudflare:test";
import { afterEach, describe, expect, it } from "vitest";
import { signedFetch } from "./helpers";
import { consume, freshPair, type Pair, postStatus, publishKey, signedStatus, upload } from "./mailbox-helpers";
import { connect, type TestSocket } from "./socket-helpers";

const open: TestSocket[] = [];
afterEach(() => { for (const s of open.splice(0)) s.close(); });

const now = () => Math.floor(Date.now() / 1000);

const pairStatus = (p: Pair, as: "owner" | "sub") =>
  as === "owner"
    ? signedFetch(`/v1/pairs/${p.pairIdHash}?epoch=0`, "GET", undefined, p.owner.privateKey, p.ownerId)
    : signedFetch(`/v1/pairs/${p.pairIdHash}?epoch=0`, "GET", undefined, p.sub.privateKey, p.subId);

/** A pair whose Owner has picked up catalog snapshot 1, so a receipt for it is allowed. */
async function pickedUp(): Promise<Pair> {
  const p = await freshPair();
  const { envelope: key } = await publishKey(p);
  await upload(p, key.receiveKeyId, 1);
  expect((await consume(p, 1)).r.status).toBe(200);
  return p;
}

describe("mailbox status", () => {
  it("stores a receipt and a sender status and hands them verbatim to the other member", async () => {
    const p = await pickedUp();
    const receipt = await postStatus(p, { sequence: 1, code: "ok" });
    expect(receipt.r.status).toBe(200);
    expect(receipt.r.json).toEqual({ type: "mailbox-status-ack", schemaVersion: 1, stored: true });
    const sender = await postStatus(p, { role: "sender", code: "not_scanned" });
    expect(sender.r.status).toBe(200);

    const mailbox = (await pairStatus(p, "sub")).json.catalogMailbox;
    expect(mailbox.recipientStatus).toEqual(receipt.envelope);
    expect(mailbox.senderStatus).toEqual(sender.envelope);
    expect(mailbox.refreshRequestedAt).toBeNull();
  });

  it("accepts statuses on both rulebook channels from the matching side", async () => {
    const p = await freshPair();
    expect((await postStatus(p, { mailbox: "rulebook", role: "sender", code: "upload_failed" })).r.status).toBe(200);
    expect((await postStatus(p, { mailbox: "report", role: "sender", code: "key_unverified" })).r.status).toBe(200);
    expect((await postStatus(p, { mailbox: "rulebook", role: "recipient", code: "ok" })).r.status).toBe(200);
    const rb = (await pairStatus(p, "owner")).json.rulebookMailbox;
    // A channel without a receive key reports only that it doesn't exist.
    expect(rb.rulebook).toEqual({ exists: false });
  });

  it("rejects a status from the wrong side of the mailbox", async () => {
    const p = await pickedUp();
    expect((await postStatus(p, { role: "sender", as: "owner" })).r.status).toBe(401);
    expect((await postStatus(p, { role: "recipient", as: "sub" })).r.status).toBe(401);
    expect((await postStatus(p, { mailbox: "rulebook", role: "sender", as: "sub" })).r.status).toBe(401);
  });

  it("rejects an envelope naming a device other than the signer", async () => {
    const p = await pickedUp();
    expect((await postStatus(p, { sequence: 1, claimedId: p.subId })).r.status).toBe(401);
  });

  it("rejects a status on a revoked pair", async () => {
    const p = await pickedUp();
    await env.RELAY_DB.prepare(`UPDATE pairs SET revoked_at = ?1 WHERE pair_id_hash = ?2`).bind(now(), p.pairIdHash).run();
    expect((await postStatus(p, { sequence: 1 })).r.status).toBe(401);
  });

  it("rejects an unknown code, a code from the other role's list, and any extra field", async () => {
    const p = await pickedUp();
    expect((await postStatus(p, { code: "exploded" })).r.status).toBe(400);
    expect((await postStatus(p, { code: "not_scanned" })).r.status).toBe(400);
    expect((await postStatus(p, { role: "sender", code: "decrypt_failed" })).r.status).toBe(400);
    expect((await postStatus(p, { extra: { note: "could not read it" } })).r.status).toBe(400);
  });

  it("rejects a bad signature, a receipt for an item not handed out yet, and a time in the future", async () => {
    const p = await pickedUp();
    expect((await postStatus(p, { sequence: 1, tamper: true })).r.status).toBe(401);
    expect((await postStatus(p, { sequence: 2 })).r.status).toBe(400);
    expect((await postStatus(p, { sequence: 1, at: now() + 3600 })).r.status).toBe(400);
  });

  it("keeps the newer status when an older one arrives late", async () => {
    const p = await pickedUp();
    const newer = await postStatus(p, { role: "sender", code: "too_large", at: now() });
    expect(newer.r.status).toBe(200);
    const older = await postStatus(p, { role: "sender", code: "not_scanned", at: now() - 60 });
    expect(older.r.status).toBe(200);
    expect(older.r.json.stored).toBe(false);
    expect((await pairStatus(p, "owner")).json.catalogMailbox.senderStatus).toEqual(newer.envelope);
  });

  it("hints the other member when a status is stored", async () => {
    const p = await pickedUp();
    const owner = await connect({ key: p.owner, id: p.ownerId });
    const sub = await connect({ key: p.sub, id: p.subId });
    open.push(owner, sub);
    expect((await postStatus(p, { sequence: 1 })).r.status).toBe(200);
    expect(await sub.next()).toEqual({ type: "hint", pairIdHash: p.pairIdHash, pairEpoch: 0 });
    expect(await owner.maybeNext(150)).toBeNull();
  });

  it("verifies against the envelope as sent, not a reordered copy", async () => {
    const p = await pickedUp();
    const { envelope, signer, signerId } = await signedStatus(p, { sequence: 1 });
    const reordered = Object.fromEntries(Object.entries(envelope).reverse());
    expect((await signedFetch("/v1/mailbox/status", "POST", reordered, signer.privateKey, signerId)).status).toBe(200);
  });
});
