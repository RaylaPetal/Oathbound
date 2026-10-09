import { b64url, signedFetch } from "./helpers";
import type { Pair } from "./mailbox-helpers";

export const pictureId = () => b64url(crypto.getRandomValues(new Uint8Array(16)));

const signer = (p: Pair, as: "sub" | "owner") => (as === "sub" ? { key: p.sub.privateKey, id: p.subId } : { key: p.owner.privateKey, id: p.ownerId });

export function syncPictures(p: Pair, ids: string[], as: "sub" | "owner" = "sub") {
  const s = signer(p, as);
  return signedFetch("/v1/pictures/sync", "POST", { type: "picture-sync", schemaVersion: 1, pairIdHash: p.pairIdHash, pairEpoch: 0, ids }, s.key, s.id);
}

export function uploadPicture(p: Pair, id: string, bytes: Uint8Array = crypto.getRandomValues(new Uint8Array(512)), as: "sub" | "owner" = "sub", pairEpoch = 0) {
  const s = signer(p, as);
  const body = { type: "picture-upload", schemaVersion: 1, pairIdHash: p.pairIdHash, pairEpoch, pictureId: id, ciphertextBase64Url: b64url(bytes) };
  return signedFetch("/v1/pictures/upload", "POST", body, s.key, s.id);
}

export function fetchPictures(p: Pair, ids: string[], as: "sub" | "owner" = "owner") {
  const s = signer(p, as);
  return signedFetch("/v1/pictures/fetch", "POST", { type: "picture-fetch", schemaVersion: 1, pairIdHash: p.pairIdHash, pairEpoch: 0, ids }, s.key, s.id);
}
