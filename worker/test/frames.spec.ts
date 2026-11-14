import { env, runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { afterEach, describe, expect, it } from "vitest";
import type { DeviceHub } from "../src/hub/DeviceHub";
import { connect, frame, livePair, revoke, type TestSocket } from "./socket-helpers";

const open: TestSocket[] = [];
const track = (s: TestSocket) => { open.push(s); return s; };
afterEach(() => { for (const s of open.splice(0)) s.close(); });

const hubOf = (id: string) => env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(id));

async function disconnect(s: TestSocket): Promise<void> {
  s.close();
  // Let the hub see the close before the next send.
  await new Promise((r) => setTimeout(r, 50));
}

describe("frame forwarding", () => {
  it("forwards a frame to the other member unchanged and acks delivered", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner));
    const sub = track(await connect(p.sub));
    const sent = frame(p.pairIdHash, 0, "owner", 1);
    owner.send(sent);

    const got = await sub.next();
    expect(got).toEqual({
      type: "frame", pairIdHash: sent.pairIdHash, pairEpoch: sent.pairEpoch, from: sent.from, seq: sent.seq,
      sentAt: sent.sentAt, nonce: sent.nonce, ciphertext: sent.ciphertext,
    });
    expect(await owner.next()).toEqual({ type: "ack", id: sent.id, result: "delivered" });
  });

  it("works in both directions", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner));
    const sub = track(await connect(p.sub));
    sub.send(frame(p.pairIdHash, 0, "sub", 7));
    expect((await owner.next()).type).toBe("frame");
    expect((await sub.next()).result).toBe("delivered");
  });

  it("refuses a frame from a device that isn't a member", async () => {
    const p = await livePair();
    // A device with its key on file, paired with someone else.
    const stranger = (await livePair()).owner;
    const sub = track(await connect(p.sub));
    const s = track(await connect(stranger));
    s.send(frame(p.pairIdHash, 0, "owner", 1));
    expect((await s.next()).result).toBe("refused");
    expect(await sub.maybeNext()).toBeNull();
  });

  it("refuses a frame that claims the other member's side", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner));
    const sub = track(await connect(p.sub));
    owner.send(frame(p.pairIdHash, 0, "sub", 1));
    expect((await owner.next()).result).toBe("refused");
    expect(await sub.maybeNext()).toBeNull();
  });

  it("refuses a frame for a wrong epoch or a revoked pairing", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner));
    owner.send(frame(p.pairIdHash, 1, "owner", 1));
    expect((await owner.next()).result).toBe("refused");

    expect((await revoke(p.pairIdHash, 0, p.sub)).status).toBe(200);
    expect((await owner.next()).type).toBe("hint");
    owner.send(frame(p.pairIdHash, 0, "owner", 2));
    expect((await owner.next()).result).toBe("refused");
  });

  it("refuses a frame whose send time is far off", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner));
    owner.send(frame(p.pairIdHash, 0, "owner", 1, { sentAt: Math.floor(Date.now() / 1000) - 3600 }));
    expect((await owner.next()).result).toBe("refused");
  });
});

describe("delivery while the peer is away", () => {
  it("reports needs_update for a peer that never connected", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner));
    owner.send(frame(p.pairIdHash, 0, "owner", 1));
    expect((await owner.next()).result).toBe("needs_update");
  });

  it("holds a frame for a reconnect and delivers it in order", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner));
    await disconnect(await connect(p.sub));

    owner.send(frame(p.pairIdHash, 0, "owner", 1));
    owner.send(frame(p.pairIdHash, 0, "owner", 2));
    expect(await owner.next()).toEqual({ type: "ack", id: "m1", result: "held" });
    expect(await owner.next()).toEqual({ type: "ack", id: "m2", result: "held" });

    const sub = track(await connect(p.sub));
    expect((await sub.next()).seq).toBe(1);
    expect((await sub.next()).seq).toBe(2);
    const updates = [await owner.next(), await owner.next()];
    expect(updates).toEqual(expect.arrayContaining([
      { type: "ack", id: "m1", result: "delivered" },
      { type: "ack", id: "m2", result: "delivered" },
    ]));
  });

  it("drops a held frame after the hold window and tells the sender it was offline", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner));
    await disconnect(await connect(p.sub));

    owner.send(frame(p.pairIdHash, 0, "owner", 1));
    expect((await owner.next()).result).toBe("held");

    const subHub = hubOf(p.sub.id);
    await runInDurableObject(subHub, (_hub: DeviceHub, state) => {
      state.storage.sql.exec(`UPDATE held_frames SET expires_at = 0`);
    });
    await runDurableObjectAlarm(subHub);
    expect(await owner.next()).toEqual({ type: "ack", id: "m1", result: "offline" });

    const sub = track(await connect(p.sub));
    expect(await sub.maybeNext()).toBeNull();
  });
});

describe("per-device limits", () => {
  it("refuses a burst over the limit, but still forwards a safety frame from the Sub", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner));
    const sub = track(await connect(p.sub));

    for (let i = 1; i <= 21; i++) sub.send(frame(p.pairIdHash, 0, "sub", i));
    const results: string[] = [];
    for (let i = 1; i <= 21; i++) results.push((await sub.next()).result as string);
    expect(results.filter((r) => r === "delivered")).toHaveLength(20);
    expect(results[20]).toBe("over_limit");
    for (let i = 0; i < 20; i++) await owner.next();

    sub.send(frame(p.pairIdHash, 0, "sub", 22, { safety: true }));
    expect((await sub.next()).result).toBe("delivered");
    expect((await owner.next()).seq).toBe(22);
  });

  it("ignores the safety flag from the Owner side", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner));
    track(await connect(p.sub));
    for (let i = 1; i <= 20; i++) owner.send(frame(p.pairIdHash, 0, "owner", i));
    for (let i = 1; i <= 20; i++) await owner.next();
    owner.send(frame(p.pairIdHash, 0, "owner", 21, { safety: true }));
    expect((await owner.next()).result).toBe("over_limit");
  });
});

describe("after a revocation", () => {
  it("the revoking device's own hub stops forwarding at once", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner));
    const sub = track(await connect(p.sub));
    sub.send(frame(p.pairIdHash, 0, "sub", 1));
    expect((await sub.next()).result).toBe("delivered");
    await owner.next();

    expect((await revoke(p.pairIdHash, 0, p.sub)).status).toBe(200);
    await owner.next();
    sub.send(frame(p.pairIdHash, 0, "sub", 2));
    expect((await sub.next()).result).toBe("refused");
  });
});
