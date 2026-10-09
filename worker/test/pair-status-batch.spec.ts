import { describe, expect, it } from "vitest";
import { signedFetch } from "./helpers";
import { freshPair } from "./mailbox-helpers";

const batch = (pairs: { pairIdHash: string; pairEpoch: number }[]) => ({ type: "pair-status-batch-request", schemaVersion: 1, pairs });

describe("POST /v1/pairs/status", () => {
  it("answers each of the caller's pairings exactly as the single lookup does", async () => {
    const a = await freshPair();
    const single = await signedFetch(`/v1/pairs/${a.pairIdHash}?epoch=0`, "GET", undefined, a.owner.privateKey, a.ownerId);
    const r = await signedFetch("/v1/pairs/status", "POST", batch([{ pairIdHash: a.pairIdHash, pairEpoch: 0 }]), a.owner.privateKey, a.ownerId);
    expect(r.status).toBe(200);
    expect(r.json.type).toBe("pair-status-batch");
    expect(r.json.pairs).toEqual([{ pairIdHash: a.pairIdHash, pairEpoch: 0, pair: single.json }]);
  });

  it("rejects only the pairings the caller isn't a member of", async () => {
    const mine = await freshPair();
    const other = await freshPair();
    const r = await signedFetch(
      "/v1/pairs/status",
      "POST",
      batch([
        { pairIdHash: mine.pairIdHash, pairEpoch: 0 },
        { pairIdHash: other.pairIdHash, pairEpoch: 0 },
        { pairIdHash: mine.pairIdHash, pairEpoch: 7 },
      ]),
      mine.sub.privateKey,
      mine.subId,
    );
    expect(r.status).toBe(200);
    expect(r.json.pairs[0].pair.pairIdHash).toBe(mine.pairIdHash);
    expect(r.json.pairs[1]).toEqual({ pairIdHash: other.pairIdHash, pairEpoch: 0, error: "unauthorized" });
    expect(r.json.pairs[2]).toEqual({ pairIdHash: mine.pairIdHash, pairEpoch: 7, error: "unauthorized" });
  });

  it("refuses an empty or oversized batch", async () => {
    const p = await freshPair();
    const ref = { pairIdHash: p.pairIdHash, pairEpoch: 0 };
    const empty = await signedFetch("/v1/pairs/status", "POST", batch([]), p.owner.privateKey, p.ownerId);
    expect(empty.status).toBe(400);
    const oversized = await signedFetch("/v1/pairs/status", "POST", batch(Array.from({ length: 33 }, () => ref)), p.owner.privateKey, p.ownerId);
    expect(oversized.status).toBe(400);
  });
});
