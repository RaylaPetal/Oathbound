import { env } from "cloudflare:test";
import { afterEach, describe, expect, it } from "vitest";
import {
  acceptInvitation,
  b64url,
  createInvitation,
  deviceKeyId,
  genSigningKeyPair,
  hex,
  type KeyPair,
  randomCapabilityId,
  relayFetch,
  signEnvelope,
  signedFetch,
  useFetcher,
} from "./helpers";
import { consume, ecdhPublicJwk, fetchKey, freshPair, publishKey, status, upload, type Pair } from "./mailbox-helpers";
import { type Channel, consumeItem, fetchRulebookKey, publishRulebookKey, uploadItem } from "./rulebook-helpers";
import { fetchPictures, pictureId, syncPictures, uploadPicture } from "./picture-helpers";
import { ROUTE_WEIGHTS, routeKey } from "../src/lib/routeWeights";
import { emptyTally, meteredFetcher, type WriteTally } from "./write-meter";

/** Highest tally seen per route key ("METHOD /v1/shape/:id"). */
const measured = new Map<string, WriteTally>();

interface Result {
  status: number;
}

async function measure(key: string, send: () => Promise<Result | Response>): Promise<void> {
  // Per-device quota rows are usually new each hour for a real client, so measure the insert, not the cheaper update.
  await env.RELAY_DB.prepare(`DELETE FROM quota_counters WHERE scope NOT LIKE 'global%'`).run();
  const tally = emptyTally();
  const metered = meteredFetcher(tally);
  const sent: string[] = [];
  useFetcher((request) => {
    sent.push(routeKey(request.method, new URL(request.url).pathname.split("/").filter(Boolean)));
    return metered(request);
  });
  let result: Result | Response;
  try {
    result = await send();
  } finally {
    useFetcher();
  }
  expect(result.status, key).toBe(200);
  expect(sent, key).toEqual([key]);
  const previous = measured.get(key);
  measured.set(key, {
    rowsWritten: Math.max(previous?.rowsWritten ?? 0, tally.rowsWritten),
    rowsTouched: Math.max(previous?.rowsTouched ?? 0, tally.rowsTouched),
  });
}

const now = () => Math.floor(Date.now() / 1000);

function blob() {
  return { nonce: b64url(crypto.getRandomValues(new Uint8Array(12))), ciphertext: b64url(crypto.getRandomValues(new Uint8Array(48))) };
}

async function device(): Promise<{ key: KeyPair; id: string }> {
  const key = await genSigningKeyPair();
  return { key, id: await deviceKeyId(key.publicKeyJwk) };
}

async function createCodeInvitation(inviter: { key: KeyPair; id: string }) {
  const unsigned = {
    type: "invitation",
    schemaVersion: 1,
    invitationId: randomCapabilityId(),
    inviterDeviceKeyId: inviter.id,
    inviterPublicKey: inviter.key.publicKeyJwk,
    role: "owner",
    kind: "code",
    encryptedCharacter: blob(),
    createdAt: now(),
    expiresAt: now() + 7 * 86400,
  };
  const signature = await signEnvelope(inviter.key.privateKey, unsigned);
  const r = await signedFetch("/v1/invitations", "POST", { ...unsigned, signature }, inviter.key.privateKey, inviter.id);
  return { r, invitationId: unsigned.invitationId };
}

async function revocation(p: Pair) {
  const unsigned = {
    type: "revocation",
    schemaVersion: 1,
    pairIdHash: p.pairIdHash,
    pairEpoch: 0,
    sequence: 1,
    reason: "unpair",
    issuedByDeviceKeyId: p.subId,
    createdAt: now(),
    expiresAt: now() + 604800,
  };
  const signature = await signEnvelope(p.sub.privateKey, unsigned);
  return signedFetch("/v1/revocations", "POST", { ...unsigned, signature }, p.sub.privateKey, p.subId);
}

async function catalogRequest(p: Pair) {
  const requestId = randomCapabilityId();
  const unsigned = {
    type: "catalog-request",
    schemaVersion: 1,
    pairIdHash: p.pairIdHash,
    pairEpoch: 0,
    requestId,
    requesterDeviceKeyId: p.ownerId,
    ownerEphemeralPublicKey: await ecdhPublicJwk(),
    createdAt: now(),
    expiresAt: now() + 900,
  };
  const signature = await signEnvelope(p.owner.privateKey, unsigned);
  const r = await signedFetch("/v1/catalog/requests", "POST", { ...unsigned, signature }, p.owner.privateKey, p.ownerId);
  return { r, requestId };
}

async function catalogUpload(p: Pair, requestId: string) {
  const ciphertext = crypto.getRandomValues(new Uint8Array(64));
  const unsigned = {
    type: "catalog-response",
    schemaVersion: 1,
    pairIdHash: p.pairIdHash,
    pairEpoch: 0,
    requestId,
    snapshotId: 1,
    senderDeviceKeyId: p.subId,
    recipientDeviceKeyId: p.ownerId,
    createdAt: now(),
    expiresAt: now() + 900,
    algorithm: "ECDH-P256+HKDF-SHA256+AES-256-GCM",
    ciphertextSizeBytes: ciphertext.byteLength,
    ciphertextDigest: hex(await crypto.subtle.digest("SHA-256", ciphertext)),
    nonce: b64url(crypto.getRandomValues(new Uint8Array(12))),
    senderEphemeralPublicKey: await ecdhPublicJwk(),
  };
  const signature = await signEnvelope(p.sub.privateKey, unsigned);
  return signedFetch(
    `/v1/catalog/requests/${requestId}/upload`,
    "POST",
    { envelope: { ...unsigned, signature }, ciphertextBase64Url: b64url(ciphertext) },
    p.sub.privateKey,
    p.subId,
  );
}

function backupBody() {
  return { type: "backup", schemaVersion: 1, nonce: b64url(crypto.getRandomValues(new Uint8Array(12))), ciphertext: b64url(crypto.getRandomValues(new Uint8Array(256))) };
}

/** Drives one successful request per route through the metered worker. */
async function measureEveryRoute(): Promise<void> {
  // Warm the day's global counter so no route is charged for creating it.
  await relayFetch("/v1/health");
  await measure("GET /v1/health", () => relayFetch("/v1/health"));

  // Invitations, both kinds.
  const owner = await device();
  const sub = await device();
  let tell!: Awaited<ReturnType<typeof createInvitation>>;
  await measure("POST /v1/invitations", async () => (tell = await createInvitation(owner.key, owner.id)));
  await measure("GET /v1/invitations/:id", () => relayFetch(`/v1/invitations/${tell.json.invitationId}`));
  await measure("POST /v1/invitations/:id/accept", () => acceptInvitation(tell.json.invitationId, sub.key, sub.id));
  await measure("POST /v1/invitations/:id/consume", () =>
    signedFetch(`/v1/invitations/${tell.json.invitationId}/consume`, "POST", {}, owner.key.privateKey, owner.id),
  );
  let code!: Awaited<ReturnType<typeof createCodeInvitation>>;
  await measure("POST /v1/invitations", async () => {
    code = await createCodeInvitation(owner);
    return code.r;
  });
  await measure("POST /v1/invitations/:id/cancel", () =>
    signedFetch(`/v1/invitations/${code.invitationId}/cancel`, "POST", {}, owner.key.privateKey, owner.id),
  );

  // Backups: first write and overwrite.
  const backupId = randomCapabilityId();
  await measure("PUT /v1/backups/:id", () => signedFetch(`/v1/backups/${backupId}`, "PUT", backupBody(), owner.key.privateKey, owner.id));
  await measure("PUT /v1/backups/:id", () => signedFetch(`/v1/backups/${backupId}`, "PUT", backupBody(), owner.key.privateKey, owner.id));
  await measure("GET /v1/backups/:id", () => relayFetch(`/v1/backups/${backupId}`));
  await measure("DELETE /v1/backups/:id", () => signedFetch(`/v1/backups/${backupId}`, "DELETE", {}, owner.key.privateKey, owner.id));

  // Pair status and collar status.
  const p = await freshPair();
  await measure("GET /v1/pairs/:id", () => signedFetch(`/v1/pairs/${p.pairIdHash}?epoch=0`, "GET", undefined, p.owner.privateKey, p.ownerId));
  await measure("POST /v1/pairs/status", () =>
    signedFetch("/v1/pairs/status", "POST", { type: "pair-status-batch-request", schemaVersion: 1, pairs: [{ pairIdHash: p.pairIdHash, pairEpoch: 0 }] }, p.owner.privateKey, p.ownerId),
  );
  await measure("POST /v1/pairs/:id/collar-status", () =>
    signedFetch(`/v1/pairs/${p.pairIdHash}/collar-status`, "POST", { type: "collar-status", schemaVersion: 1, pairEpoch: 0, state: "locked", stateAt: now() }, p.sub.privateKey, p.subId),
  );

  // Catalog request/response.
  let request!: Awaited<ReturnType<typeof catalogRequest>>;
  await measure("POST /v1/catalog/requests", async () => {
    request = await catalogRequest(p);
    return request.r;
  });
  await measure("GET /v1/catalog/requests/:id", () => relayFetch(`/v1/catalog/requests/${request.requestId}`));
  await measure("POST /v1/catalog/requests/:id/upload", () => catalogUpload(p, request.requestId));
  await measure("POST /v1/catalog/requests/:id/consume", () =>
    signedFetch(`/v1/catalog/requests/${request.requestId}/consume`, "POST", {}, p.owner.privateKey, p.ownerId),
  );

  // Catalog mailbox, on its own pair so the request flow's snapshot doesn't collide.
  const m = await freshPair();
  let key!: Awaited<ReturnType<typeof publishKey>>;
  await measure("POST /v1/catalog/mailbox/key", async () => {
    key = await publishKey(m);
    return key.r;
  });
  await measure("POST /v1/catalog/mailbox/key/fetch", () => fetchKey(m));
  await measure("POST /v1/catalog/mailbox/upload", async () => (await upload(m, key.envelope.receiveKeyId, 1)).r);
  await measure("POST /v1/catalog/mailbox/status", () => status(m));
  await measure("POST /v1/catalog/mailbox/consume", async () => (await consume(m, 1)).r);

  const picture = pictureId();
  await measure("POST /v1/pictures/sync", () => syncPictures(m, [picture]));
  await measure("POST /v1/pictures/upload", () => uploadPicture(m, picture));
  await measure("POST /v1/pictures/fetch", () => fetchPictures(m, [picture]));
  await measure("POST /v1/pictures/sync", () => syncPictures(m, []));

  // Rulebook mailbox, both channels.
  for (const channel of ["rulebook", "report"] as Channel[]) {
    let rbKey!: Awaited<ReturnType<typeof publishRulebookKey>>;
    await measure("POST /v1/rulebook/key", async () => {
      rbKey = await publishRulebookKey(p, channel);
      return rbKey.r;
    });
    await measure("POST /v1/rulebook/key/fetch", () => fetchRulebookKey(p, channel));
    await measure("POST /v1/rulebook/upload", async () => (await uploadItem(p, channel, rbKey.envelope.receiveKeyId, 1)).r);
    await measure("POST /v1/rulebook/consume", async () => (await consumeItem(p, channel, 1)).r);
  }

  // Revocations last, since publishing one ends the pair.
  await measure("GET /v1/revocations/:id", () => signedFetch(`/v1/revocations/${p.pairIdHash}?sinceSequence=0`, "GET", undefined, p.owner.privateKey, p.ownerId));
  await measure("POST /v1/revocations", () => revocation(p));
}

describe("rows written per route", () => {
  afterEach(() => useFetcher());

  it("charges every route at least the rows one request writes plus what the cron later clears", async () => {
    await measureEveryRoute();
    const table = [...measured.entries()].sort(([a], [b]) => a.localeCompare(b));

    expect(Object.keys(ROUTE_WEIGHTS).sort()).toEqual(table.map(([route]) => route));
    for (const [route, t] of table) {
      expect(ROUTE_WEIGHTS[route], `${route} written=${t.rowsWritten} touched=${t.rowsTouched}`).toBeGreaterThanOrEqual(t.rowsWritten + t.rowsTouched);
    }
  });
});
