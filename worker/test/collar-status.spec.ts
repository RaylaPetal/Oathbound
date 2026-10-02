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

function report(state: string, stateAt: number, pairEpoch = 0) {
  return { type: "collar-status", schemaVersion: 1, pairEpoch, state, stateAt };
}

describe("POST /v1/pairs/:pairIdHash/collar-status", () => {
  it("stores the Sub's report and returns it to the Owner through the pair lookup", async () => {
    const { owner, sub, ownerId, subId, pairIdHash } = await setup();

    const before = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(before.json.collarState).toBeNull();
    expect(before.json.collarCheckinAt).toBeNull();

    const at = now();
    const r = await signedFetch(`/v1/pairs/${pairIdHash}/collar-status`, "POST", report("broken", at), sub.privateKey, subId);
    expect(r.status).toBe(200);

    const after = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(after.json.collarState).toBe("broken");
    expect(after.json.collarStateAt).toBe(at);
    expect(after.json.collarCheckinAt).toBeGreaterThanOrEqual(at);
  });

  it("rejects a report from the Owner's device or a stranger", async () => {
    const { owner, ownerId, pairIdHash } = await setup();
    const stranger = await genSigningKeyPair();
    const strangerId = await deviceKeyId(stranger.publicKeyJwk);
    await createInvitation(stranger, strangerId);

    const asOwner = await signedFetch(`/v1/pairs/${pairIdHash}/collar-status`, "POST", report("broken", now()), owner.privateKey, ownerId);
    expect(asOwner.status).not.toBe(200);
    const asStranger = await signedFetch(`/v1/pairs/${pairIdHash}/collar-status`, "POST", report("broken", now()), stranger.privateKey, strangerId);
    expect(asStranger.status).not.toBe(200);

    const pair = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(pair.json.collarState).toBeNull();
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

    const r = await signedFetch(`/v1/pairs/${pairIdHash}/collar-status`, "POST", report("locked", now()), sub.privateKey, subId);
    expect(r.status).not.toBe(200);
  });

  it("keeps the newer state when an older report arrives late, but still counts it as a check-in", async () => {
    const { owner, sub, ownerId, subId, pairIdHash } = await setup();
    const at = now();
    await signedFetch(`/v1/pairs/${pairIdHash}/collar-status`, "POST", report("locked", at), sub.privateKey, subId);
    const late = await signedFetch(`/v1/pairs/${pairIdHash}/collar-status`, "POST", report("broken", at - 600), sub.privateKey, subId);
    expect(late.status).toBe(200);

    const pair = await signedFetch(`/v1/pairs/${pairIdHash}?epoch=0`, "GET", undefined, owner.privateKey, ownerId);
    expect(pair.json.collarState).toBe("locked");
    expect(pair.json.collarStateAt).toBe(at);
    expect(pair.json.collarCheckinAt).not.toBeNull();
  });

  it("rejects an unknown state or a report from the future", async () => {
    const { sub, subId, pairIdHash } = await setup();
    const unknown = await signedFetch(`/v1/pairs/${pairIdHash}/collar-status`, "POST", report("off", now()), sub.privateKey, subId);
    expect(unknown.status).toBe(400);
    const future = await signedFetch(`/v1/pairs/${pairIdHash}/collar-status`, "POST", report("locked", now() + 3600), sub.privateKey, subId);
    expect(future.status).toBe(400);
  });
});
