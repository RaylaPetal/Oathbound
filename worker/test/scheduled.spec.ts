import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { runScheduledCleanup } from "../src/scheduled";

describe("scheduled cleanup", () => {
  it("deletes expired invitations, revocations, stale catalog requests, and their R2 objects", async () => {
    const past = Math.floor(Date.now() / 1000) - 3600;
    const deviceKeyId = "a".repeat(64);

    await env.RELAY_DB.prepare(
      `INSERT INTO invitations (invitation_id_hash, inviter_device_key_id, inviter_public_key_jwk, role, created_at, expires_at, signature, status)
       VALUES ('expired-inv-hash', ?2, '{}', 'owner', ?1, ?1, ?3, 'pending')`,
    )
      .bind(past, deviceKeyId, "s".repeat(86))
      .run();

    await env.RELAY_DB.prepare(
      `INSERT INTO revocations (pair_id_hash, sequence, pair_epoch, reason, issued_by_device_key_id, signature, created_at, expires_at)
       VALUES ('pair-hash-1', 1, 0, 'unpair', 'device-1', 'sig', ?1, ?1)`,
    )
      .bind(past)
      .run();

    await env.RELAY_DB.prepare(
      `INSERT INTO pair_cooldowns (pair_id_hash, pair_epoch, last_accepted_sync_at, last_snapshot_id, active_request_id_hash)
       VALUES ('pair-hash-2', 0, 0, 0, 'stale-request-hash')`,
    ).run();
    await env.RELAY_DB.prepare(
      `INSERT INTO catalog_requests (request_id_hash, pair_id_hash, pair_epoch, requester_device_key_id, owner_ephemeral_public_key_jwk, created_at, expires_at, status)
       VALUES ('stale-request-hash', 'pair-hash-2', 0, 'device-1', '{}', ?1, ?1, 'pending')`,
    )
      .bind(past)
      .run();

    const orphanKey = "catalog/orphan-object-hash";
    await env.RELAY_CATALOG_BUCKET.put(orphanKey, new TextEncoder().encode("leftover"));
    await env.RELAY_DB.prepare(
      `INSERT INTO catalog_requests (request_id_hash, pair_id_hash, pair_epoch, requester_device_key_id, owner_ephemeral_public_key_jwk, created_at, expires_at, status)
       VALUES ('orphan-object-hash', 'pair-hash-3', 0, 'device-1', '{}', ?1, ?1, 'uploaded')`,
    )
      .bind(past)
      .run();
    await env.RELAY_DB.prepare(
      `INSERT INTO catalog_objects (request_id_hash, r2_key, sender_device_key_id, recipient_device_key_id, snapshot_id, ciphertext_digest, ciphertext_size_bytes, nonce, sender_ephemeral_public_key_jwk, algorithm, created_at, expires_at)
       VALUES ('orphan-object-hash', ?2, 'device-1', 'device-2', 1, ?3, 5, ?4, '{}', 'ECDH-P256+HKDF-SHA256+AES-256-GCM', ?1, ?1)`,
    )
      .bind(past, orphanKey, "x".repeat(64), "y".repeat(16))
      .run();

    await runScheduledCleanup(env);

    const invitationRow = await env.RELAY_DB.prepare(`SELECT 1 FROM invitations WHERE invitation_id_hash = 'expired-inv-hash'`).first();
    expect(invitationRow).toBeNull();

    const revocationRow = await env.RELAY_DB.prepare(`SELECT 1 FROM revocations WHERE pair_id_hash = 'pair-hash-1'`).first();
    expect(revocationRow).toBeNull();

    const cooldownRow = await env.RELAY_DB.prepare(`SELECT active_request_id_hash FROM pair_cooldowns WHERE pair_id_hash = 'pair-hash-2'`).first<{
      active_request_id_hash: string | null;
    }>();
    expect(cooldownRow?.active_request_id_hash).toBeNull();

    const catalogRequestRow = await env.RELAY_DB.prepare(`SELECT 1 FROM catalog_requests WHERE request_id_hash = 'stale-request-hash'`).first();
    expect(catalogRequestRow).toBeNull();

    const catalogObjectRow = await env.RELAY_DB.prepare(`SELECT 1 FROM catalog_objects WHERE request_id_hash = 'orphan-object-hash'`).first();
    expect(catalogObjectRow).toBeNull();

    const r2Object = await env.RELAY_CATALOG_BUCKET.get(orphanKey);
    expect(r2Object).toBeNull();
  });

  it("drops expired mailbox snapshots, removes revoked pairs' mailboxes, and sweeps orphaned mailbox objects", async () => {
    const now = Math.floor(Date.now() / 1000);
    const past = now - 3600;
    const insertPair = (hash: string, revokedAt: number | null) =>
      env.RELAY_DB.prepare(
        `INSERT INTO pairs (pair_id_hash, pair_epoch, owner_device_key_id, sub_device_key_id, created_at, revoked_at) VALUES (?1, 0, 'o', 's', ?2, ?3)`,
      )
        .bind(hash, past, revokedAt)
        .run();
    const insertMailbox = (hash: string, r2Key: string, expiresAt: number) =>
      env.RELAY_DB.prepare(
        `INSERT INTO catalog_mailboxes (pair_id_hash, pair_epoch, receive_key_id, receive_key_envelope, key_published_at, last_upload_at,
           snapshot_id, snapshot_r2_key, snapshot_envelope, snapshot_created_at, snapshot_expires_at)
         VALUES (?1, 0, 'key-id', '{}', ?2, ?2, 1, ?3, '{}', ?2, ?4)`,
      )
        .bind(hash, past, r2Key, expiresAt)
        .run();

    await insertPair("mbx-expired", null);
    await insertMailbox("mbx-expired", "mailbox/mbx-expired/0/1", past);
    await env.RELAY_CATALOG_BUCKET.put("mailbox/mbx-expired/0/1", new Uint8Array([1]));

    await insertPair("mbx-live", null);
    await insertMailbox("mbx-live", "mailbox/mbx-live/0/1", now + 3600);
    await env.RELAY_CATALOG_BUCKET.put("mailbox/mbx-live/0/1", new Uint8Array([1]));

    await insertPair("mbx-revoked", past);
    await insertMailbox("mbx-revoked", "mailbox/mbx-revoked/0/1", now + 3600);
    await env.RELAY_CATALOG_BUCKET.put("mailbox/mbx-revoked/0/1", new Uint8Array([1]));

    await env.RELAY_CATALOG_BUCKET.put("mailbox/mbx-live/0/0", new Uint8Array([1])); // replaced but never deleted

    await runScheduledCleanup(env);

    const expired = await env.RELAY_DB.prepare(`SELECT receive_key_id, snapshot_id FROM catalog_mailboxes WHERE pair_id_hash = 'mbx-expired'`).first<{
      receive_key_id: string;
      snapshot_id: number | null;
    }>();
    expect(expired).toEqual({ receive_key_id: "key-id", snapshot_id: null });
    expect(await env.RELAY_CATALOG_BUCKET.head("mailbox/mbx-expired/0/1")).toBeNull();

    const live = await env.RELAY_DB.prepare(`SELECT snapshot_id FROM catalog_mailboxes WHERE pair_id_hash = 'mbx-live'`).first<{ snapshot_id: number }>();
    expect(live?.snapshot_id).toBe(1);
    expect(await env.RELAY_CATALOG_BUCKET.head("mailbox/mbx-live/0/1")).not.toBeNull();
    expect(await env.RELAY_CATALOG_BUCKET.head("mailbox/mbx-live/0/0")).toBeNull();

    expect(await env.RELAY_DB.prepare(`SELECT 1 FROM catalog_mailboxes WHERE pair_id_hash = 'mbx-revoked'`).first()).toBeNull();
    expect(await env.RELAY_CATALOG_BUCKET.head("mailbox/mbx-revoked/0/1")).toBeNull();
  });

  it("drops expired rulebook items per channel, removes revoked pairs' rulebook mailboxes, and sweeps orphaned rulebook objects", async () => {
    const now = Math.floor(Date.now() / 1000);
    const past = now - 3600;
    const insertPair = (hash: string, revokedAt: number | null) =>
      env.RELAY_DB.prepare(
        `INSERT INTO pairs (pair_id_hash, pair_epoch, owner_device_key_id, sub_device_key_id, created_at, revoked_at) VALUES (?1, 0, 'o', 's', ?2, ?3)`,
      )
        .bind(hash, past, revokedAt)
        .run();
    const insertMailbox = async (hash: string, channel: string, expiresAt: number) => {
      const r2Key = `rulebook/${hash}/0/${channel}/1`;
      await env.RELAY_DB.prepare(
        `INSERT INTO rulebook_mailboxes (pair_id_hash, pair_epoch, channel, receive_key_id, receive_key_envelope, key_published_at, last_upload_at,
           last_sequence, item_sequence, item_r2_key, item_envelope, item_created_at, item_expires_at)
         VALUES (?1, 0, ?2, 'key-id', '{}', ?3, ?3, 1, 1, ?4, '{}', ?3, ?5)`,
      )
        .bind(hash, channel, past, r2Key, expiresAt)
        .run();
      await env.RELAY_CATALOG_BUCKET.put(r2Key, new Uint8Array([1]));
      return r2Key;
    };

    await insertPair("rb-live", null);
    const expiredKey = await insertMailbox("rb-live", "rulebook", past);
    const liveKey = await insertMailbox("rb-live", "report", now + 3600);
    await env.RELAY_CATALOG_BUCKET.put("rulebook/rb-live/0/report/0", new Uint8Array([1])); // replaced but never deleted

    await insertPair("rb-revoked", past);
    const revokedKey = await insertMailbox("rb-revoked", "rulebook", now + 3600);

    await runScheduledCleanup(env);

    const expired = await env.RELAY_DB.prepare(`SELECT receive_key_id, item_sequence FROM rulebook_mailboxes WHERE pair_id_hash = 'rb-live' AND channel = 'rulebook'`).first();
    expect(expired).toEqual({ receive_key_id: "key-id", item_sequence: null });
    expect(await env.RELAY_CATALOG_BUCKET.head(expiredKey)).toBeNull();

    const live = await env.RELAY_DB.prepare(`SELECT item_sequence FROM rulebook_mailboxes WHERE pair_id_hash = 'rb-live' AND channel = 'report'`).first<{ item_sequence: number }>();
    expect(live?.item_sequence).toBe(1);
    expect(await env.RELAY_CATALOG_BUCKET.head(liveKey)).not.toBeNull();
    expect(await env.RELAY_CATALOG_BUCKET.head("rulebook/rb-live/0/report/0")).toBeNull();

    expect(await env.RELAY_DB.prepare(`SELECT 1 FROM rulebook_mailboxes WHERE pair_id_hash = 'rb-revoked'`).first()).toBeNull();
    expect(await env.RELAY_CATALOG_BUCKET.head(revokedKey)).toBeNull();
  });

  it("drops nonces once they're past the replay window", async () => {
    const now = Math.floor(Date.now() / 1000);
    const insert = (device: string, nonce: string, seenAt: number) =>
      env.RELAY_DB.prepare(`INSERT INTO nonces (device_key_id, nonce, seen_at) VALUES (?1, ?2, ?3)`).bind(device, nonce, seenAt).run();
    await env.RELAY_DB.prepare(`DELETE FROM nonces`).run();
    await insert("device-a", "n1", now - 60);
    await insert("device-a", "n2", now - 3000);

    await runScheduledCleanup(env);

    expect(await env.RELAY_DB.prepare(`SELECT 1 FROM nonces WHERE nonce = 'n1'`).first()).not.toBeNull();
    expect(await env.RELAY_DB.prepare(`SELECT 1 FROM nonces WHERE nonce = 'n2'`).first()).toBeNull();
  });
});
