import { env } from "cloudflare:test";
import { b64url, deviceKeyId, genSigningKeyPair, hex, type Jwk, type KeyPair, pairDevices, signEnvelope, signedFetch } from "./helpers";

export interface Pair {
  owner: KeyPair;
  ownerId: string;
  sub: KeyPair;
  subId: string;
  pairIdHash: string;
}

/** A fresh Owner/Sub pair per test so the per-pair upload interval and snapshot sequence never leak between tests. */
export async function freshPair(): Promise<Pair> {
  const owner = await genSigningKeyPair();
  const sub = await genSigningKeyPair();
  const ownerId = await deviceKeyId(owner.publicKeyJwk);
  const subId = await deviceKeyId(sub.publicKeyJwk);
  const { pair } = await pairDevices(owner, ownerId, sub, subId, "owner");
  return { owner, ownerId, sub, subId, pairIdHash: pair.pairIdHash };
}

export async function ecdhPublicJwk(): Promise<Jwk> {
  const kp = (await crypto.subtle.generateKey({ name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"])) as CryptoKeyPair;
  const jwk = (await crypto.subtle.exportKey("jwk", kp.publicKey)) as JsonWebKey;
  return { kty: "EC", crv: "P-256", x: jwk.x!, y: jwk.y! };
}

export function randomReceiveKeyId(): string {
  return b64url(crypto.getRandomValues(new Uint8Array(16)));
}

export async function signedKeyEnvelope(p: Pair, receiveKeyId = randomReceiveKeyId(), signer: KeyPair = p.owner) {
  const unsigned = {
    type: "catalog-mailbox-key",
    schemaVersion: 1,
    pairIdHash: p.pairIdHash,
    pairEpoch: 0,
    receiveKeyId,
    ownerDeviceKeyId: p.ownerId,
    receivePublicKey: await ecdhPublicJwk(),
    createdAt: Math.floor(Date.now() / 1000),
  };
  return { ...unsigned, signature: await signEnvelope(signer.privateKey, unsigned) };
}

export async function publishKey(p: Pair, key?: Awaited<ReturnType<typeof signedKeyEnvelope>>) {
  const envelope = key ?? (await signedKeyEnvelope(p));
  const r = await signedFetch("/v1/catalog/mailbox/key", "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0, key: envelope }, p.owner.privateKey, p.ownerId);
  return { r, envelope };
}

export async function upload(p: Pair, receiveKeyId: string, snapshotId: number, opts: { as?: KeyPair; asId?: string } = {}) {
  const ciphertext = crypto.getRandomValues(new Uint8Array(64));
  const now = Math.floor(Date.now() / 1000);
  const unsigned = {
    type: "catalog-push",
    schemaVersion: 1,
    pairIdHash: p.pairIdHash,
    pairEpoch: 0,
    receiveKeyId,
    snapshotId,
    senderDeviceKeyId: opts.asId ?? p.subId,
    recipientDeviceKeyId: p.ownerId,
    createdAt: now,
    expiresAt: now + 3600,
    algorithm: "ECDH-P256+HKDF-SHA256+AES-256-GCM",
    ciphertextDigest: hex(await crypto.subtle.digest("SHA-256", ciphertext)),
    ciphertextSizeBytes: ciphertext.byteLength,
    nonce: b64url(crypto.getRandomValues(new Uint8Array(12))),
    senderEphemeralPublicKey: await ecdhPublicJwk(),
  };
  const signer = opts.as ?? p.sub;
  const envelope = { ...unsigned, signature: await signEnvelope(signer.privateKey, unsigned) };
  const r = await signedFetch("/v1/catalog/mailbox/upload", "POST", { envelope, ciphertextBase64Url: b64url(ciphertext) }, signer.privateKey, opts.asId ?? p.subId);
  return { r, ciphertext };
}

export const fetchKey = (p: Pair) => signedFetch("/v1/catalog/mailbox/key/fetch", "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0 }, p.sub.privateKey, p.subId);

export const status = (p: Pair) => signedFetch("/v1/catalog/mailbox/status", "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0 }, p.owner.privateKey, p.ownerId);

export async function consume(p: Pair, snapshotId: number) {
  const nextKey = await signedKeyEnvelope(p);
  const r = await signedFetch("/v1/catalog/mailbox/consume", "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0, snapshotId, nextKey }, p.owner.privateKey, p.ownerId);
  return { r, nextKey };
}

/** Pretends the last upload happened long enough ago that the per-pair minimum interval has passed. */
export async function rewindUploadClock(p: Pair) {
  await env.RELAY_DB.prepare(`UPDATE catalog_mailboxes SET last_upload_at = last_upload_at - 3600 WHERE pair_id_hash = ?1`).bind(p.pairIdHash).run();
}
