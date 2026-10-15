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

function report(state: string, stateAt: number, endsAt: number | null, pairEpoch = 0) {
  return { type: "cage-status", schemaVersion: 1, pairEpoch, state, stateAt, endsAt };
}

describe("POST /v1/pairs/:pairIdHash/cage-status", () => {
  it("stores the Sub's report and returns it to the Owner through the single and batch pair lookups", async () => {
    const { owner, sub, ownerId, subId, pairIdHash } = await setup();

    const before = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(before.json.cageState).toBeNull();
    expect(before.json.cageEndsAt).toBeNull();

    const at = now();
    const r = await signedFetch(`/v1/pairs/${pairIdHash}/cage-status`, "POST", report("caged", at, at + 3600), sub.privateKey, subId);
    expect(r.status).toBe(200);

    const after = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(after.json.cageState).toBe("caged");
    expect(after.json.cageStateAt).toBe(at);
    expect(after.json.cageEndsAt).toBe(at + 3600);

    const batch = await signedFetch(
      "/v1/pairs/status",
      "POST",
      { type: "pair-status-batch-request", schemaVersion: 1, pairs: [{ pairIdHash, pairEpoch: 0 }] },
      owner.privateKey,
      ownerId,
    );
    expect(batch.json.pairs[0].pair.cageState).toBe("caged");
    expect(batch.json.pairs[0].pair.cageEndsAt).toBe(at + 3600);
  });

  it("accepts a cage locked until opened with no end time", async () => {
    const { owner, sub, ownerId, subId, pairIdHash } = await setup();
    const r = await signedFetch(`/v1/pairs/${pairIdHash}/cage-status`, "POST", report("caged", now(), null), sub.privateKey, subId);
    expect(r.status).toBe(200);
    const pair = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(pair.json.cageState).toBe("caged");
    expect(pair.json.cageEndsAt).toBeNull();
  });

  it("rejects a report from the Owner's device or a stranger", async () => {
    const { owner, ownerId, pairIdHash } = await setup();
    const stranger = await genSigningKeyPair();
    const strangerId = await deviceKeyId(stranger.publicKeyJwk);
    await createInvitation(stranger, strangerId);

    const asOwner = await signedFetch(`/v1/pairs/${pairIdHash}/cage-status`, "POST", report("caged", now(), null), owner.privateKey, ownerId);
    expect(asOwner.status).toBe(401);
    const asStranger = await signedFetch(`/v1/pairs/${pairIdHash}/cage-status`, "POST", report("caged", now(), null), stranger.privateKey, strangerId);
    expect(asStranger.status).toBe(401);

    const pair = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(pair.json.cageState).toBeNull();
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

    const r = await signedFetch(`/v1/pairs/${pairIdHash}/cage-status`, "POST", report("armed", now(), now() + 3600), sub.privateKey, subId);
    expect(r.status).toBe(401);
  });

  it("keeps the newer state when an older report arrives late", async () => {
    const { owner, sub, ownerId, subId, pairIdHash } = await setup();
    const at = now();
    await signedFetch(`/v1/pairs/${pairIdHash}/cage-status`, "POST", report("caged", at, at + 600), sub.privateKey, subId);
    const late = await signedFetch(`/v1/pairs/${pairIdHash}/cage-status`, "POST", report("armed", at - 600, at + 86400), sub.privateKey, subId);
    expect(late.status).toBe(200);

    const pair = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(pair.json.cageState).toBe("caged");
    expect(pair.json.cageStateAt).toBe(at);
    expect(pair.json.cageEndsAt).toBe(at + 600);
  });

  it("rejects an unknown state, a missing or misplaced end time, or a report from the future", async () => {
    const { sub, subId, pairIdHash } = await setup();
    const url = `/v1/pairs/${pairIdHash}/cage-status`;
    expect((await signedFetch(url, "POST", report("open", now(), null), sub.privateKey, subId)).status).toBe(400);
    const missing: Record<string, unknown> = report("caged", now(), null);
    delete missing.endsAt;
    expect((await signedFetch(url, "POST", missing, sub.privateKey, subId)).status).toBe(400);
    expect((await signedFetch(url, "POST", report("none", now(), now() + 60), sub.privateKey, subId)).status).toBe(400);
    expect((await signedFetch(url, "POST", report("caged", now() + 3600, null), sub.privateKey, subId)).status).toBe(400);
  });

  it("limits each device to 30 reports an hour", async () => {
    const { sub, subId, pairIdHash } = await setup();
    const at = now();
    for (let i = 0; i < 30; i++) {
      const r = await signedFetch(`/v1/pairs/${pairIdHash}/cage-status`, "POST", report("armed", at, at + 3600), sub.privateKey, subId);
      expect(r.status).toBe(200);
    }
    const over = await signedFetch(`/v1/pairs/${pairIdHash}/cage-status`, "POST", report("armed", at, at + 3600), sub.privateKey, subId);
    expect(over.status).toBe(429);
  });
});
