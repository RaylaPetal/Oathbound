import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { signedFetch } from "./helpers";
import { freshPair, type Pair } from "./mailbox-helpers";
import { consumeItem, publishRulebookKey, uploadItem } from "./rulebook-helpers";

const pairStatus = (p: Pair) => signedFetch(`/v1/pairs/${p.pairIdHash}?epoch=0`, "GET", undefined, p.owner.privateKey, p.ownerId);

describe("rulebook mailbox: channels are independent", () => {
  it("doesn't let a rulebook-push land on the report channel's key", async () => {
    const p = await freshPair();
    const { envelope: reportKey } = await publishRulebookKey(p, "report");
    await publishRulebookKey(p, "rulebook");
    // An Owner-signed rulebook-push naming the report key id is checked against the rulebook channel's key.
    expect((await uploadItem(p, "rulebook", reportKey.receiveKeyId, 1)).r.status).toBe(410);
  });
});

describe("rulebook mailbox: state on the pair status response", () => {
  it("reports neither channel before any key was published", async () => {
    const p = await freshPair();
    expect((await pairStatus(p)).json.rulebookMailbox).toEqual({ rulebook: { exists: false }, report: { exists: false } });
  });

  it("tracks key, waiting and consumed per channel for both members", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishRulebookKey(p, "rulebook");
    await uploadItem(p, "rulebook", key.receiveKeyId, 3);

    const bySub = await signedFetch(`/v1/pairs/${p.pairIdHash}?epoch=0`, "GET", undefined, p.sub.privateKey, p.subId);
    expect(bySub.json.rulebookMailbox.rulebook).toMatchObject({ exists: true, receiveKeyId: key.receiveKeyId, waitingSequence: 3, lastConsumedSequence: null });
    expect(bySub.json.rulebookMailbox.report).toEqual({ exists: false });

    const { nextKey } = await consumeItem(p, "rulebook", 3);
    expect((await pairStatus(p)).json.rulebookMailbox.rulebook).toMatchObject({ receiveKeyId: nextKey.receiveKeyId, waitingSequence: null, lastConsumedSequence: 3 });
  });

  it("stops reporting an item as waiting once it expired", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishRulebookKey(p, "report");
    await uploadItem(p, "report", key.receiveKeyId, 1);
    await env.RELAY_DB.prepare(`UPDATE rulebook_mailboxes SET item_expires_at = 1 WHERE pair_id_hash = ?1`).bind(p.pairIdHash).run();
    expect((await pairStatus(p)).json.rulebookMailbox.report).toMatchObject({ waitingSequence: null });
  });
});

describe("rulebook mailbox: failed uploads don't hold the interval", () => {
  it("lets the sender retry at once after a quota rejection", async () => {
    const p = await freshPair();
    const { envelope: key } = await publishRulebookKey(p, "rulebook");
    const windowStart = Math.floor(Date.now() / 1000 / 3600) * 3600;
    await env.RELAY_DB.prepare(`INSERT INTO quota_counters (scope, window_start, count, bytes) VALUES (?1, ?2, 60, 0)`)
      .bind(`rulebookUploadBytes:${p.ownerId}`, windowStart)
      .run();
    expect((await uploadItem(p, "rulebook", key.receiveKeyId, 1)).r.status).toBe(429);

    await env.RELAY_DB.prepare(`DELETE FROM quota_counters WHERE scope = ?1`).bind(`rulebookUploadBytes:${p.ownerId}`).run();
    expect((await uploadItem(p, "rulebook", key.receiveKeyId, 1)).r.status).toBe(200);
  });
});

describe("pair status: relay activity", () => {
  it("is null until the cron ran, then carries the latest count", async () => {
    const p = await freshPair();
    await env.RELAY_DB.prepare(`DELETE FROM relay_stats`).run();
    expect((await pairStatus(p)).json).toMatchObject({ relayActiveDevices: null, relayActiveDevicesAt: null });

    await env.RELAY_DB.prepare(`INSERT INTO relay_stats (id, active_devices, computed_at) VALUES (1, 42, 1700000000)`).run();
    expect((await pairStatus(p)).json).toMatchObject({ relayActiveDevices: 42, relayActiveDevicesAt: 1700000000 });
  });
});
