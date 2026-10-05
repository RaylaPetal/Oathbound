/**
 * Estimated D1 rows written per request, charged against the daily global budget before the route runs. Each
 * weight covers the request's own writes (global counter, nonce, per-device quota rows, route rows, index entries)
 * plus one cron write for every row it inserts or updates. `test/route-writes.spec.ts` fails if a weight drops
 * below what one request of that route actually writes, or if a route is missing here.
 */
export const ROUTE_WEIGHTS: Readonly<Record<string, number>> = {
  "GET /v1/health": 2,

  "POST /v1/invitations": 15,
  "GET /v1/invitations/:id": 4,
  "POST /v1/invitations/:id/accept": 11,
  "POST /v1/invitations/:id/consume": 11,
  "POST /v1/invitations/:id/cancel": 8,

  "PUT /v1/backups/:id": 9,
  "GET /v1/backups/:id": 4,
  "DELETE /v1/backups/:id": 7,

  "GET /v1/pairs/:id": 4,
  "POST /v1/pairs/:id/collar-status": 8,

  "POST /v1/revocations": 12,
  "GET /v1/revocations/:id": 4,

  "POST /v1/catalog/requests": 18,
  "GET /v1/catalog/requests/:id": 2,
  "POST /v1/catalog/requests/:id/upload": 15,
  "POST /v1/catalog/requests/:id/consume": 9,

  "POST /v1/catalog/mailbox/key": 10,
  "POST /v1/catalog/mailbox/key/fetch": 6,
  "POST /v1/catalog/mailbox/upload": 16,
  "POST /v1/catalog/mailbox/status": 6,
  "POST /v1/catalog/mailbox/consume": 9,

  "POST /v1/rulebook/key": 10,
  "POST /v1/rulebook/key/fetch": 6,
  "POST /v1/rulebook/upload": 11,
  "POST /v1/rulebook/consume": 9,
};

/** Unknown paths only cost the global counter write. */
export const UNKNOWN_ROUTE_WEIGHT = 1;

// Which segment of each resource's path is a capability id rather than a fixed name.
const ID_SEGMENT: Readonly<Record<string, number>> = { invitations: 2, backups: 2, pairs: 2, revocations: 2, requests: 3 };

export function routeKey(method: string, segments: readonly string[]): string {
  const idAt = segments[1] === "catalog" ? (segments[2] === "requests" ? ID_SEGMENT.requests : -1) : (ID_SEGMENT[segments[1] ?? ""] ?? -1);
  const shape = segments.map((segment, i) => (i === idAt ? ":id" : segment));
  return `${method} /${shape.join("/")}`;
}

export function routeWeight(method: string, segments: readonly string[]): number {
  return ROUTE_WEIGHTS[routeKey(method, segments)] ?? UNKNOWN_ROUTE_WEIGHT;
}
