import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { deviceKeyId, genSigningKeyPair, signedFetch } from "./helpers";
import { consume, freshPair, type Pair, publishKey, upload } from "./mailbox-helpers";

describe("catalog mailbox: state on the pair status response", () => {
  const pairStatus = (p: Pair, as: "owner" | "sub" = "owner") =>
    as === "owner"
      ? signedFetch(`/v1/pairs/${p.pairIdHash}?epoch=0`, "GET", undefined, p.owner.privateKey, p.ownerId)
      : signedFetch(`/v1/pairs/${p.pairIdHash}?epoch=0`, "GET", undefined, p.sub.privateKey, p.subId);

  it("reports no mailbox before the Owner ever published a key", async () => {
    const p = await freshPair();
    expect((await pairStatus(p)).json.catalogMailbox).toEqual({ exists: false });
  });

  it("tracks key, waiting, consumed and last upload for both members", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    expect((await pairStatus(p)).json.catalogMailbox).toEqual({
      exists: true, receiveKeyId: key.receiveKeyId, waitingSnapshotId: null, lastConsumedSnapshotId: null, lastUploadAt: null,
    });

    await upload(p, key.receiveKeyId, 1);
    const waiting = (await pairStatus(p, "sub")).json.catalogMailbox;
    expect(waiting).toMatchObject({ exists: true, receiveKeyId: key.receiveKeyId, waitingSnapshotId: 1, lastConsumedSnapshotId: null });
    expect(waiting.lastUploadAt).toBeGreaterThan(0);

    const { nextKey } = await consume(p, 1);
    expect((await pairStatus(p)).json.catalogMailbox).toMatchObject({ receiveKeyId: nextKey.receiveKeyId, waitingSnapshotId: null, lastConsumedSnapshotId: 1 });
  });

  it("stops reporting a snapshot as waiting once it expired", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishKey(p);
    await upload(p, key.receiveKeyId, 1);
    await env.RELAY_DB.prepare(`UPDATE catalog_mailboxes SET snapshot_expires_at = 1 WHERE pair_id_hash = ?1`).bind(p.pairIdHash).run();
    expect((await pairStatus(p)).json.catalogMailbox).toMatchObject({ waitingSnapshotId: null });
  });

  it("reveals nothing to a device outside the pair", async () => {
    const p = await freshPair();
    await publishKey(p);
    const outsider = await genSigningKeyPair();
    const outsiderId = await deviceKeyId(outsider.publicKeyJwk);
    const r = await signedFetch(`/v1/pairs/${p.pairIdHash}?epoch=0`, "GET", undefined, outsider.privateKey, outsiderId);
    expect(r.status).toBe(401);
    expect(r.json.catalogMailbox).toBeUndefined();
  });
});
