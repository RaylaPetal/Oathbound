import { SELF } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { b64url, BASE, createInvitation, deviceKeyId, genSigningKeyPair, type KeyPair, randomCapabilityId, signedFetch } from "./helpers";

// collar/pairing-recovery: the relay stores an opaque backup per backupId, bound to the device key that
// first wrote it.
function backupBody(bytes = 256) {
  return {
    type: "backup",
    schemaVersion: 1,
    nonce: b64url(crypto.getRandomValues(new Uint8Array(12))),
    ciphertext: b64url(crypto.getRandomValues(new Uint8Array(bytes))),
  };
}

async function knownDevice(): Promise<{ key: KeyPair; id: string }> {
  const key = await genSigningKeyPair();
  const id = await deviceKeyId(key.publicKeyJwk);
  await createInvitation(key, id); // a device only has a backup after pairing, so its key is already on file
  return { key, id };
}

describe("backups", () => {
  let device: { key: KeyPair; id: string };
  let backupId: string;

  beforeEach(async () => {
    device = await knownDevice();
    backupId = randomCapabilityId();
  });

  it("round-trips a backup through a capability read", async () => {
    const body = backupBody();
    expect((await signedFetch(`/v1/backups/${backupId}`, "PUT", body, device.key.privateKey, device.id)).status).toBe(200);

    const response = await SELF.fetch(BASE + `/v1/backups/${backupId}`);
    expect(response.status).toBe(200);
    const json = (await response.json()) as any;
    expect(json.deviceKeyId).toBe(device.id);
    expect(json.nonce).toBe(body.nonce);
    expect(json.ciphertext).toBe(body.ciphertext);

    const updated = backupBody();
    expect((await signedFetch(`/v1/backups/${backupId}`, "PUT", updated, device.key.privateKey, device.id)).status).toBe(200);
    expect(((await (await SELF.fetch(BASE + `/v1/backups/${backupId}`)).json()) as any).ciphertext).toBe(updated.ciphertext);
  });

  it("refuses a different device overwriting or deleting someone's backup", async () => {
    await signedFetch(`/v1/backups/${backupId}`, "PUT", backupBody(), device.key.privateKey, device.id);
    const other = await knownDevice();
    expect((await signedFetch(`/v1/backups/${backupId}`, "PUT", backupBody(), other.key.privateKey, other.id)).status).toBe(401);
    expect((await signedFetch(`/v1/backups/${backupId}`, "DELETE", {}, other.key.privateKey, other.id)).status).toBe(401);
  });

  it("rejects an oversized backup and reports a missing one as not found", async () => {
    expect((await signedFetch(`/v1/backups/${backupId}`, "PUT", backupBody(40000), device.key.privateKey, device.id)).status).toBe(400);
    expect((await SELF.fetch(BASE + `/v1/backups/${randomCapabilityId()}`)).status).toBe(404);
  });

  it("deletes a backup for its own device", async () => {
    await signedFetch(`/v1/backups/${backupId}`, "PUT", backupBody(), device.key.privateKey, device.id);
    const deleted = await signedFetch(`/v1/backups/${backupId}`, "DELETE", {}, device.key.privateKey, device.id);
    expect(deleted.status).toBe(200);
    expect(deleted.json.deleted).toBe(true);
    expect((await SELF.fetch(BASE + `/v1/backups/${backupId}`)).status).toBe(404);
  });
});
