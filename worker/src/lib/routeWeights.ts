/**
 * Estimated D1 rows written per request, charged against the daily global budget before the route runs. Each
 * weight covers the request's own writes (global counter, nonce on state-changing routes, a device's first-of-day
 * activity marker, per-device quota rows, route rows, index entries) plus one cron write for every row it inserts or
 * updates. `test/route-writes.spec.ts` fails if a weight drops
 * below what one request of that route actually writes, or if a route is missing here.
 */
export const ROUTE_WEIGHTS: Readonly<Record<string, number>> = {
  "GET /v1/health": 2,
  "POST /v1/presence/hello": 8,

  "POST /v1/invitations": 15,
  "GET /v1/invitations/:id": 4,
  "POST /v1/invitations/:id/accept": 11,
  "POST /v1/invitations/:id/consume": 13,
  "POST /v1/invitations/:id/cancel": 8,

  "PUT /v1/backups/:id": 9,
  "GET /v1/backups/:id": 4,
  "DELETE /v1/backups/:id": 7,

  "GET /v1/pairs/:id": 2,
  "POST /v1/pairs/status": 2,
  "POST /v1/pairs/:id/collar-status": 12,
  "POST /v1/pairs/:id/cage-status": 12,
  "POST /v1/pairs/:id/characters": 12,

  "POST /v1/revocations": 12,
  "GET /v1/revocations/:id": 2,

  "POST /v1/catalog/requests": 20,
  "GET /v1/catalog/requests/:id": 2,
  "POST /v1/catalog/requests/:id/upload": 15,
  "POST /v1/catalog/requests/:id/consume": 9,

  "POST /v1/catalog/mailbox/key": 12,
  "POST /v1/catalog/mailbox/key/fetch": 6,
  "POST /v1/catalog/mailbox/upload": 16,
  "POST /v1/catalog/mailbox/status": 4,
  "POST /v1/catalog/mailbox/consume": 11,

  "POST /v1/mailbox/status": 12,

  "POST /v1/pictures/sync": 8,
  "POST /v1/pictures/upload": 8,
  "POST /v1/pictures/fetch": 4,

  "POST /v1/chat-images/upload": 10,
  "POST /v1/chat-images/fetch": 4,

  "POST /v1/lfp/me": 8,
  "PUT /v1/lfp/profile": 16,
  "DELETE /v1/lfp/profile": 10,
  "POST /v1/lfp/profile/refresh": 10,
  "POST /v1/lfp/profile/code": 10,
  "POST /v1/lfp/profile/picture": 12,
  "DELETE /v1/lfp/profile/picture": 10,
  "GET /v1/lfp/pictures/:id": 2,
  "GET /v1/lfp/browse": 4,
  "GET /v1/lfp/code/:id": 4,
  "POST /v1/lfp/likes/:id": 22,
  "DELETE /v1/lfp/likes/:id": 8,
  "GET /v1/lfp/likes": 4,
  "GET /v1/lfp/matches": 4,
  "DELETE /v1/lfp/matches/:id": 14,
  "POST /v1/lfp/blocks": 20,
  "GET /v1/lfp/blocks": 4,
  "DELETE /v1/lfp/blocks/:id": 8,

  "POST /v1/rulebook/key": 12,
  "POST /v1/rulebook/key/fetch": 4,
  "POST /v1/rulebook/upload": 13,
  "POST /v1/rulebook/consume": 11,
};

/** Unknown paths only cost the global counter write. */
export const UNKNOWN_ROUTE_WEIGHT = 1;

// Which segment of each resource's path is a capability id rather than a fixed name.
const ID_SEGMENT: Readonly<Record<string, number>> = { invitations: 2, backups: 2, pairs: 2, revocations: 2, requests: 3 };

export function routeKey(method: string, segments: readonly string[]): string {
  if (segments[1] === "pairs" && segments[2] === "status" && segments.length === 3) return `${method} /v1/pairs/status`;
  if (segments[1] === "lfp") {
    const idAt = segments.length === 4 && ["pictures", "code", "likes", "matches", "blocks"].includes(segments[2] ?? "") ? 3 : -1;
    return `${method} /${segments.map((segment, i) => (i === idAt ? ":id" : segment)).join("/")}`;
  }
  const idAt = segments[1] === "catalog" ? (segments[2] === "requests" ? ID_SEGMENT.requests : -1) : (ID_SEGMENT[segments[1] ?? ""] ?? -1);
  const shape = segments.map((segment, i) => (i === idAt ? ":id" : segment));
  return `${method} /${shape.join("/")}`;
}

export function routeWeight(method: string, segments: readonly string[]): number {
  return ROUTE_WEIGHTS[routeKey(method, segments)] ?? UNKNOWN_ROUTE_WEIGHT;
}
