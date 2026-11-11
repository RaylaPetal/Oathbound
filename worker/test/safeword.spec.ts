import { describe, expect, it } from "vitest";
import { createInvitation, deviceKeyId, genSigningKeyPair, pairDevices, signEnvelope, signedFetch } from "./helpers";

const now = () => Math.floor(Date.now() / 1000);

async function setup() {
  // The inviter invites as Owner, so the receiver is the Sub.
  const owner = await genSigningKeyPair();
  const sub = await genSigningKeyPair();
  const ownerId = await deviceKeyId(owner.publicKeyJwk);
  const subId = await deviceKeyId(sub.publicKeyJwk);
  const { pair } = await pairDevices(owner, ownerId, sub, subId);
  return { owner, sub, ownerId, subId, pairIdHash: pair.pairIdHash as string };
}

function report(at: number, mood: string, pairEpoch = 0) {
  return { type: "safeword", schemaVersion: 1, pairEpoch, at, mood };
}

describe("POST /v1/pairs/:pairIdHash/safeword", () => {
  it("stores the Sub's safeword and returns it to the Owner through the single and batch pair lookups", async () => {
    const { owner, sub, ownerId, subId, pairIdHash } = await setup();

    const before = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(before.json.safewordAt).toBeNull();
    expect(before.json.safewordMood).toBeNull();

    const at = now();
    const r = await signedFetch(`/v1/pairs/${pairIdHash}/safeword`, "POST", report(at, "checkin"), sub.privateKey, subId);
    expect(r.status).toBe(200);
    expect(r.json.type).toBe("safeword-ack");

    const after = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(after.json.safewordAt).toBe(at);
    expect(after.json.safewordMood).toBe("checkin");

    const batch = await signedFetch(
      "/v1/pairs/status",
      "POST",
      { type: "pair-status-batch-request", schemaVersion: 1, pairs: [{ pairIdHash, pairEpoch: 0 }] },
      owner.privateKey,
      ownerId,
    );
    expect(batch.json.pairs[0].pair.safewordAt).toBe(at);
    expect(batch.json.pairs[0].pair.safewordMood).toBe("checkin");
  });

  it("rejects a report from the Owner's device or a stranger with the same answer", async () => {
    const { owner, ownerId, pairIdHash } = await setup();
    const stranger = await genSigningKeyPair();
    const strangerId = await deviceKeyId(stranger.publicKeyJwk);
    await createInvitation(stranger, strangerId);

    const asOwner = await signedFetch(`/v1/pairs/${pairIdHash}/safeword`, "POST", report(now(), "stop"), owner.privateKey, ownerId);
    expect(asOwner.status).toBe(401);
    const asStranger = await signedFetch(`/v1/pairs/${pairIdHash}/safeword`, "POST", report(now(), "stop"), stranger.privateKey, strangerId);
    expect(asStranger.status).toBe(401);
    expect(asStranger.json).toEqual(asOwner.json);

    const pair = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(pair.json.safewordAt).toBeNull();
  });

  it("rejects a report for a revoked epoch", async () => {
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

    const r = await signedFetch(`/v1/pairs/${pairIdHash}/safeword`, "POST", report(now(), "stop"), sub.privateKey, subId);
    expect(r.status).toBe(401);
  });

  it("keeps the newer safeword when an older report arrives late", async () => {
    const { owner, sub, ownerId, subId, pairIdHash } = await setup();
    const at = now();
    await signedFetch(`/v1/pairs/${pairIdHash}/safeword`, "POST", report(at, "checkin"), sub.privateKey, subId);
    const late = await signedFetch(`/v1/pairs/${pairIdHash}/safeword`, "POST", report(at - 600, "stop"), sub.privateKey, subId);
    expect(late.status).toBe(200);

    const pair = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(pair.json.safewordAt).toBe(at);
    expect(pair.json.safewordMood).toBe("checkin");
  });

  it("rejects an unknown mood, a missing time, or a report from the future", async () => {
    const { sub, subId, pairIdHash } = await setup();
    const url = `/v1/pairs/${pairIdHash}/safeword`;
    expect((await signedFetch(url, "POST", report(now(), "help"), sub.privateKey, subId)).status).toBe(400);
    const missing: Record<string, unknown> = report(now(), "stop");
    delete missing.at;
    expect((await signedFetch(url, "POST", missing, sub.privateKey, subId)).status).toBe(400);
    expect((await signedFetch(url, "POST", report(now() + 3600, "stop"), sub.privateKey, subId)).status).toBe(400);
  });

  it("limits each device to 30 reports an hour", async () => {
    const { sub, subId, pairIdHash } = await setup();
    const at = now();
    for (let i = 0; i < 30; i++) {
      const r = await signedFetch(`/v1/pairs/${pairIdHash}/safeword`, "POST", report(at, "stop"), sub.privateKey, subId);
      expect(r.status).toBe(200);
    }
    const over = await signedFetch(`/v1/pairs/${pairIdHash}/safeword`, "POST", report(at, "stop"), sub.privateKey, subId);
    expect(over.status).toBe(429);
  });
});
