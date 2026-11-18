import { env } from "cloudflare:test";
import { beforeAll, describe, expect, it } from "vitest";
import { capabilityHash } from "../src/lib/capability";
import { b64url, deviceKeyId, genSigningKeyPair, hex, type Jwk, type KeyPair, pairDevices, randomCapabilityId, signEnvelope, signedFetch } from "./helpers";
import { ecdhPublicJwk, freshPair, type Pair } from "./mailbox-helpers";

describe("catalog request active-slot claim (no post-success cooldown)", () => {
  let owner: KeyPair;
  let sub: KeyPair;
  let ownerId: string;
  let subId: string;
  let pairIdHash: string;

  beforeAll(async () => {
    owner = await genSigningKeyPair();
    sub = await genSigningKeyPair();
    ownerId = await deviceKeyId(owner.publicKeyJwk);
    subId = await deviceKeyId(sub.publicKeyJwk);
    const { pair } = await pairDevices(owner, ownerId, sub, subId, "owner");
    pairIdHash = pair.pairIdHash;
  });

  /** Sends a real, freshly-valid createCatalogRequest as the paired Owner and returns the parsed response. */
  async function requestRefresh() {
    const ownerEphemeral = (await crypto.subtle.generateKey({ name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"])) as CryptoKeyPair;
    const jwkRaw = (await crypto.subtle.exportKey("jwk", ownerEphemeral.publicKey)) as JsonWebKey;
    const ownerEphemeralJwk: Jwk = { kty: "EC", crv: "P-256", x: jwkRaw.x!, y: jwkRaw.y! };
    const now = Math.floor(Date.now() / 1000);
    const unsigned = {
      type: "catalog-request",
      schemaVersion: 1,
      pairIdHash,
      pairEpoch: 0,
      requestId: randomCapabilityId(),
      requesterDeviceKeyId: ownerId,
      ownerEphemeralPublicKey: ownerEphemeralJwk,
      createdAt: now,
      expiresAt: now + 900,
    };
    const signature = await signEnvelope(owner.privateKey, unsigned);
    return signedFetch("/v1/catalog/requests", "POST", { ...unsigned, signature }, owner.privateKey, ownerId);
  }

  /** Directly wedges the pair's active-request slot with a request row whose expiry is `offsetSeconds` from now (negative = already expired). */
  async function seedActiveRequest(offsetSeconds: number, requesterDeviceKeyId = ownerId): Promise<string> {
    const requestIdHash = b64url(crypto.getRandomValues(new Uint8Array(32)));
    const expiresAt = Math.floor(Date.now() / 1000) + offsetSeconds;
    await env.RELAY_DB.prepare(
      `INSERT INTO catalog_requests (request_id_hash, pair_id_hash, pair_epoch, requester_device_key_id, owner_ephemeral_public_key_jwk, created_at, expires_at, status)
       VALUES (?1, ?2, 0, ?3, '{}', ?4, ?4, 'pending')`,
    )
      .bind(requestIdHash, pairIdHash, requesterDeviceKeyId, expiresAt)
      .run();
    await env.RELAY_DB.prepare(
      `INSERT INTO pair_cooldowns (pair_id_hash, pair_epoch, last_accepted_sync_at, last_snapshot_id, active_request_id_hash)
       VALUES (?1, 0, 0, 0, ?2)
       ON CONFLICT (pair_id_hash, pair_epoch) DO UPDATE SET active_request_id_hash = ?2`,
    )
      .bind(pairIdHash, requestIdHash)
      .run();
    return requestIdHash;
  }

  it("accepts a new request once the previously active request has expired, without a scheduled cleanup pass", async () => {
    await seedActiveRequest(-30);

    const r = await requestRefresh();

    expect(r.status).toBe(200);
    const row = await env.RELAY_DB.prepare(`SELECT active_request_id_hash FROM pair_cooldowns WHERE pair_id_hash = ?1 AND pair_epoch = 0`)
      .bind(pairIdHash)
      .first<{ active_request_id_hash: string }>();
    expect(row?.active_request_id_hash).toBe(await capabilityHash(r.json.requestId));
  });

  it("replaces the Owner's own still-valid request instead of rejecting it", async () => {
    const earlier = await seedActiveRequest(300);

    const r = await requestRefresh();

    expect(r.status).toBe(200);
    const slot = await env.RELAY_DB.prepare(`SELECT active_request_id_hash FROM pair_cooldowns WHERE pair_id_hash = ?1 AND pair_epoch = 0`)
      .bind(pairIdHash)
      .first<{ active_request_id_hash: string }>();
    expect(slot?.active_request_id_hash).toBe(await capabilityHash(r.json.requestId));
    const old = await env.RELAY_DB.prepare(`SELECT status FROM catalog_requests WHERE request_id_hash = ?1`)
      .bind(earlier)
      .first<{ status: string }>();
    expect(old?.status).toBe("expired");
  });

  it("rejects while another device's request is still valid, with a wait bounded by its remaining validity", async () => {
    await seedActiveRequest(120, "f".repeat(64));

    const r = await requestRefresh();

    expect(r.status).toBe(429);
    expect(r.json.code).toBe("cooldown_active");
    expect(r.json.retryAfterSeconds).toBeGreaterThan(0);
    expect(r.json.retryAfterSeconds).toBeLessThanOrEqual(120);
  });

  it("accepts a new request immediately after a successful sync when no request is active", async () => {
    await env.RELAY_DB.prepare(
      `INSERT INTO pair_cooldowns (pair_id_hash, pair_epoch, last_accepted_sync_at, last_snapshot_id, active_request_id_hash)
       VALUES (?1, 0, ?2, 1, NULL)
       ON CONFLICT (pair_id_hash, pair_epoch) DO UPDATE SET last_accepted_sync_at = ?2, active_request_id_hash = NULL`,
    )
      .bind(pairIdHash, Math.floor(Date.now() / 1000) - 1)
      .run();

    const r = await requestRefresh();

    expect(r.status).toBe(200);
  });
});

describe("a replaced catalog request", () => {
  async function request(p: Pair) {
    const requestId = randomCapabilityId();
    const now = Math.floor(Date.now() / 1000);
    const unsigned = {
      type: "catalog-request",
      schemaVersion: 1,
      pairIdHash: p.pairIdHash,
      pairEpoch: 0,
      requestId,
      requesterDeviceKeyId: p.ownerId,
      ownerEphemeralPublicKey: await ecdhPublicJwk(),
      createdAt: now,
      expiresAt: now + 900,
    };
    const signature = await signEnvelope(p.owner.privateKey, unsigned);
    const r = await signedFetch("/v1/catalog/requests", "POST", { ...unsigned, signature }, p.owner.privateKey, p.ownerId);
    return { r, requestId };
  }

  async function answer(p: Pair, requestId: string, snapshotId: number) {
    const ciphertext = crypto.getRandomValues(new Uint8Array(64));
    const now = Math.floor(Date.now() / 1000);
    const unsigned = {
      type: "catalog-response",
      schemaVersion: 1,
      pairIdHash: p.pairIdHash,
      pairEpoch: 0,
      requestId,
      snapshotId,
      senderDeviceKeyId: p.subId,
      recipientDeviceKeyId: p.ownerId,
      createdAt: now,
      expiresAt: now + 900,
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

  it("can no longer be answered, while the request that replaced it still completes", async () => {
    const p = await freshPair();
    const first = await request(p);
    const second = await request(p);
    expect(first.r.status).toBe(200);
    expect(second.r.status).toBe(200);

    const late = await answer(p, first.requestId, 1);
    expect(late.status).toBe(410);
    expect(late.json.code).toBe("expired");

    const current = await answer(p, second.requestId, 1);
    expect(current.status).toBe(200);
  });
});
