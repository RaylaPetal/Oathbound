// Workers Paid ($5/month) included usage, verified 2026-11-14 against Cloudflare's pricing pages:
// Workers 10M requests; Durable Objects 1M requests (+$0.15/M; incoming WebSocket messages bill 20:1, outgoing and
// protocol pings are free) and 400k GB-s (idle hibernatable objects aren't billed); D1 and DO SQLite 50M rows
// written each (+$1/M).
const days = 30;
const included = { workerRequests: 10_000_000, doRequests: 1_000_000, d1Writes: 50_000_000, doGbSeconds: 400_000 };

// Daily budgets the relay enforces (lib/quotas.ts). Work+safety are in D1 rows written; live is in live units
// (socket opens, forwarded frames, hints).
const budgets = { work: 1_200_000, safety: 200_000, live: 250_000 };

const profiles = [100, 1_000, 5_000];
const perActiveClientDay = {
  socketOpens: 6,
  framesSent: 150,
  hints: 10,
  pairStatusPolls: 48,
  otherSignedRequests: 20,
  d1RowsPerOtherRequest: 6,
};
const activeShare = 0.5;
const activeMsPerEvent = 30;
const gbPerObject = 0.128;

console.log("clients,worker-req/mo,do-req/mo,do-gb-s/mo,d1-writes/mo,live-units/day,overage-usd/mo,fits-live-budget");
for (const clients of profiles) {
  const active = clients * activeShare;
  const p = perActiveClientDay;
  const liveUnitsDay = active * (p.socketOpens + p.framesSent + p.hints);
  const workerReq = active * (p.socketOpens + p.pairStatusPolls + p.otherSignedRequests) * days;
  // A frame: 1/20 for the incoming message, 1 for the hub-to-hub RPC. Opens and hints: 1 each.
  const doReq = active * (p.socketOpens + p.hints + p.framesSent * (1 / 20 + 1)) * days;
  const doEvents = active * (p.socketOpens + p.hints + p.framesSent * 2) * days;
  const gbSeconds = doEvents * (activeMsPerEvent / 1000) * gbPerObject;
  // Live units are charged by sampling into D1 (1 in 16 writes one row).
  const d1Writes = active * (p.otherSignedRequests * p.d1RowsPerOtherRequest + (p.socketOpens + p.framesSent + p.hints) / 16) * days;
  const overage =
    Math.max(0, workerReq - included.workerRequests) / 1e6 * 0.30 +
    Math.max(0, doReq - included.doRequests) / 1e6 * 0.15 +
    Math.max(0, gbSeconds - included.doGbSeconds) / 1e6 * 12.5 +
    Math.max(0, d1Writes - included.d1Writes) / 1e6 * 1.0;
  console.log(`${clients},${Math.round(workerReq)},${Math.round(doReq)},${Math.round(gbSeconds)},${Math.round(d1Writes)},${Math.round(liveUnitsDay)},${overage.toFixed(2)},${liveUnitsDay <= budgets.live}`);
}

const monthOf = (daily) => daily * 31;
console.log(`budgets: work+safety ${monthOf(budgets.work + budgets.safety)} D1 rows/31-day month (included ${included.d1Writes}); ` +
  `live ${monthOf(budgets.live)} units/month (worst case ~${(monthOf(budgets.live) * 1.05 / 1e6).toFixed(1)}M DO requests, ` +
  `~$${(Math.max(0, monthOf(budgets.live) * 1.05 - included.doRequests) / 1e6 * 0.15).toFixed(2)} over the included DO requests)`);
