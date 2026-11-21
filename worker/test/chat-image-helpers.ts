import { b64url, signedFetch } from "./helpers";
import type { Pair } from "./mailbox-helpers";

export const imageId = () => b64url(crypto.getRandomValues(new Uint8Array(16)));

const signer = (p: Pair, as: "sub" | "owner") => (as === "sub" ? { key: p.sub.privateKey, id: p.subId } : { key: p.owner.privateKey, id: p.ownerId });

export function uploadChatImage(p: Pair, id: string, bytes: Uint8Array = crypto.getRandomValues(new Uint8Array(2048)), as: "sub" | "owner" = "owner", pairEpoch = 0) {
  const s = signer(p, as);
  const body = { type: "chat-image-upload", schemaVersion: 1, pairIdHash: p.pairIdHash, pairEpoch, imageId: id, ciphertextBase64Url: b64url(bytes) };
  return signedFetch("/v1/chat-images/upload", "POST", body, s.key, s.id);
}

export function fetchChatImage(p: Pair, id: string, as: "sub" | "owner" = "sub", pairEpoch = 0) {
  const s = signer(p, as);
  return signedFetch("/v1/chat-images/fetch", "POST", { type: "chat-image-fetch", schemaVersion: 1, pairIdHash: p.pairIdHash, pairEpoch, imageId: id }, s.key, s.id);
}
