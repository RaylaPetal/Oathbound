import { describe, expect, it } from "vitest";
import { b64url, createInvitation, deviceKeyId, genSigningKeyPair, pairDevices, signEnvelope, signedFetch } from "./helpers";

const now = () => Math.floor(Date.now() / 1000);

async function setup() {
  const owner = await genSigningKeyPair();
  const sub = await genSigningKeyPair();
  const ownerId = await deviceKeyId(owner.publicKeyJwk);
  const subId = await deviceKeyId(sub.publicKeyJwk);
  const { pair } = await pairDevices(owner, ownerId, sub, subId);
  return { owner, sub, ownerId, subId, pairIdHash: pair.pairIdHash as string };
}

function record(bytes = 64) {
  return { nonce: b64url(crypto.getRandomValues(new Uint8Array(12))), ciphertext: b64url(crypto.getRandomValues(new Uint8Array(bytes))) };
}

function body(seq: number, rec = record(), pairEpoch = 0) {
  return { type: "pair-characters", schemaVersion: 1, pairEpoch, seq, record: rec };
}

describe("POST /v1/pairs/:pairIdHash/characters", () => {
  it("stores each member's record in its own column and returns only the other member's through both lookups", async () => {
    const { owner, sub, ownerId, subId, pairIdHash } = await setup();

    const before = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(before.json.peerCharacters).toBeNull();

    const subRecord = record();
    const ownerRecord = record();
    const r = await signedFetch(`/v1/pairs/${pairIdHash}/characters`, "POST", body(1, subRecord), sub.privateKey, subId);
    expect(r.status).toBe(200);
    expect(r.json).toEqual({ type: "pair-characters-ack", schemaVersion: 1, stored: true });
    expect((await signedFetch(`/v1/pairs/${pairIdHash}/characters`, "POST", body(1, ownerRecord), owner.privateKey, ownerId)).status).toBe(200);

    const asOwner = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(asOwner.json.peerCharacters).toEqual({ seq: 1, record: subRecord });
    const asSub = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, sub.privateKey, subId);
    expect(asSub.json.peerCharacters).toEqual({ seq: 1, record: ownerRecord });

    const batch = await signedFetch(
      "/v1/pairs/status",
      "POST",
      { type: "pair-status-batch-request", schemaVersion: 1, pairs: [{ pairIdHash, pairEpoch: 0 }] },
      owner.privateKey,
      ownerId,
    );
    expect(batch.json.pairs[0].pair.peerCharacters).toEqual({ seq: 1, record: subRecord });
  });

  it("only replaces a record with a higher seq", async () => {
    const { owner, sub, ownerId, subId, pairIdHash } = await setup();
    const newer = record();
    await signedFetch(`/v1/pairs/${pairIdHash}/characters`, "POST", body(5, newer), sub.privateKey, subId);

    const same = await signedFetch(`/v1/pairs/${pairIdHash}/characters`, "POST", body(5), sub.privateKey, subId);
    expect(same.status).toBe(200);
    expect(same.json.stored).toBe(false);
    const older = await signedFetch(`/v1/pairs/${pairIdHash}/characters`, "POST", body(4), sub.privateKey, subId);
    expect(older.json.stored).toBe(false);

    const pair = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(pair.json.peerCharacters).toEqual({ seq: 5, record: newer });
  });

  it("rejects a stranger and a wrong epoch with the same answer", async () => {
    const { sub, subId, pairIdHash } = await setup();
    const stranger = await genSigningKeyPair();
    const strangerId = await deviceKeyId(stranger.publicKeyJwk);
    await createInvitation(stranger, strangerId);

    const asStranger = await signedFetch(`/v1/pairs/${pairIdHash}/characters`, "POST", body(1), stranger.privateKey, strangerId);
    expect(asStranger.status).toBe(401);
    const wrongEpoch = await signedFetch(`/v1/pairs/${pairIdHash}/characters`, "POST", body(1, record(), 9), sub.privateKey, subId);
    expect(wrongEpoch.status).toBe(401);
    expect(wrongEpoch.json).toEqual(asStranger.json);
  });

  it("rejects a record for a revoked epoch", async () => {
    const { sub, subId, pairIdHash } = await setup();
    const revocation = {
      type: "revocation",
      schemaVersion: 1,
      pairIdHash,
      pairEpoch: 0,
      sequence: 1,
      reason: "unpair",
      issuedByDeviceKeyId: subId,
      createdAt: now(),
      expiresAt: now() + 604800,
    };
    const signature = await signEnvelope(sub.privateKey, revocation);
    expect((await signedFetch("/v1/revocations", "POST", { ...revocation, signature }, sub.privateKey, subId)).status).toBe(200);

    const r = await signedFetch(`/v1/pairs/${pairIdHash}/characters`, "POST", body(1), sub.privateKey, subId);
    expect(r.status).toBe(401);
  });

  it("rejects an oversized or malformed record", async () => {
    const { sub, subId, pairIdHash } = await setup();
    const url = `/v1/pairs/${pairIdHash}/characters`;
    expect((await signedFetch(url, "POST", body(1, record(4096)), sub.privateKey, subId)).status).toBe(200);
    expect((await signedFetch(url, "POST", body(2, record(4097)), sub.privateKey, subId)).status).toBe(400);
    expect((await signedFetch(url, "POST", body(3, { ...record(), extra: "x" } as never), sub.privateKey, subId)).status).toBe(400);
    const missing: Record<string, unknown> = body(4);
    delete missing.seq;
    expect((await signedFetch(url, "POST", missing, sub.privateKey, subId)).status).toBe(400);
  });

  it("limits each device to 30 records an hour", async () => {
    const { sub, subId, pairIdHash } = await setup();
    for (let i = 1; i <= 30; i++) {
      const r = await signedFetch(`/v1/pairs/${pairIdHash}/characters`, "POST", body(i), sub.privateKey, subId);
      expect(r.status).toBe(200);
    }
    const over = await signedFetch(`/v1/pairs/${pairIdHash}/characters`, "POST", body(31), sub.privateKey, subId);
    expect(over.status).toBe(429);
  });
});
