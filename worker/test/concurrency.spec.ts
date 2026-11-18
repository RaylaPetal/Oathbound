import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { acceptInvitation, b64url, createInvitation, deviceKeyId, genSigningKeyPair, signEnvelope, signedFetch } from "./helpers";

describe("concurrency guarantees", () => {
  it("concurrent Accept attempts from two different receivers produce exactly one successful pairing", async () => {
    const inviter = await genSigningKeyPair();
    const inviterId = await deviceKeyId(inviter.publicKeyJwk);
    const invite = await createInvitation(inviter, inviterId);
    const invitationId = invite.json.invitationId as string;

    const receiverA = await genSigningKeyPair();
    const receiverB = await genSigningKeyPair();
    const receiverAId = await deviceKeyId(receiverA.publicKeyJwk);
    const receiverBId = await deviceKeyId(receiverB.publicKeyJwk);

    const [resultA, resultB] = await Promise.all([
      acceptInvitation(invitationId, receiverA, receiverAId),
      acceptInvitation(invitationId, receiverB, receiverBId),
    ]);

    const successes = [resultA, resultB].filter((r) => r.status === 200);
    expect(successes).toHaveLength(1);
  });

  it("concurrent catalog-request creation for the same pair leaves exactly one answerable request", async () => {
    const inviter = await genSigningKeyPair();
    const receiver = await genSigningKeyPair();
    const inviterId = await deviceKeyId(inviter.publicKeyJwk);
    const receiverId = await deviceKeyId(receiver.publicKeyJwk);

    const invite = await createInvitation(inviter, inviterId);
    const invitationId = invite.json.invitationId as string;
    await acceptInvitation(invitationId, receiver, receiverId);
    const pair = (await signedFetch(`/v1/invitations/${invitationId}/consume`, "POST", {}, inviter.privateKey, inviterId)).json;
    const pairIdHash = pair.pairIdHash as string;

    async function buildRequest() {
      const ownerEphemeral = (await crypto.subtle.generateKey({ name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"])) as CryptoKeyPair;
      const jwkRaw = (await crypto.subtle.exportKey("jwk", ownerEphemeral.publicKey)) as JsonWebKey;
      const now = Math.floor(Date.now() / 1000);
      const unsigned = {
        type: "catalog-request",
        schemaVersion: 1,
        pairIdHash,
        pairEpoch: 0,
        requestId: b64url(crypto.getRandomValues(new Uint8Array(32))),
        requesterDeviceKeyId: inviterId,
        ownerEphemeralPublicKey: { kty: "EC", crv: "P-256", x: jwkRaw.x!, y: jwkRaw.y! },
        createdAt: now,
        expiresAt: now + 900,
      };
      const signature = await signEnvelope(inviter.privateKey, unsigned);
      return signedFetch("/v1/catalog/requests", "POST", { ...unsigned, signature }, inviter.privateKey, inviterId);
    }

    const results = await Promise.all([buildRequest(), buildRequest(), buildRequest()]);
    expect(results.some((r) => r.status === 200)).toBe(true);
    const pending = await env.RELAY_DB.prepare(`SELECT COUNT(*) AS n FROM catalog_requests WHERE pair_id_hash = ?1 AND pair_epoch = 0 AND status = 'pending'`)
      .bind(pairIdHash)
      .first<{ n: number }>();
    expect(pending?.n).toBe(1);
  });
});
