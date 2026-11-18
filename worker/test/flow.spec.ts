import { SELF } from "cloudflare:test";
import canonicalize from "canonicalize";
import { beforeAll, describe, expect, it } from "vitest";
import {
  acceptInvitation,
  b64url,
  b64urlToBytes,
  BASE,
  createInvitation,
  deviceKeyId,
  genSigningKeyPair,
  hex,
  type Jwk,
  type KeyPair,
  signEnvelope,
  signedFetch,
} from "./helpers";

describe("end-to-end relay flow", () => {
  let inviter: KeyPair;
  let receiver: KeyPair;
  let inviterId: string;
  let receiverId: string;

  beforeAll(async () => {
    inviter = await genSigningKeyPair();
    receiver = await genSigningKeyPair();
    inviterId = await deviceKeyId(inviter.publicKeyJwk);
    receiverId = await deviceKeyId(receiver.publicKeyJwk);
  });

  it("pairs, revokes, re-pairs, and syncs a catalog end to end", async () => {
    // 1. invitation lifecycle
    const invite = await createInvitation(inviter, inviterId);
    expect(invite.status).toBe(200);
    const invitationId = invite.json.invitationId as string;

    const fetchStatus = (await SELF.fetch(BASE + `/v1/invitations/${invitationId}`)).status;
    expect(fetchStatus).toBe(200);

    const accept1 = await acceptInvitation(invitationId, receiver, receiverId);
    expect(accept1.status).toBe(200);

    const accept2 = await acceptInvitation(invitationId, receiver, receiverId);
    expect(accept2.status).not.toBe(200);

    let r = await signedFetch(`/v1/invitations/${invitationId}/consume`, "POST", {}, inviter.privateKey, inviterId);
    expect(r.status).toBe(200);
    expect(r.json.pairEpoch).toBe(0);
    const pairIdHash = r.json.pairIdHash as string;
    expect(r.json.ownerDeviceKeyId).toBe(inviterId);
    expect(r.json.subDeviceKeyId).toBe(receiverId);

    r = await signedFetch(`/v1/invitations/${invitationId}/consume`, "POST", {}, inviter.privateKey, inviterId);
    expect(r.status).not.toBe(200);

    // 2. revocation publish/check, replay rejection
    const revocationUnsigned = {
      type: "revocation",
      schemaVersion: 1,
      pairIdHash,
      pairEpoch: 0,
      sequence: 1,
      reason: "unpair",
      issuedByDeviceKeyId: receiverId,
      createdAt: Math.floor(Date.now() / 1000),
      expiresAt: Math.floor(Date.now() / 1000) + 604800,
    };
    const revocationSig = await signEnvelope(receiver.privateKey, revocationUnsigned);
    r = await signedFetch("/v1/revocations", "POST", { ...revocationUnsigned, signature: revocationSig }, receiver.privateKey, receiverId);
    expect(r.status).toBe(200);

    r = await signedFetch(`/v1/revocations/${pairIdHash}?sinceSequence=0`, "GET", undefined, inviter.privateKey, inviterId);
    expect(r.status).toBe(200);
    expect(r.json.revocations).toHaveLength(1);
    expect(r.json.revocations[0].reason).toBe("unpair");

    r = await signedFetch("/v1/revocations", "POST", { ...revocationUnsigned, signature: revocationSig }, receiver.privateKey, receiverId);
    expect(r.status).not.toBe(200);

    // 3. re-pair (new epoch)
    const invite2 = await createInvitation(inviter, inviterId);
    await acceptInvitation(invite2.json.invitationId, receiver, receiverId);
    const pair2 = (await signedFetch(`/v1/invitations/${invite2.json.invitationId}/consume`, "POST", {}, inviter.privateKey, inviterId)).json;
    expect(pair2.pairEpoch).toBe(1);
    expect(pair2.pairIdHash).toBe(pairIdHash);

    // 4. catalog sync: create -> replace -> fetch -> upload -> consume, with round-trip decryption
    const ownerEphemeral = (await crypto.subtle.generateKey({ name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"])) as CryptoKeyPair;
    const ownerEphemeralJwkRaw = (await crypto.subtle.exportKey("jwk", ownerEphemeral.publicKey)) as JsonWebKey;
    const ownerEphemeralJwk: Jwk = { kty: "EC", crv: "P-256", x: ownerEphemeralJwkRaw.x!, y: ownerEphemeralJwkRaw.y! };

    const replacedRequestId = b64url(crypto.getRandomValues(new Uint8Array(32)));
    const requestId = b64url(crypto.getRandomValues(new Uint8Array(32)));
    const now = Math.floor(Date.now() / 1000);
    const catalogRequestUnsigned = {
      type: "catalog-request",
      schemaVersion: 1,
      pairIdHash,
      pairEpoch: 1,
      requestId: replacedRequestId,
      requesterDeviceKeyId: inviterId,
      ownerEphemeralPublicKey: ownerEphemeralJwk,
      createdAt: now,
      expiresAt: now + 900,
    };
    const catalogReqSig = await signEnvelope(inviter.privateKey, catalogRequestUnsigned);
    r = await signedFetch("/v1/catalog/requests", "POST", { ...catalogRequestUnsigned, signature: catalogReqSig }, inviter.privateKey, inviterId);
    expect(r.status).toBe(200);

    const dupUnsigned = { ...catalogRequestUnsigned, requestId };
    const dupSig = await signEnvelope(inviter.privateKey, dupUnsigned);
    r = await signedFetch("/v1/catalog/requests", "POST", { ...dupUnsigned, signature: dupSig }, inviter.privateKey, inviterId);
    expect(r.status).toBe(200);

    // The Owner's second request replaced the first, which can no longer be answered.
    const replacedResponse = await SELF.fetch(BASE + `/v1/catalog/requests/${replacedRequestId}`);
    expect(((await replacedResponse.json()) as any).status).toBe("expired");
    const fetchResponse = await SELF.fetch(BASE + `/v1/catalog/requests/${requestId}`);
    expect(((await fetchResponse.json()) as any).status).toBe("pending");

    const subEphemeral = (await crypto.subtle.generateKey({ name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"])) as CryptoKeyPair;
    const subEphemeralJwkRaw = (await crypto.subtle.exportKey("jwk", subEphemeral.publicKey)) as JsonWebKey;
    const subEphemeralJwk: Jwk = { kty: "EC", crv: "P-256", x: subEphemeralJwkRaw.x!, y: subEphemeralJwkRaw.y! };

    const ownerRaw = new Uint8Array((await crypto.subtle.exportKey("raw", ownerEphemeral.publicKey)) as ArrayBuffer);
    const subRaw = new Uint8Array((await crypto.subtle.exportKey("raw", subEphemeral.publicKey)) as ArrayBuffer);
    const combined = new Uint8Array(ownerRaw.length + subRaw.length);
    combined.set(ownerRaw, 0);
    combined.set(subRaw, ownerRaw.length);
    const salt = await crypto.subtle.digest("SHA-256", combined);
    const info = new TextEncoder().encode("oathbound-relay-catalog-v1" + pairIdHash + requestId);

    const sharedSecretSub = await crypto.subtle.deriveBits(
      { name: "ECDH", public: ownerEphemeral.publicKey } as unknown as SubtleCryptoDeriveKeyAlgorithm,
      subEphemeral.privateKey,
      256,
    );
    const hkdfKeySub = await crypto.subtle.importKey("raw", sharedSecretSub, "HKDF", false, ["deriveBits"]);
    const aesKeyBitsSub = await crypto.subtle.deriveBits({ name: "HKDF", hash: "SHA-256", salt, info }, hkdfKeySub, 256);
    const aesKeySub = await crypto.subtle.importKey("raw", aesKeyBitsSub, "AES-GCM", false, ["encrypt"]);

    const plaintext = new TextEncoder().encode(JSON.stringify({ commands: ["example"] }));
    const nonceBytes = crypto.getRandomValues(new Uint8Array(12));

    const responseUnsignedForAad = {
      type: "catalog-response",
      schemaVersion: 1,
      pairIdHash,
      pairEpoch: 1,
      requestId,
      snapshotId: 1,
      senderDeviceKeyId: receiverId,
      recipientDeviceKeyId: inviterId,
      createdAt: now,
      expiresAt: now + 900,
      algorithm: "ECDH-P256+HKDF-SHA256+AES-256-GCM",
      ciphertextSizeBytes: 0,
      nonce: b64url(nonceBytes),
      senderEphemeralPublicKey: subEphemeralJwk,
    };
    const aad = new TextEncoder().encode(canonicalize(responseUnsignedForAad)!);
    const ciphertextWithTag = new Uint8Array(
      await crypto.subtle.encrypt({ name: "AES-GCM", iv: nonceBytes, additionalData: aad, tagLength: 128 }, aesKeySub, plaintext),
    );
    const ciphertextDigest = hex(await crypto.subtle.digest("SHA-256", ciphertextWithTag));

    const responseEnvelopeUnsigned = { ...responseUnsignedForAad, ciphertextSizeBytes: ciphertextWithTag.byteLength, ciphertextDigest };
    const responseSig = await signEnvelope(receiver.privateKey, responseEnvelopeUnsigned);
    const responseEnvelope = { ...responseEnvelopeUnsigned, signature: responseSig };

    r = await signedFetch(
      `/v1/catalog/requests/${requestId}/upload`,
      "POST",
      { envelope: responseEnvelope, ciphertextBase64Url: b64url(ciphertextWithTag) },
      receiver.privateKey,
      receiverId,
    );
    expect(r.status).toBe(200);

    r = await signedFetch(`/v1/catalog/requests/${requestId}/consume`, "POST", {}, inviter.privateKey, inviterId);
    expect(r.status).toBe(200);
    const consumed = r.json;

    const sharedSecretOwner = await crypto.subtle.deriveBits(
      { name: "ECDH", public: subEphemeral.publicKey } as unknown as SubtleCryptoDeriveKeyAlgorithm,
      ownerEphemeral.privateKey,
      256,
    );
    const hkdfKeyOwner = await crypto.subtle.importKey("raw", sharedSecretOwner, "HKDF", false, ["deriveBits"]);
    const aesKeyBitsOwner = await crypto.subtle.deriveBits({ name: "HKDF", hash: "SHA-256", salt, info }, hkdfKeyOwner, 256);
    const aesKeyOwner = await crypto.subtle.importKey("raw", aesKeyBitsOwner, "AES-GCM", false, ["decrypt"]);
    const decrypted = await crypto.subtle.decrypt(
      { name: "AES-GCM", iv: nonceBytes, additionalData: aad, tagLength: 128 },
      aesKeyOwner,
      b64urlToBytes(consumed.ciphertextBase64Url),
    );
    expect(new TextDecoder().decode(decrypted)).toBe(new TextDecoder().decode(plaintext));

    r = await signedFetch(`/v1/catalog/requests/${requestId}/consume`, "POST", {}, inviter.privateKey, inviterId);
    expect(r.status).not.toBe(200);
  });
});
