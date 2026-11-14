import { afterEach, describe, expect, it } from "vitest";
import { b64url, randomCapabilityId, signEnvelope, signedFetch } from "./helpers";
import { ecdhPublicJwk, freshPair, publishKey, upload, type Pair } from "./mailbox-helpers";
import { publishRulebookKey, uploadItem } from "./rulebook-helpers";
import { connect, revoke, type TestSocket } from "./socket-helpers";

const open: TestSocket[] = [];
afterEach(() => { for (const s of open.splice(0)) s.close(); });

const now = () => Math.floor(Date.now() / 1000);

async function connected(): Promise<{ p: Pair; owner: TestSocket; sub: TestSocket }> {
  const p = await freshPair();
  const owner = await connect({ key: p.owner, id: p.ownerId });
  const sub = await connect({ key: p.sub, id: p.subId });
  open.push(owner, sub);
  return { p, owner, sub };
}

async function expectOneHint(to: TestSocket, notTo: TestSocket, p: Pair): Promise<void> {
  expect(await to.next()).toEqual({ type: "hint", pairIdHash: p.pairIdHash, pairEpoch: 0 });
  expect(await to.maybeNext(150)).toBeNull();
  expect(await notTo.maybeNext(150)).toBeNull();
}

const blob = () => ({ nonce: b64url(crypto.getRandomValues(new Uint8Array(12))), ciphertext: b64url(crypto.getRandomValues(new Uint8Array(48))) });

describe("pair hints", () => {
  it("collar status from the Sub hints the Owner", async () => {
    const { p, owner, sub } = await connected();
    const r = await signedFetch(`/v1/pairs/${p.pairIdHash}/collar-status`, "POST",
      { type: "collar-status", schemaVersion: 1, pairEpoch: 0, state: "locked", stateAt: now() }, p.sub.privateKey, p.subId);
    expect(r.status).toBe(200);
    await expectOneHint(owner, sub, p);
  });

  it("cage status from the Sub hints the Owner", async () => {
    const { p, owner, sub } = await connected();
    const r = await signedFetch(`/v1/pairs/${p.pairIdHash}/cage-status`, "POST",
      { type: "cage-status", schemaVersion: 1, pairEpoch: 0, state: "caged", stateAt: now(), endsAt: now() + 600 }, p.sub.privateKey, p.subId);
    expect(r.status).toBe(200);
    await expectOneHint(owner, sub, p);
  });

  it("a safeword report hints the Owner", async () => {
    const { p, owner, sub } = await connected();
    const r = await signedFetch(`/v1/pairs/${p.pairIdHash}/safeword`, "POST",
      { type: "safeword", schemaVersion: 1, pairEpoch: 0, at: now(), mood: "stop" }, p.sub.privateKey, p.subId);
    expect(r.status).toBe(200);
    await expectOneHint(owner, sub, p);
  });

  it("a stored character record hints the other member, a stale one doesn't", async () => {
    const { p, owner, sub } = await connected();
    const send = (seq: number) => signedFetch(`/v1/pairs/${p.pairIdHash}/characters`, "POST",
      { type: "pair-characters", schemaVersion: 1, pairEpoch: 0, seq, record: blob() }, p.owner.privateKey, p.ownerId);
    expect((await send(2)).status).toBe(200);
    await expectOneHint(sub, owner, p);
    expect((await send(1)).json.stored).toBe(false);
    expect(await sub.maybeNext(150)).toBeNull();
  });

  it("the Owner's catalog key and the Sub's catalog upload hint the other member", async () => {
    const { p, owner, sub } = await connected();
    const key = await publishKey(p);
    expect(key.r.status).toBe(200);
    await expectOneHint(sub, owner, p);
    expect((await upload(p, key.envelope.receiveKeyId, 1)).r.status).toBe(200);
    await expectOneHint(owner, sub, p);
  });

  it("a catalog request hints the Sub", async () => {
    const { p, owner, sub } = await connected();
    const unsigned = {
      type: "catalog-request", schemaVersion: 1, pairIdHash: p.pairIdHash, pairEpoch: 0, requestId: randomCapabilityId(),
      requesterDeviceKeyId: p.ownerId, ownerEphemeralPublicKey: await ecdhPublicJwk(), createdAt: now(), expiresAt: now() + 900,
    };
    const r = await signedFetch("/v1/catalog/requests", "POST", { ...unsigned, signature: await signEnvelope(p.owner.privateKey, unsigned) }, p.owner.privateKey, p.ownerId);
    expect(r.status).toBe(200);
    await expectOneHint(sub, owner, p);
  });

  it("the Sub's rulebook key and the Owner's rulebook upload hint the other member", async () => {
    const { p, owner, sub } = await connected();
    const key = await publishRulebookKey(p, "rulebook");
    expect(key.r.status).toBe(200);
    await expectOneHint(owner, sub, p);
    expect((await uploadItem(p, "rulebook", key.envelope.receiveKeyId, 1)).r.status).toBe(200);
    await expectOneHint(sub, owner, p);
  });

  it("a revocation hints the other member", async () => {
    const { p, owner, sub } = await connected();
    expect((await revoke(p.pairIdHash, 0, { key: p.sub, id: p.subId })).status).toBe(200);
    await expectOneHint(owner, sub, p);
  });

  it("a member that isn't connected gets nothing and the route still succeeds", async () => {
    const p = await freshPair();
    const r = await signedFetch(`/v1/pairs/${p.pairIdHash}/collar-status`, "POST",
      { type: "collar-status", schemaVersion: 1, pairEpoch: 0, state: "locked", stateAt: now() }, p.sub.privateKey, p.subId);
    expect(r.status).toBe(200);
  });
});
