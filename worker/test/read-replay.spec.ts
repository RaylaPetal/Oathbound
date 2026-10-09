import { env, SELF } from "cloudflare:test";
import canonicalize from "canonicalize";
import { describe, expect, it } from "vitest";
import { b64url, BASE, hex, type KeyPair } from "./helpers";
import { freshPair, publishKey } from "./mailbox-helpers";

/** One signed request, kept so it can be sent again byte for byte. */
async function signedRequest(path: string, method: string, bodyJson: object | undefined, signer: KeyPair, signerId: string) {
  const canonicalBody = canonicalize(bodyJson ?? {})!;
  const bodyDigest = hex(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(canonicalBody)));
  const timestamp = Math.floor(Date.now() / 1000);
  const nonce = b64url(crypto.getRandomValues(new Uint8Array(16)));
  const baseString = [method, path.split("?")[0], bodyDigest, String(timestamp), nonce].join("\n");
  const sig = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, signer.privateKey, new TextEncoder().encode(baseString));
  const headers = {
    "content-type": "application/json",
    "x-relay-device-key-id": signerId,
    "x-relay-timestamp": String(timestamp),
    "x-relay-nonce": nonce,
    "x-relay-signature": b64url(sig),
  };
  return () => SELF.fetch(BASE + path, { method, headers, ...(method === "GET" ? {} : { body: canonicalBody }) });
}

async function nonceCount(deviceKeyId: string): Promise<number> {
  const row = await env.RELAY_DB.prepare(`SELECT COUNT(*) AS n FROM nonces WHERE device_key_id = ?1`).bind(deviceKeyId).first<{ n: number }>();
  return row?.n ?? 0;
}

describe("read-only signed requests", () => {
  it("writes no nonce for pair status, mailbox status or key fetches", async () => {
    const p = await freshPair();
    await publishKey(p);
    const ownerBefore = await nonceCount(p.ownerId);
    const subBefore = await nonceCount(p.subId);

    const reads = [
      await signedRequest(`/v1/pairs/${p.pairIdHash}?epoch=0`, "GET", undefined, p.owner, p.ownerId),
      await signedRequest(`/v1/catalog/mailbox/status`, "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0 }, p.owner, p.ownerId),
      await signedRequest(`/v1/catalog/mailbox/key/fetch`, "POST", { pairIdHash: p.pairIdHash, pairEpoch: 0 }, p.sub, p.subId),
    ];
    for (const send of reads) expect((await send()).status).toBe(200);

    expect(await nonceCount(p.ownerId)).toBe(ownerBefore);
    expect(await nonceCount(p.subId)).toBe(subBefore);
  });

  it("answers a replayed pair status read the same way and changes nothing", async () => {
    const p = await freshPair();
    const send = await signedRequest(`/v1/pairs/${p.pairIdHash}?epoch=0`, "GET", undefined, p.owner, p.ownerId);
    const first = await send();
    const second = await send();
    expect(first.status).toBe(200);
    expect(second.status).toBe(200);
    expect((await second.json<{ pairIdHash: string }>()).pairIdHash).toBe(p.pairIdHash);
  });

  it("still rejects a replayed state-changing request", async () => {
    const p = await freshPair();
    const report = { type: "collar-status", schemaVersion: 1, pairEpoch: 0, state: "locked", stateAt: Math.floor(Date.now() / 1000) };
    const send = await signedRequest(`/v1/pairs/${p.pairIdHash}/collar-status`, "POST", report, p.sub, p.subId);
    expect((await send()).status).toBe(200);
    expect((await send()).status).toBe(401);
  });
});
