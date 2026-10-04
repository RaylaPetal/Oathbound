import { env } from "cloudflare:test";
import { b64url, hex, type KeyPair, signEnvelope, signedFetch } from "./helpers";
import { ecdhPublicJwk, type Pair, randomReceiveKeyId } from "./mailbox-helpers";

export type Channel = "rulebook" | "report";

/** The Sub receives on channel rulebook, the Owner on channel report. */
export function recipientOf(p: Pair, channel: Channel): { keys: KeyPair; id: string } {
  return channel === "rulebook" ? { keys: p.sub, id: p.subId } : { keys: p.owner, id: p.ownerId };
}

export function senderOf(p: Pair, channel: Channel): { keys: KeyPair; id: string } {
  return channel === "rulebook" ? { keys: p.owner, id: p.ownerId } : { keys: p.sub, id: p.subId };
}

export async function signedRulebookKey(p: Pair, channel: Channel, opts: { receiveKeyId?: string; signer?: KeyPair } = {}) {
  const recipient = recipientOf(p, channel);
  const unsigned = {
    type: "rulebook-key",
    schemaVersion: 1,
    channel,
    pairIdHash: p.pairIdHash,
    pairEpoch: 0,
    receiveKeyId: opts.receiveKeyId ?? randomReceiveKeyId(),
    recipientDeviceKeyId: recipient.id,
    receivePublicKey: await ecdhPublicJwk(),
    createdAt: Math.floor(Date.now() / 1000),
  };
  return { ...unsigned, signature: await signEnvelope((opts.signer ?? recipient.keys).privateKey, unsigned) };
}

export async function publishRulebookKey(p: Pair, channel: Channel, opts: { key?: Awaited<ReturnType<typeof signedRulebookKey>>; as?: { keys: KeyPair; id: string } } = {}) {
  const envelope = opts.key ?? (await signedRulebookKey(p, channel));
  const caller = opts.as ?? recipientOf(p, channel);
  const r = await signedFetch("/v1/rulebook/key", "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0, channel, key: envelope }, caller.keys.privateKey, caller.id);
  return { r, envelope };
}

export function fetchRulebookKey(p: Pair, channel: Channel, as = senderOf(p, channel)) {
  return signedFetch("/v1/rulebook/key/fetch", "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0, channel }, as.keys.privateKey, as.id);
}

export async function uploadItem(
  p: Pair,
  channel: Channel,
  receiveKeyId: string,
  sequence: number,
  opts: { as?: { keys: KeyPair; id: string }; sizeBytes?: number; expiresIn?: number } = {},
) {
  const sender = opts.as ?? senderOf(p, channel);
  const ciphertext = new Uint8Array(opts.sizeBytes ?? 64);
  crypto.getRandomValues(ciphertext.subarray(0, Math.min(ciphertext.byteLength, 65536)));
  const now = Math.floor(Date.now() / 1000);
  const unsigned = {
    type: channel === "rulebook" ? "rulebook-push" : "rulebook-report",
    schemaVersion: 1,
    pairIdHash: p.pairIdHash,
    pairEpoch: 0,
    receiveKeyId,
    sequence,
    senderDeviceKeyId: sender.id,
    recipientDeviceKeyId: recipientOf(p, channel).id,
    createdAt: now,
    expiresAt: now + (opts.expiresIn ?? 3600),
    algorithm: "ECDH-P256+HKDF-SHA256+AES-256-GCM",
    ciphertextDigest: hex(await crypto.subtle.digest("SHA-256", ciphertext)),
    ciphertextSizeBytes: ciphertext.byteLength,
    nonce: b64url(crypto.getRandomValues(new Uint8Array(12))),
    senderEphemeralPublicKey: await ecdhPublicJwk(),
  };
  const envelope = { ...unsigned, signature: await signEnvelope(sender.keys.privateKey, unsigned) };
  const r = await signedFetch("/v1/rulebook/upload", "POST", { envelope, ciphertextBase64Url: b64url(ciphertext) }, sender.keys.privateKey, sender.id);
  return { r, ciphertext };
}

export async function consumeItem(p: Pair, channel: Channel, sequence: number, as = recipientOf(p, channel)) {
  const nextKey = await signedRulebookKey(p, channel);
  const r = await signedFetch("/v1/rulebook/consume", "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0, channel, sequence, nextKey }, as.keys.privateKey, as.id);
  return { r, nextKey };
}

export async function rewindRulebookUploadClock(p: Pair, channel: Channel) {
  await env.RELAY_DB.prepare(`UPDATE rulebook_mailboxes SET last_upload_at = last_upload_at - 3600 WHERE pair_id_hash = ?1 AND channel = ?2`)
    .bind(p.pairIdHash, channel)
    .run();
}
