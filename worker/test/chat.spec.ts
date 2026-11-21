import { env, runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { afterEach, describe, expect, it } from "vitest";
import type { DeviceHub } from "../src/hub/DeviceHub";
import { QUOTA_LIMITS } from "../src/lib/quotas";
import { chat, connect, frame, livePair, revoke, type Device, type TestSocket } from "./socket-helpers";

const CHAT = ["chat"];
const open: TestSocket[] = [];
const track = (s: TestSocket) => { open.push(s); return s; };
afterEach(() => { for (const s of open.splice(0)) s.close(); });

const hubOf = (id: string) => env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(id));

async function disconnect(s: TestSocket): Promise<void> {
  s.close();
  await new Promise((r) => setTimeout(r, 50));
}

/** Connects once with chat declared, then drops, so the device counts as chat-capable but away. */
async function awayWithChat(device: Device): Promise<void> {
  await disconnect(await connect(device, CHAT));
}

async function mailboxCount(device: Device): Promise<number> {
  return runInDurableObject(hubOf(device.id), (_hub: DeviceHub, state) =>
    state.storage.sql.exec<{ n: number }>(`SELECT COUNT(*) AS n FROM chat_mailbox`).one().n);
}

describe("chat delivery", () => {
  it("forwards chat to a connected partner with its channel and acks delivered", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner, CHAT));
    const sub = track(await connect(p.sub, CHAT));
    const sent = chat(p.pairIdHash, 0, "owner", 5);
    owner.send(sent);

    expect(await sub.next()).toEqual({
      type: "frame", channel: "chat", pairIdHash: sent.pairIdHash, pairEpoch: 0, from: "owner", seq: 5,
      sentAt: sent.sentAt, nonce: sent.nonce, ciphertext: sent.ciphertext,
    });
    expect(await owner.next()).toEqual({ type: "ack", id: "c5", result: "delivered" });
  });

  it("reports needs_update and stores nothing for a partner that never declared chat", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner, CHAT));
    const sub = track(await connect(p.sub));
    owner.send(chat(p.pairIdHash, 0, "owner", 1));
    expect((await owner.next()).result).toBe("needs_update");
    expect(await sub.maybeNext()).toBeNull();
    expect(await mailboxCount(p.sub)).toBe(0);
  });

  it("refuses chat from a non-member, for the wrong side, or dated in the future", async () => {
    const p = await livePair();
    await awayWithChat(p.sub);
    const owner = track(await connect(p.owner, CHAT));
    owner.send(chat(p.pairIdHash, 0, "sub", 1));
    expect((await owner.next()).result).toBe("refused");
    owner.send(chat(p.pairIdHash, 0, "owner", 2, { sentAt: Math.floor(Date.now() / 1000) + 3600 }));
    expect((await owner.next()).result).toBe("refused");

    const stranger = track(await connect((await livePair()).owner, CHAT));
    stranger.send(chat(p.pairIdHash, 0, "owner", 1));
    expect((await stranger.next()).result).toBe("refused");
    expect(await mailboxCount(p.sub)).toBe(0);
  });

  it("accepts chat sent long before the 2-minute live window", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner, CHAT));
    const sub = track(await connect(p.sub, CHAT));
    owner.send(chat(p.pairIdHash, 0, "owner", 1, { sentAt: Math.floor(Date.now() / 1000) - 3600 }));
    expect((await owner.next()).result).toBe("delivered");
    expect((await sub.next()).channel).toBe("chat");
  });
});

describe("chat for an offline partner", () => {
  it("stores chat and delivers it in order on connect, with delivered receipts", async () => {
    const p = await livePair();
    await awayWithChat(p.sub);
    const owner = track(await connect(p.owner, CHAT));
    for (let i = 1; i <= 3; i++) owner.send(chat(p.pairIdHash, 0, "owner", i));
    for (let i = 1; i <= 3; i++) expect(await owner.next()).toEqual({ type: "ack", id: `c${i}`, result: "stored" });

    const sub = track(await connect(p.sub, CHAT));
    expect([(await sub.next()).seq, (await sub.next()).seq, (await sub.next()).seq]).toEqual([1, 2, 3]);
    const receipts = [await owner.next(), await owner.next(), await owner.next()];
    expect(receipts).toEqual(expect.arrayContaining([1, 2, 3].map((i) => ({ type: "ack", id: `c${i}`, result: "delivered" }))));
    expect(await mailboxCount(p.sub)).toBe(0);
  });

  it("keeps a receipt for a sender who went offline and sends it when they reconnect", async () => {
    const p = await livePair();
    await awayWithChat(p.sub);
    const owner = await connect(p.owner, CHAT);
    owner.send(chat(p.pairIdHash, 0, "owner", 1));
    expect((await owner.next()).result).toBe("stored");
    await disconnect(owner);

    const sub = track(await connect(p.sub, CHAT));
    expect((await sub.next()).seq).toBe(1);
    await new Promise((r) => setTimeout(r, 50));

    const back = track(await connect(p.owner, CHAT));
    expect(await back.next()).toEqual({ type: "ack", id: "c1", result: "delivered" });
    expect(await back.maybeNext()).toBeNull();
  });

  it("refuses with mailbox_full at the cap and keeps what's stored", async () => {
    const p = await livePair();
    await awayWithChat(p.sub);
    await runInDurableObject(hubOf(p.sub.id), (_hub: DeviceHub, state) => {
      for (let i = 0; i < 200; i++) {
        state.storage.sql.exec(
          `INSERT INTO chat_mailbox (sender, message_id, pair_id_hash, pair_epoch, frame, expires_at) VALUES (?, ?, ?, 0, '{}', ?)`,
          p.owner.id, `x${i}`, p.pairIdHash, Math.floor(Date.now() / 1000) + 600);
      }
    });
    const owner = track(await connect(p.owner, CHAT));
    owner.send(chat(p.pairIdHash, 0, "owner", 1));
    expect((await owner.next()).result).toBe("mailbox_full");
    expect(await mailboxCount(p.sub)).toBe(200);
  });

  it("expires stored chat after its TTL and tells the sender", async () => {
    const p = await livePair();
    await awayWithChat(p.sub);
    const owner = track(await connect(p.owner, CHAT));
    owner.send(chat(p.pairIdHash, 0, "owner", 1));
    expect((await owner.next()).result).toBe("stored");

    const subHub = hubOf(p.sub.id);
    await runInDurableObject(subHub, (_hub: DeviceHub, state) => {
      state.storage.sql.exec(`UPDATE chat_mailbox SET expires_at = 0`);
    });
    await runDurableObjectAlarm(subHub);
    expect(await owner.next()).toEqual({ type: "ack", id: "c1", result: "expired" });

    const sub = track(await connect(p.sub, CHAT));
    expect(await sub.maybeNext()).toBeNull();
  });

  it("drops waiting chat on both sides when the pairing is revoked", async () => {
    const p = await livePair();
    await awayWithChat(p.sub);
    const owner = track(await connect(p.owner, CHAT));
    owner.send(chat(p.pairIdHash, 0, "owner", 1));
    expect((await owner.next()).result).toBe("stored");

    expect((await revoke(p.pairIdHash, 0, p.sub)).status).toBe(200);
    const messages = [await owner.next(), await owner.next()];
    expect(messages).toEqual(expect.arrayContaining([{ type: "ack", id: "c1", result: "expired" }]));
    expect(messages.some((m) => m.type === "hint")).toBe(true);
    expect(await mailboxCount(p.sub)).toBe(0);
  });
});

describe("chat limits and budget", () => {
  it("refuses chat over its own burst limit while live frames still forward", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner, CHAT));
    const sub = track(await connect(p.sub, CHAT));
    for (let i = 1; i <= 21; i++) owner.send(chat(p.pairIdHash, 0, "owner", i));
    const results: string[] = [];
    for (let i = 1; i <= 21; i++) results.push((await owner.next()).result as string);
    expect(results.filter((r) => r === "delivered")).toHaveLength(20);
    expect(results[20]).toBe("over_limit");
    for (let i = 0; i < 20; i++) await sub.next();

    owner.send(frame(p.pairIdHash, 0, "owner", 1));
    expect((await owner.next()).result).toBe("delivered");
    expect((await sub.next()).seq).toBe(1);
  });

  it("refuses chat once the chat budget is spent while a safeword frame still goes through", async () => {
    const p = await livePair();
    const owner = track(await connect(p.owner, CHAT));
    const sub = track(await connect(p.sub, CHAT));
    const today = Math.floor(Date.now() / 1000 / 86400) * 86400;
    await env.RELAY_DB.prepare(
      `INSERT INTO quota_counters (scope, window_start, count, bytes) VALUES (?1, ?2, ?3, 0)
       ON CONFLICT (scope, window_start) DO UPDATE SET count = excluded.count`,
    ).bind("globalDailyChat:all", today, QUOTA_LIMITS.globalDailyChat.maxCount).run();
    try {
      sub.send(chat(p.pairIdHash, 0, "sub", 1));
      expect((await sub.next()).result).toBe("over_budget");
      sub.send(frame(p.pairIdHash, 0, "sub", 1, { safety: true }));
      expect((await sub.next()).result).toBe("delivered");
      expect((await owner.next()).seq).toBe(1);
    } finally {
      await env.RELAY_DB.prepare(`DELETE FROM quota_counters WHERE scope = ?1`).bind("globalDailyChat:all").run();
    }
  });
});
