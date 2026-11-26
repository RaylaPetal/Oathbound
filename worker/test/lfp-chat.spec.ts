import { env, runInDurableObject } from "cloudflare:test";
import { afterEach, describe, expect, it } from "vitest";
import type { DeviceHub } from "../src/hub/DeviceHub";
import { b64url } from "./helpers";
import { chat, connect, livePair, type Device, type TestSocket } from "./socket-helpers";
import { del, get, matched, post, spreadOrigins } from "./lfp-helpers";

spreadOrigins();

const FEATURES = ["chat", "match"];
const open: TestSocket[] = [];
const track = (s: TestSocket) => { open.push(s); return s; };
afterEach(() => { for (const s of open.splice(0)) s.close(); });

const hubOf = (id: string) => env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(id));

async function disconnect(s: TestSocket): Promise<void> {
  s.close();
  await new Promise((r) => setTimeout(r, 50));
}

function matchChat(matchId: string, from: "a" | "b", seq: number, extra: Record<string, unknown> = {}) {
  return {
    type: "send", id: `x${seq}`, channel: "match", matchId, from, seq, sentAt: Math.floor(Date.now() / 1000),
    nonce: b64url(crypto.getRandomValues(new Uint8Array(12))), ciphertext: b64url(crypto.getRandomValues(new Uint8Array(40))), ...extra,
  };
}

async function sides() {
  const m = await matched();
  const [first, second] = m.a.device.id < m.b.device.id ? [m.a.device, m.b.device] : [m.b.device, m.a.device];
  return { ...m, sideA: first, sideB: second };
}

async function mailboxCount(device: Device): Promise<number> {
  return runInDurableObject(hubOf(device.id), (_hub: DeviceHub, state) =>
    state.storage.sql.exec<{ n: number }>(`SELECT COUNT(*) AS n FROM chat_mailbox`).one().n);
}

describe("match chat", () => {
  it("forwards to a connected match partner with its match id and acks delivered", async () => {
    const m = await sides();
    const a = track(await connect(m.sideA, FEATURES));
    const b = track(await connect(m.sideB, FEATURES));
    const sent = matchChat(m.matchId, "a", 7);
    a.send(sent);
    expect(await b.next()).toEqual({
      type: "frame", channel: "match", matchId: m.matchId, from: "a", seq: 7, sentAt: sent.sentAt, nonce: sent.nonce, ciphertext: sent.ciphertext,
    });
    expect(await a.next()).toEqual({ type: "ack", id: "x7", result: "delivered" });
  });

  it("stores for an offline partner and flushes in order with delivered receipts", async () => {
    const m = await sides();
    await disconnect(await connect(m.sideB, FEATURES));
    const a = track(await connect(m.sideA, FEATURES));
    for (const seq of [1, 2, 3]) {
      a.send(matchChat(m.matchId, "a", seq));
      expect((await a.next()).result).toBe("stored");
    }
    const b = track(await connect(m.sideB, FEATURES));
    expect([(await b.next()).seq, (await b.next()).seq, (await b.next()).seq]).toEqual([1, 2, 3]);
    const receipts = [await a.next(), await a.next(), await a.next()];
    expect(receipts.map((r) => r.result)).toEqual(["delivered", "delivered", "delivered"]);
  });

  it("refuses a non-member, the wrong side, and a pairing frame naming the match", async () => {
    const m = await sides();
    await disconnect(await connect(m.sideB, FEATURES));
    const a = track(await connect(m.sideA, FEATURES));
    a.send(matchChat(m.matchId, "b", 1));
    expect((await a.next()).result).toBe("refused");

    const stranger = track(await connect((await livePair()).owner, FEATURES));
    stranger.send(matchChat(m.matchId, "a", 1));
    expect((await stranger.next()).result).toBe("refused");
    a.send(chat(m.matchId, 0, "owner", 2));
    expect((await a.next()).result).toBe("refused");
    expect(await mailboxCount(m.sideB)).toBe(0);
  });

  it("reports needs_update for a partner that never declared match", async () => {
    const m = await sides();
    const a = track(await connect(m.sideA, FEATURES));
    track(await connect(m.sideB, ["chat"]));
    a.send(matchChat(m.matchId, "a", 1));
    expect((await a.next()).result).toBe("needs_update");
  });

  it("refuses after unmatch, and unmatch purges stored messages with expired receipts", async () => {
    const m = await sides();
    await disconnect(await connect(m.sideB, FEATURES));
    const a = track(await connect(m.sideA, FEATURES));
    a.send(matchChat(m.matchId, "a", 1));
    expect((await a.next()).result).toBe("stored");
    expect(await mailboxCount(m.sideB)).toBe(1);

    await del(m.sideB, `/v1/lfp/matches/${m.matchId}`);
    const after = [await a.next(), await a.next()];
    expect(after).toContainEqual({ type: "ack", id: "x1", result: "expired" });
    expect(after).toContainEqual({ type: "hint", kind: "lfp" });
    expect(await mailboxCount(m.sideB)).toBe(0);

    a.send(matchChat(m.matchId, "a", 2));
    expect((await a.next()).result).toBe("refused");
  });

  it("a block purges stored messages the same way", async () => {
    const m = await sides();
    await disconnect(await connect(m.sideB, FEATURES));
    const a = track(await connect(m.sideA, FEATURES));
    a.send(matchChat(m.matchId, "a", 1));
    expect((await a.next()).result).toBe("stored");
    await post(m.sideB, "/v1/lfp/blocks", { type: "lfp-block", schemaVersion: 1, matchId: m.matchId });
    expect(await mailboxCount(m.sideB)).toBe(0);
    expect((await get(m.sideA, "/v1/lfp/matches")).json.matches[0].endedAt).toBeTypeOf("number");
  });
});
