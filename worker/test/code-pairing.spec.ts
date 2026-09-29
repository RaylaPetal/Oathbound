import { env, SELF } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { runScheduledCleanup } from "../src/scheduled";
import {
  b64url,
  BASE,
  createInvitation,
  deviceKeyId,
  genSigningKeyPair,
  hex,
  type KeyPair,
  randomCapabilityId,
  signEnvelope,
  signedFetch,
} from "./helpers";

// collar/pairing: code invitations. The relay only ever handles opaque character blobs here; these tests
// use random bytes for them, since the relay never decrypts anything.
function blob() {
  return { nonce: b64url(crypto.getRandomValues(new Uint8Array(12))), ciphertext: b64url(crypto.getRandomValues(new Uint8Array(48))) };
}

async function createCodeInvitation(inviter: KeyPair, inviterId: string, opts: { lifetime?: number; extra?: object; role?: "owner" | "sub" } = {}) {
  const now = Math.floor(Date.now() / 1000);
  const unsigned = {
    type: "invitation",
    schemaVersion: 1,
    invitationId: randomCapabilityId(),
    inviterDeviceKeyId: inviterId,
    inviterPublicKey: inviter.publicKeyJwk,
    role: opts.role ?? "owner",
    kind: "code",
    encryptedCharacter: blob(),
    createdAt: now,
    expiresAt: now + (opts.lifetime ?? 7 * 86400),
    ...opts.extra,
  };
  const signature = await signEnvelope(inviter.privateKey, unsigned);
  const result = await signedFetch("/v1/invitations", "POST", { ...unsigned, signature }, inviter.privateKey, inviterId);
  return { ...result, invitationId: unsigned.invitationId };
}

async function acceptCode(invitationId: string, accepter: KeyPair, accepterId: string, opts: { withBlob?: boolean; extra?: object } = {}) {
  const now = Math.floor(Date.now() / 1000);
  const unsigned = {
    type: "acceptance",
    schemaVersion: 1,
    invitationId,
    accepterDeviceKeyId: accepterId,
    accepterPublicKey: accepter.publicKeyJwk,
    proofDigest: hex(crypto.getRandomValues(new Uint8Array(16)).buffer),
    role: "sub",
    ...(opts.withBlob === false ? {} : { encryptedCharacter: blob() }),
    createdAt: now,
    expiresAt: now + 7 * 86400,
    ...opts.extra,
  };
  const signature = await signEnvelope(accepter.privateKey, unsigned);
  return signedFetch(`/v1/invitations/${invitationId}/accept`, "POST", { ...unsigned, signature }, accepter.privateKey, accepterId);
}

async function fetchInvite(invitationId: string) {
  const response = await SELF.fetch(BASE + `/v1/invitations/${invitationId}`);
  return { status: response.status, json: (await response.json()) as any };
}

describe("code invitations", () => {
  let owner: KeyPair;
  let sub: KeyPair;
  let ownerId: string;
  let subId: string;

  beforeEach(async () => {
    owner = await genSigningKeyPair();
    sub = await genSigningKeyPair();
    ownerId = await deviceKeyId(owner.publicKeyJwk);
    subId = await deviceKeyId(sub.publicKeyJwk);
  });

  it("pairs asynchronously: create, accept, confirm, and the outcome stays readable after cleanup", async () => {
    const created = await createCodeInvitation(owner, ownerId);
    expect(created.status).toBe(200);

    let fetched = await fetchInvite(created.invitationId);
    expect(fetched.status).toBe(200);
    expect(fetched.json.kind).toBe("code");
    expect(fetched.json.status).toBe("pending");
    expect(fetched.json.encryptedCharacter.ciphertext).toBeTypeOf("string");
    expect(fetched.json.triggerPhrase).toBeUndefined();

    expect((await acceptCode(created.invitationId, sub, subId)).status).toBe(200);
    fetched = await fetchInvite(created.invitationId);
    expect(fetched.json.status).toBe("accepted");
    expect(fetched.json.acceptance.encryptedCharacter.ciphertext).toBeTypeOf("string");

    const consumed = await signedFetch(`/v1/invitations/${created.invitationId}/consume`, "POST", {}, owner.privateKey, ownerId);
    expect(consumed.status).toBe(200);
    expect(consumed.json.ownerDeviceKeyId).toBe(ownerId);
    expect(consumed.json.subDeviceKeyId).toBe(subId);

    // A consumed tell invitation is deleted by cleanup; a code one stays until its own expiry so the
    // accepter's client can still learn the outcome at its next (possibly much later) check.
    await runScheduledCleanup(env);
    fetched = await fetchInvite(created.invitationId);
    expect(fetched.status).toBe(200);
    expect(fetched.json.status).toBe("consumed");

    const pair = await signedFetch(`/v1/pairs/${consumed.json.pairIdHash}?epoch=${consumed.json.pairEpoch}`, "GET", undefined, sub.privateKey, subId);
    expect(pair.status).toBe(200);
    expect(pair.json.revokedAt).toBeNull();
  });

  it("allows a 7-day lifetime but not more", async () => {
    expect((await createCodeInvitation(owner, ownerId, { lifetime: 7 * 86400 })).status).toBe(200);
    expect((await createCodeInvitation(owner, ownerId, { lifetime: 7 * 86400 + 1 })).status).toBe(400);
  });

  it("keeps a tell invitation to its original 15-minute lifetime and shape", async () => {
    const tell = await createInvitation(owner, ownerId);
    expect(tell.status).toBe(200);
    const fetched = await fetchInvite(tell.json.invitationId);
    expect(fetched.json.kind).toBeUndefined();
    expect(fetched.json.encryptedCharacter).toBeUndefined();
  });

  it("rejects a clear trigger phrase or a missing blob on a code invitation, and a blob on a tell acceptance", async () => {
    expect((await createCodeInvitation(owner, ownerId, { extra: { triggerPhrase: "command" } })).status).toBe(400);
    expect((await createCodeInvitation(owner, ownerId, { extra: { encryptedCharacter: undefined } })).status).toBe(400);

    const code = await createCodeInvitation(owner, ownerId);
    expect((await acceptCode(code.invitationId, sub, subId, { withBlob: false })).status).toBe(400);
    expect((await acceptCode(code.invitationId, sub, subId, { extra: { triggerPhrase: "command" } })).status).toBe(400);

    const tell = await createInvitation(owner, ownerId);
    expect((await acceptCode(tell.json.invitationId, sub, subId)).status).toBe(400);
  });

  it("refuses the inviter accepting its own code, and a second acceptance", async () => {
    const code = await createCodeInvitation(owner, ownerId);
    expect((await acceptCode(code.invitationId, owner, ownerId)).status).toBe(400);
    expect((await acceptCode(code.invitationId, sub, subId)).status).toBe(200);
    const intruder = await genSigningKeyPair();
    expect((await acceptCode(code.invitationId, intruder, await deviceKeyId(intruder.publicKeyJwk))).status).not.toBe(200);
  });

  it("lets the inviter cancel an unused code, after which it can't be accepted", async () => {
    const code = await createCodeInvitation(owner, ownerId);
    const cancelled = await signedFetch(`/v1/invitations/${code.invitationId}/cancel`, "POST", {}, owner.privateKey, ownerId);
    expect(cancelled.status).toBe(200);
    expect(cancelled.json.status).toBe("cancelled");
    expect((await fetchInvite(code.invitationId)).json.status).toBe("cancelled");
    expect((await acceptCode(code.invitationId, sub, subId)).status).not.toBe(200);
  });

  it("lets the inviter reject the character that accepted, after which it can't be consumed", async () => {
    const code = await createCodeInvitation(owner, ownerId);
    await acceptCode(code.invitationId, sub, subId);
    const rejected = await signedFetch(`/v1/invitations/${code.invitationId}/cancel`, "POST", {}, owner.privateKey, ownerId);
    expect(rejected.json.status).toBe("rejected");
    expect((await fetchInvite(code.invitationId)).json.status).toBe("rejected");
    const consume = await signedFetch(`/v1/invitations/${code.invitationId}/consume`, "POST", {}, owner.privateKey, ownerId);
    expect(consume.status).not.toBe(200);
  });

  it("only lets the inviter cancel, and only code invitations", async () => {
    const code = await createCodeInvitation(owner, ownerId);
    await acceptCode(code.invitationId, sub, subId);
    expect((await signedFetch(`/v1/invitations/${code.invitationId}/cancel`, "POST", {}, sub.privateKey, subId)).status).not.toBe(200);

    const tell = await createInvitation(owner, ownerId);
    expect((await signedFetch(`/v1/invitations/${tell.json.invitationId}/cancel`, "POST", {}, owner.privateKey, ownerId)).status).not.toBe(200);
  });
});

describe("pair status by epoch (relay-authoritative unpair)", () => {
  it("answers for the exact epoch asked about, including after revocation, and survives cleanup", async () => {
    const a = await genSigningKeyPair();
    const b = await genSigningKeyPair();
    const aId = await deviceKeyId(a.publicKeyJwk);
    const bId = await deviceKeyId(b.publicKeyJwk);

    // a -> b (epoch 0), then the opposite direction b -> a (epoch 1): a mutual pair sharing one hash.
    const first = await createCodeInvitation(a, aId);
    await acceptCode(first.invitationId, b, bId);
    const pair0 = (await signedFetch(`/v1/invitations/${first.invitationId}/consume`, "POST", {}, a.privateKey, aId)).json;
    const second = await createCodeInvitation(b, bId);
    await acceptCode(second.invitationId, a, aId);
    const pair1 = (await signedFetch(`/v1/invitations/${second.invitationId}/consume`, "POST", {}, b.privateKey, bId)).json;
    expect(pair1.pairIdHash).toBe(pair0.pairIdHash);
    expect(pair1.pairEpoch).toBe(1);

    // Revoke epoch 0 only.
    const now = Math.floor(Date.now() / 1000);
    const revocation = {
      type: "revocation",
      schemaVersion: 1,
      pairIdHash: pair0.pairIdHash,
      pairEpoch: 0,
      sequence: 1,
      reason: "unpair",
      issuedByDeviceKeyId: aId,
      createdAt: now,
      expiresAt: now + 604800,
    };
    const signature = await signEnvelope(a.privateKey, revocation);
    expect((await signedFetch("/v1/revocations", "POST", { ...revocation, signature }, a.privateKey, aId)).status).toBe(200);

    await runScheduledCleanup(env);

    const epoch0 = await signedFetch(`/v1/pairs/${pair0.pairIdHash}?epoch=0`, "GET", undefined, b.privateKey, bId);
    expect(epoch0.status).toBe(200);
    expect(epoch0.json.revokedAt).toBeTypeOf("number");
    const epoch1 = await signedFetch(`/v1/pairs/${pair0.pairIdHash}?epoch=1`, "GET", undefined, b.privateKey, bId);
    expect(epoch1.json.revokedAt).toBeNull();

    expect((await signedFetch(`/v1/pairs/${pair0.pairIdHash}?epoch=abc`, "GET", undefined, b.privateKey, bId)).status).toBe(400);
    const outsider = await genSigningKeyPair();
    const outsiderId = await deviceKeyId(outsider.publicKeyJwk);
    await createInvitation(outsider, outsiderId); // makes the outsider's key known to the relay
    expect((await signedFetch(`/v1/pairs/${pair0.pairIdHash}?epoch=0`, "GET", undefined, outsider.privateKey, outsiderId)).status).toBe(401);
  });
});
